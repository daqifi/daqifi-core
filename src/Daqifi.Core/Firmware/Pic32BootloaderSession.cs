using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device.Discovery;
using Microsoft.Extensions.Logging;

namespace Daqifi.Core.Firmware;

/// <summary>
/// The low-level HID conversation with the PIC32 bootloader: enumerate and connect to the
/// bootloader device, read its version, erase, program and CRC-verify flash, and jump back to the
/// application. Every operation is a single bootloader exchange (with the configured transient
/// retry); the ordering, state transitions and failure handling belong to
/// <see cref="Pic32FirmwareUpdater"/>.
/// </summary>
internal sealed class Pic32BootloaderSession
{
    private readonly FirmwareUpdateContext _context;
    private readonly IHidTransport _hidTransport;
    private readonly IBootloaderProtocol _bootloaderProtocol;
    private readonly IHidDeviceEnumerator _hidDeviceEnumerator;
    private readonly IUsbLocationProvider _usbLocationProvider;

    private int _bootloaderPollAttempts;
    private Exception? _lastBootloaderEnumerationError;
    private string? _targetBootloaderDevicePath;
    private string? _targetBootloaderLocationKey;

    internal Pic32BootloaderSession(
        FirmwareUpdateContext context,
        IHidTransport hidTransport,
        IBootloaderProtocol bootloaderProtocol,
        IHidDeviceEnumerator hidDeviceEnumerator,
        IUsbLocationProvider usbLocationProvider)
    {
        _context = context;
        _hidTransport = hidTransport;
        _bootloaderProtocol = bootloaderProtocol;
        _hidDeviceEnumerator = hidDeviceEnumerator;
        _usbLocationProvider = usbLocationProvider;
    }

    internal bool IsConnected => _hidTransport.IsConnected;

    private ILogger Logger => _context.Logger;

    private FirmwareUpdateServiceOptions Options => _context.Options;

    /// <summary>
    /// Parses and validates the HEX image before any device I/O, returning the programmable
    /// records, the CRC regions used by the post-programming verify pass and the total byte count.
    /// </summary>
    internal (IReadOnlyList<byte[]> HexRecords, IReadOnlyList<FlashCrcRegion> CrcRegions, long TotalBytes)
        PrepareHexImage(string[] hexLines)
    {
        var hexRecords = _bootloaderProtocol.ParseHexFile(hexLines);
        var totalBytes = hexRecords.Sum(record => (long)record.Length);
        if (totalBytes <= 0)
        {
            throw new InvalidDataException("Firmware HEX file did not contain any writable records.");
        }

        // Computed up front (alongside parsing) so the post-programming Verifying
        // state can checksum exactly the bytes we programmed via the bootloader
        // READ_CRC command. See VerifyFlashContentsAsync.
        var crcRegions = _bootloaderProtocol.ComputeCrcRegions(hexLines);

        return (hexRecords, crcRegions, totalBytes);
    }

    /// <summary>
    /// Clears the per-run bootloader poll counters and targeting state so a
    /// <see cref="FirmwareUpdateState.WaitingForBootloader"/> timeout message describes only the
    /// current run.
    /// </summary>
    internal void ResetTargetingState(string? targetDevicePath = null, string? targetLocationKey = null)
    {
        _bootloaderPollAttempts = 0;
        _lastBootloaderEnumerationError = null;
        _targetBootloaderDevicePath = targetDevicePath;
        _targetBootloaderLocationKey = targetLocationKey;
    }

    /// <summary>
    /// Records the target requested for this run so a
    /// <see cref="FirmwareUpdateState.WaitingForBootloader"/> timeout can name it.
    /// </summary>
    internal void SetRequestedTarget(string? targetDevicePath, string? targetLocationKey)
    {
        _targetBootloaderDevicePath = targetDevicePath;
        _targetBootloaderLocationKey = targetLocationKey;
    }

