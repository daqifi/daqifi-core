using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Discovery;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Daqifi.Core.Firmware;

/// <summary>
/// Default firmware update orchestration service for PIC32 and WiFi update flows.
/// </summary>
/// <remarks>
/// This type is the public facade. The two independent flows live behind it in
/// <see cref="Pic32FirmwareUpdater"/> (bootloader connect/erase/program/verify/jump and the
/// standalone bootloader diagnostics) and <see cref="WifiModuleUpdater"/> (WINC version probe and
/// external flash-tool orchestration), over the shared state machine, progress and retry plumbing
/// in <see cref="FirmwareUpdateContext"/>. The facade owns argument validation, the operation lock
/// that serializes all device I/O, and disposal.
/// </remarks>
public sealed class FirmwareUpdateService : IFirmwareUpdateService, IPic32BootloaderDiagnostics, IDisposable
{
    private readonly SemaphoreSlim _operationLock = new(1, 1);

    // Async-context flag set true while the current logical flow holds
    // _operationLock. CheckWifiFirmwareStatusAsync uses this to detect
    // re-entrancy from progress / state-change callbacks fired by an
    // in-flight UpdateFirmwareAsync / UpdateWifiModuleAsync (which run
    // synchronously while the lock is held). Without this guard the
    // status probe would deadlock waiting for the lock its own caller
    // holds, since SemaphoreSlim is not re-entrant. AsyncLocal flows
    // through await resumptions on different threads.
    private readonly AsyncLocal<bool> _isInsideOperation = new();
    private readonly FirmwareUpdateContext _context;
    private readonly Pic32BootloaderSession _bootloaderSession;
    private readonly Pic32FirmwareUpdater _pic32Updater;
    private readonly WifiModuleUpdater _wifiUpdater;

    private bool _disposed;

    /// <summary>
    /// Initializes a new firmware update service. The optional <paramref name="logger"/> defaults to
    /// a no-op logger (<see cref="NullLogger{T}.Instance"/>) when omitted, so the service is usable
    /// without wiring up logging. <paramref name="usbLocationProvider"/> resolves an enumerated
    /// bootloader's USB physical-location key when targeting by <c>targetLocationKey</c>; when null,
    /// a platform-default provider is used (Windows → WMI, others → no-op fallback, which makes
    /// location-key targeting a no-op).
    /// </summary>
    public FirmwareUpdateService(
        IHidTransport hidTransport,
        IFirmwareDownloadService firmwareDownloadService,
        IExternalProcessRunner externalProcessRunner,
        ILogger<FirmwareUpdateService>? logger = null,
        IBootloaderProtocol? bootloaderProtocol = null,
        IHidDeviceEnumerator? hidDeviceEnumerator = null,
        FirmwareUpdateServiceOptions? options = null,
        IUsbLocationProvider? usbLocationProvider = null)
    {
        ArgumentNullException.ThrowIfNull(hidTransport);
        ArgumentNullException.ThrowIfNull(firmwareDownloadService);
        ArgumentNullException.ThrowIfNull(externalProcessRunner);

        FirmwareDownloadService = firmwareDownloadService;

        var resolvedOptions = options ?? new FirmwareUpdateServiceOptions();
        resolvedOptions.Validate();

        _context = new FirmwareUpdateContext(
            this,
            logger ?? NullLogger<FirmwareUpdateService>.Instance,
            resolvedOptions);

        _bootloaderSession = new Pic32BootloaderSession(
            _context,
            hidTransport,
            bootloaderProtocol ?? new Pic32BootloaderProtocol(),
            hidDeviceEnumerator ?? new HidLibraryDeviceEnumerator(),
            usbLocationProvider ?? UsbLocationProviderFactory.CreateForCurrentPlatform());

        _pic32Updater = new Pic32FirmwareUpdater(_context, _bootloaderSession);
        _wifiUpdater = new WifiModuleUpdater(_context, externalProcessRunner, firmwareDownloadService);

        // Only the PIC32 flow polls for a bootloader, so it supplies the extra detail
        // (VID/PID, poll attempts, requested target) appended to a WaitingForBootloader timeout.
        _context.WaitingForBootloaderTimeoutDetailProvider = _bootloaderSession.DescribeBootloaderSearch;
    }