    /// <summary>
    /// Extra detail appended to a <see cref="FirmwareUpdateState.WaitingForBootloader"/> timeout,
    /// naming the VID/PID searched, the poll attempts made, any requested target and the last
    /// enumeration error.
    /// </summary>
    internal string DescribeBootloaderSearch()
    {
        var details =
            $"No matching HID bootloader device was enumerated for VID=0x{Options.BootloaderVendorId:X4}, " +
            $"PID=0x{Options.BootloaderProductId:X4} after {_bootloaderPollAttempts} poll attempt(s).";

        if (_targetBootloaderDevicePath != null)
        {
            details += $" Target device path requested: {_targetBootloaderDevicePath}.";
        }

        if (_targetBootloaderLocationKey != null)
        {
            details += $" Target location key requested: {_targetBootloaderLocationKey}.";
        }

        if (_lastBootloaderEnumerationError == null)
        {
            return details;
        }

        var errorSummary = FirmwareUpdateContext.FormatExceptionSummary(_lastBootloaderEnumerationError);
        return $"{details} Last HID enumeration error: {errorSummary}.";
    }

    /// <summary>
    /// Polls HID enumeration until a bootloader matching the requested target appears. Bounded by
    /// the caller's state timeout.
    /// </summary>
    internal async Task<HidDeviceInfo> WaitForBootloaderDeviceAsync(
        string? targetDevicePath,
        string? targetLocationKey,
        CancellationToken cancellationToken)
    {
        // A device path's physical location can't change while it stays enumerated, so caching
        // per call (across poll iterations, not across separate update runs) avoids re-issuing a
        // WMI query for the same candidate on every poll while targeting by location.
        var locationCache = new Dictionary<string, string?>(StringComparer.Ordinal);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _bootloaderPollAttempts++;

            IReadOnlyList<HidDeviceInfo> devices;
            try
            {
                devices = await _hidDeviceEnumerator
                    .EnumerateAsync(Options.BootloaderVendorId, Options.BootloaderProductId, cancellationToken)
                    .ConfigureAwait(false);
                _lastBootloaderEnumerationError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _lastBootloaderEnumerationError = ex;
                throw new InvalidOperationException(
                    $"HID enumeration failed while searching for bootloader device " +
                    $"VID=0x{Options.BootloaderVendorId:X4}, PID=0x{Options.BootloaderProductId:X4} " +
                    $"on poll attempt {_bootloaderPollAttempts}.",
                    ex);
            }

            // Multiple identical bootloaders can be enumerated at once; when a specific one was
            // requested, match it by path (the rest stay held by the caller). Otherwise, if a
            // location key was requested, resolve each candidate's physical-location key and match
            // on that — this is what lets a caller target the bootloader a device rebooted INTO,
            // before its device path exists, using the location it observed while the device was
            // still in serial/app mode. Otherwise take the first match, preserving the
            // single-device behavior.
            // Ordinal (case-sensitive): a device path is an OS identifier and, in this flow, comes from
            // the same in-process HID enumeration (via IHidPlatform) the caller used to obtain targetDevicePath.
            var match = targetDevicePath != null
                ? devices.FirstOrDefault(d =>
                    string.Equals(d.DevicePath, targetDevicePath, StringComparison.Ordinal))
                : targetLocationKey != null
                    ? devices.FirstOrDefault(d =>
                        string.Equals(
                            ResolveLocationCached(d.DevicePath, locationCache),
                            targetLocationKey,
                            StringComparison.Ordinal))
                    : devices.FirstOrDefault();
            if (match != null)
            {
                return match;
            }

            await Task.Delay(Options.PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private string? ResolveLocationCached(string devicePath, Dictionary<string, string?> cache)
    {
        if (cache.TryGetValue(devicePath, out var cached))
        {
            return cached;
        }

        var resolved = _usbLocationProvider.GetLocationKey(devicePath);
        cache[devicePath] = resolved;
        return resolved;
    }

    internal async Task ConnectWithRetryAsync(
        HidDeviceInfo bootloaderDevice,
        string? targetDevicePath,
        string? targetLocationKey,
        CancellationToken cancellationToken)
    {
        await _context.ExecuteWithRetryAsync(
            "connect HID bootloader",
            Options.HidConnectRetryCount,
            Options.HidConnectRetryDelay,
            async ct =>
            {
                if (_hidTransport.IsConnected)
                {
                    await _hidTransport.DisconnectAsync().ConfigureAwait(false);
                }

                // When a specific device was requested (by path or by location, several identical
                // bootloaders present), connect to that exact device by path — bootloaderDevice was
                // already matched on the requested criterion in WaitForBootloaderDeviceAsync.
                // Otherwise fall back to VID/PID(+serial) first-match for the single-device case.
                if (targetDevicePath != null || targetLocationKey != null)
                {
                    await _hidTransport
                        .ConnectByPathAsync(bootloaderDevice.DevicePath, ct)
                        .ConfigureAwait(false);
                }
                else
                {
                    await _hidTransport.ConnectAsync(
                        Options.BootloaderVendorId,
                        Options.BootloaderProductId,
                        bootloaderDevice.SerialNumber,
                        ct).ConfigureAwait(false);
                }
            },
            ex => ex is IOException or TimeoutException or InvalidOperationException,
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<string> RequestVersionAsync(CancellationToken cancellationToken)
    {
        await _hidTransport
            .WriteAsync(_bootloaderProtocol.CreateRequestVersionMessage(), cancellationToken)
            .ConfigureAwait(false);

        var response = await _hidTransport
            .ReadAsync(Options.BootloaderResponseTimeout, cancellationToken)
            .ConfigureAwait(false);

        var version = _bootloaderProtocol.DecodeVersionResponse(response);
        if (string.Equals(version, "Error", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Bootloader returned an invalid version response.");
        }

        return version;
    }

    internal async Task EraseFlashWithRetryAsync(CancellationToken cancellationToken)
    {
        await _context.ExecuteWithRetryAsync(
            "erase flash",
            Options.FlashWriteRetryCount,
            Options.FlashWriteRetryDelay,
            async ct =>
            {
                await _hidTransport
                    .WriteAsync(_bootloaderProtocol.CreateEraseFlashMessage(), ct)
                    .ConfigureAwait(false);

                var response = await _hidTransport
                    .ReadAsync(Options.BootloaderResponseTimeout, ct)
                    .ConfigureAwait(false);

                if (!_bootloaderProtocol.DecodeEraseFlashResponse(response))
                {
                    throw new InvalidDataException("Bootloader erase acknowledgment was invalid.");
                }
            },
            ex => ex is IOException or TimeoutException or InvalidDataException,
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task ProgramFlashAsync(
        IReadOnlyList<byte[]> hexRecords,
        long totalBytes,
        IProgress<FirmwareUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        long bytesWritten = 0;
        for (var index = 0; index < hexRecords.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var record = hexRecords[index];
            await _context.ExecuteWithRetryAsync(
                $"program flash record {index + 1}",
                Options.FlashWriteRetryCount,
                Options.FlashWriteRetryDelay,
                async ct =>
                {
                    var message = _bootloaderProtocol.CreateProgramFlashMessage(record);
                    await _hidTransport.WriteAsync(message, ct).ConfigureAwait(false);

                    var response = await _hidTransport
                        .ReadAsync(Options.BootloaderResponseTimeout, ct)
                        .ConfigureAwait(false);

                    if (!_bootloaderProtocol.DecodeProgramFlashResponse(response))
                    {
                        throw new InvalidDataException(
                            $"Bootloader program acknowledgment was invalid for record {index + 1}.");
                    }
                },
                ex => ex is IOException or TimeoutException or InvalidDataException,
                cancellationToken).ConfigureAwait(false);

            bytesWritten += record.Length;
            var completion = totalBytes <= 0 ? 90 : 20 + (bytesWritten / (double)totalBytes * 70);
            _context.ReportProgress(
                progress,
                FirmwareUpdateState.Programming,
                completion,
                $"Programming record {index + 1} of {hexRecords.Count}",
                bytesWritten,
                totalBytes);
        }
    }

    internal async Task VerifyFlashContentsAsync(
        IReadOnlyList<FlashCrcRegion> crcRegions,
        IProgress<FirmwareUpdateProgress>? progress,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        if (crcRegions.Count == 0)
        {
            // No flashable regions to verify (degenerate/empty image). Fall back
            // to confirming the bootloader is still responsive so we never jump
            // to an application we couldn't reach over HID.
            var version = await RequestVersionAsync(cancellationToken).ConfigureAwait(false);
            Logger.LogInformation(
                "No flash CRC regions to verify; confirmed bootloader liveness: {BootloaderVersion}.",
                version);
            return;
        }

        for (var index = 0; index < crcRegions.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var region = crcRegions[index];
            await _context.ExecuteWithRetryAsync(
                $"read flash CRC for region {index + 1} at 0x{region.Address:X8}",
                Options.FlashWriteRetryCount,
                Options.FlashWriteRetryDelay,
                async ct =>
                {
                    await _hidTransport
                        .WriteAsync(_bootloaderProtocol.CreateReadCrcMessage(region.Address, region.Length), ct)
                        .ConfigureAwait(false);

                    var response = await _hidTransport
                        .ReadAsync(Options.BootloaderResponseTimeout, ct)
                        .ConfigureAwait(false);

                    ushort actualCrc;
                    try
                    {
                        actualCrc = _bootloaderProtocol.DecodeReadCrcResponse(response);
                    }
                    catch (InvalidDataException ex)
                    {
                        // A malformed / framing-corrupt READ_CRC frame is a
                        // transport-level fault, not evidence of bad flash. Surface
                        // it as transient (like a HID read error) so a one-off USB
                        // glitch is retried rather than failing the whole update —
                        // consistent with how the erase/program steps treat
                        // InvalidDataException.
                        throw new IOException(
                            $"READ_CRC response for region {index + 1} at 0x{region.Address:X8} was malformed; " +
                            "treating as a transient transport fault.",
                            ex);
                    }

                    if (actualCrc != region.ExpectedCrc)
                    {
                        // A genuine CRC mismatch is deterministic — the flash does
                        // not match the image. Throw InvalidDataException, which the
                        // retry predicate excludes, so verification fails fast into
                        // the failure/cleanup path rather than masking a bad flash
                        // behind retries.
                        throw new InvalidDataException(
                            $"Flash CRC mismatch in region {index + 1} at 0x{region.Address:X8} " +
                            $"(length {region.Length}): expected 0x{region.ExpectedCrc:X4}, " +
                            $"device reported 0x{actualCrc:X4}.");
                    }
                },
                // Retry transient transport faults: HID read errors, timeouts, and
                // malformed frames (wrapped as IOException above). A CRC mismatch
                // throws InvalidDataException, which is intentionally NOT retried —
                // it is deterministic and must fail fast into the failure/cleanup path.
                ex => ex is IOException or TimeoutException,
                cancellationToken).ConfigureAwait(false);

            // Verifying occupies the 92→94% band (JumpingToApp starts at 95%).
            var verifyPercent = 92 + ((index + 1) / (double)crcRegions.Count * 2);
            _context.ReportProgress(
                progress,
                FirmwareUpdateState.Verifying,
                verifyPercent,
                $"Verified flash CRC region {index + 1} of {crcRegions.Count}",
                totalBytes,
                totalBytes);
        }

        Logger.LogInformation(
            "Flash CRC verification passed for {RegionCount} region(s).",
            crcRegions.Count);
    }

    /// <summary>
    /// Writes the <c>JMP_TO_APP</c> command. Touches no flash — the bootloader simply hands control
    /// to the application image and the device re-enumerates as USB CDC.
    /// </summary>
    internal Task SendJumpToApplicationAsync(CancellationToken cancellationToken)
        => _hidTransport.WriteAsync(_bootloaderProtocol.CreateJumpToApplicationMessage(), cancellationToken);

    internal async Task SafeDisconnectAsync()
    {
        if (!_hidTransport.IsConnected)
        {
            return;
        }

        try
        {
            await _hidTransport.DisconnectAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to disconnect HID transport during cleanup.");
        }
    }
}