    /// <summary>
    /// Gets the composed firmware download service for callers that coordinate
    /// firmware acquisition and update orchestration from a shared service graph.
    /// </summary>
    public IFirmwareDownloadService FirmwareDownloadService { get; }

    /// <inheritdoc />
    public FirmwareUpdateState CurrentState => _context.CurrentState;

    /// <inheritdoc />
    public event EventHandler<FirmwareUpdateStateChangedEventArgs>? StateChanged
    {
        add => _context.StateChanged += value;
        remove => _context.StateChanged -= value;
    }

    /// <inheritdoc />
    public Task UpdateFirmwareAsync(
        IStreamingDevice device,
        string hexFilePath,
        IProgress<FirmwareUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => UpdateFirmwareAsync(device, hexFilePath, progress, null, cancellationToken);

    /// <inheritdoc />
    public Task UpdateFirmwareAsync(
        IStreamingDevice device,
        string hexFilePath,
        IProgress<FirmwareUpdateProgress>? progress,
        string? targetDevicePath,
        CancellationToken cancellationToken = default)
        => UpdateFirmwareAsync(device, hexFilePath, progress, targetDevicePath, null, cancellationToken);

    /// <inheritdoc />
    public async Task UpdateFirmwareAsync(
        IStreamingDevice device,
        string hexFilePath,
        IProgress<FirmwareUpdateProgress>? progress,
        string? targetDevicePath,
        string? targetLocationKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (string.IsNullOrWhiteSpace(hexFilePath))
        {
            throw new ArgumentException("HEX file path cannot be empty.", nameof(hexFilePath));
        }

        // Fast-fail an obviously-invalid target (whitespace) rather than polling until the
        // WaitingForBootloader state timeout. Null means "no targeting" (first enumerated bootloader).
        if (targetDevicePath != null && string.IsNullOrWhiteSpace(targetDevicePath))
        {
            throw new ArgumentException("Target device path cannot be whitespace.", nameof(targetDevicePath));
        }

        if (targetLocationKey != null && string.IsNullOrWhiteSpace(targetLocationKey))
        {
            throw new ArgumentException("Target location key cannot be whitespace.", nameof(targetLocationKey));
        }

        if (!File.Exists(hexFilePath))
        {
            throw new FileNotFoundException("Firmware HEX file was not found.", hexFilePath);
        }

        var hexLines = File.ReadAllLines(hexFilePath);
        var (hexRecords, crcRegions, totalBytes) = _bootloaderSession.PrepareHexImage(hexLines);

        await RunExclusiveAsync(
            ct => _pic32Updater.RunUpdateAsync(
                device, hexRecords, crcRegions, totalBytes, progress, targetDevicePath, targetLocationKey, ct),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
#pragma warning disable CA1068
    public async Task UpdateWifiModuleAsync(
        IStreamingDevice device,
        string firmwarePath,
        IProgress<FirmwareUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool skipVersionCheck = false)
#pragma warning restore CA1068
    {
        ArgumentNullException.ThrowIfNull(device);

        if (string.IsNullOrWhiteSpace(firmwarePath))
        {
            throw new ArgumentException("WiFi firmware path cannot be empty.", nameof(firmwarePath));
        }

        var pathExists = File.Exists(firmwarePath) || Directory.Exists(firmwarePath);
        if (!pathExists)
        {
            throw new FileNotFoundException("WiFi firmware path was not found.", firmwarePath);
        }

        await RunExclusiveAsync(
            ct => _wifiUpdater.RunUpdateAsync(device, firmwarePath, progress, skipVersionCheck, ct),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<WifiFirmwareStatus> CheckWifiFirmwareStatusAsync(
        IStreamingDevice device,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ThrowIfDisposed();

        // Serialize device I/O with UpdateFirmwareAsync / UpdateWifiModuleAsync.
        // GetLanChipInfoAsync runs a SCPI text exchange on the same transport
        // those updates use; a concurrent caller would interleave on the wire
        // and either corrupt the consumer swap or get partial replies.
        // Acquired without the RunExclusiveAsync state check — a status probe
        // is read-only and should be available to UI even when an update is
        // in flight (it just waits for the in-flight I/O to release the lock).
        //
        // Reentrancy guard: an update fires progress / state-change callbacks
        // synchronously while it holds _operationLock. If a callback handler
        // calls back into CheckWifiFirmwareStatusAsync, WaitAsync would
        // deadlock waiting for the same lock the caller's flow already owns
        // (SemaphoreSlim is not re-entrant). The AsyncLocal flag detects
        // this case and skips the second acquisition; we're already in a
        // serialized device-I/O context.
        if (_isInsideOperation.Value)
        {
            return await _wifiUpdater.CheckStatusAsync(device, cancellationToken).ConfigureAwait(false);
        }

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        _isInsideOperation.Value = true;
        try
        {
            return await _wifiUpdater.CheckStatusAsync(device, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _isInsideOperation.Value = false;
            _operationLock.Release();
        }
    }

    /// <inheritdoc />
    public Task<string> CheckBootloaderHealthAsync(
        string? targetDevicePath = null,
        CancellationToken cancellationToken = default)
        => RunBootloaderDiagnosticAsync(
            targetDevicePath,
            ct => _pic32Updater.RunHealthCheckAsync(targetDevicePath, ct),
            cancellationToken);

    /// <inheritdoc />
    public async Task ResetBootloaderAsync(
        string? targetDevicePath = null,
        CancellationToken cancellationToken = default)
        => await RunBootloaderDiagnosticAsync(
            targetDevicePath,
            async ct =>
            {
                await _pic32Updater.RunSoftResetAsync(targetDevicePath, ct).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Serializes a lightweight bootloader diagnostic on the same operation lock and HID
    /// transport the full update flow uses, then guarantees the HID transport is disconnected
    /// afterward. Unlike <see cref="RunExclusiveAsync"/> it does not drive the update state
    /// machine (<see cref="CurrentState"/> stays <see cref="FirmwareUpdateState.Idle"/>) — a
    /// health check / soft reset is not a firmware update — but it keeps the same Idle-only
    /// gate so it cannot interleave with an in-flight update. Reentrancy from an update's
    /// synchronous progress / state-change callback is rejected (rather than allowed through
    /// like the read-only <see cref="CheckWifiFirmwareStatusAsync"/> probe) because a
    /// diagnostic owns the HID connect/version/reset exchange.
    /// </summary>
    private async Task<T> RunBootloaderDiagnosticAsync<T>(
        string? targetDevicePath,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        // Fast-fail an obviously-invalid target (whitespace) rather than polling until the
        // WaitingForBootloader state timeout. Null means "no targeting" (first enumerated bootloader).
        if (targetDevicePath != null && string.IsNullOrWhiteSpace(targetDevicePath))
        {
            throw new ArgumentException("Target device path cannot be whitespace.", nameof(targetDevicePath));
        }

        if (_isInsideOperation.Value)
        {
            throw new InvalidOperationException(
                "Cannot run a bootloader diagnostic from within an in-flight firmware operation.");
        }

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        _isInsideOperation.Value = true;
        try
        {
            _context.ResetIfTerminalState();

            if (_context.CurrentState != FirmwareUpdateState.Idle)
            {
                throw new InvalidOperationException(
                    $"Cannot run a bootloader diagnostic while service is in state {_context.CurrentState}.");
            }

            _bootloaderSession.ResetTargetingState(targetDevicePath);

            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // A health check leaves a live HID handle; a soft reset re-enumerates the
            // device out from under it. Either way, release the handle before returning
            // so a subsequent update (or diagnostic) starts from a clean transport.
            await _bootloaderSession.SafeDisconnectAsync().ConfigureAwait(false);
            _isInsideOperation.Value = false;
            _operationLock.Release();
        }
    }

    private async Task RunExclusiveAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        _isInsideOperation.Value = true;
        try
        {
            _context.ResetIfTerminalState();

            if (_context.CurrentState != FirmwareUpdateState.Idle)
            {
                throw new InvalidOperationException(
                    $"Cannot start firmware update while service is in state {_context.CurrentState}.");
            }

            _context.ResetProgress();
            _bootloaderSession.ResetTargetingState();
            await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _isInsideOperation.Value = false;
            _operationLock.Release();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(FirmwareUpdateService));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _operationLock.Dispose();
        _disposed = true;
    }
}
