using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Microsoft.Extensions.Logging;

namespace Daqifi.Core.Firmware;

/// <summary>
/// Default firmware update orchestration service for PIC32 and WiFi update flows.
/// </summary>
public sealed class FirmwareUpdateService : IFirmwareUpdateService, IDisposable
{
    // WINC flash tool prompt markers (stdin handshake).
    private const string WincBootPromptMarker = "Power cycle WINC and set to bootloader mode";
    private const string WincContinuePromptMarker = "Press any key to continue";

    // WINC flash tool failure markers. The "transient" set is recoverable by re-running the
    // tool once the device has settled into bridge mode; the full set forces a failure verdict.
    private const string WifiBridgeIdQueryFailureMarker = "failed to read serial bridge ID query response";
    private const string WifiProgrammerInitFailureMarker = "failed to initialise programming firmware";
    private const string WifiProgrammingFailedMarker = "Programming device failed";
    private const string WifiReadXoFailedMarker = "Reading XO (offset) failed";
    private const string WifiBuildImageFailedMarker = "Building programming image failed";

    private static readonly IReadOnlyDictionary<FirmwareUpdateState, IReadOnlySet<FirmwareUpdateState>> AllowedTransitions
        = new Dictionary<FirmwareUpdateState, IReadOnlySet<FirmwareUpdateState>>
        {
            [FirmwareUpdateState.Idle] = new HashSet<FirmwareUpdateState>
            {
                FirmwareUpdateState.PreparingDevice,
                FirmwareUpdateState.Complete,
                FirmwareUpdateState.Failed
            },
            [FirmwareUpdateState.PreparingDevice] = new HashSet<FirmwareUpdateState>
            {
                FirmwareUpdateState.WaitingForBootloader,
                FirmwareUpdateState.Programming,
                FirmwareUpdateState.Failed
            },
            [FirmwareUpdateState.WaitingForBootloader] = new HashSet<FirmwareUpdateState>
            {
                FirmwareUpdateState.Connecting,
                FirmwareUpdateState.Failed
            },
            [FirmwareUpdateState.Connecting] = new HashSet<FirmwareUpdateState>
            {
                FirmwareUpdateState.ErasingFlash,
                FirmwareUpdateState.Failed
            },
            [FirmwareUpdateState.ErasingFlash] = new HashSet<FirmwareUpdateState>
            {
                FirmwareUpdateState.Programming,
                FirmwareUpdateState.Failed
            },
            [FirmwareUpdateState.Programming] = new HashSet<FirmwareUpdateState>
            {
                FirmwareUpdateState.Verifying,
                FirmwareUpdateState.JumpingToApp,
                FirmwareUpdateState.Failed
            },
            [FirmwareUpdateState.Verifying] = new HashSet<FirmwareUpdateState>
            {
                FirmwareUpdateState.JumpingToApp,
                FirmwareUpdateState.Complete,
                FirmwareUpdateState.Failed
            },
            [FirmwareUpdateState.JumpingToApp] = new HashSet<FirmwareUpdateState>
            {
                FirmwareUpdateState.Complete,
                FirmwareUpdateState.Failed
            },
            [FirmwareUpdateState.Complete] = new HashSet<FirmwareUpdateState>
            {
                FirmwareUpdateState.Idle
            },
            [FirmwareUpdateState.Failed] = new HashSet<FirmwareUpdateState>
            {
                FirmwareUpdateState.Idle
            }
        };

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
    private readonly IHidTransport _hidTransport;
    private readonly IExternalProcessRunner _externalProcessRunner;
    private readonly ILogger<FirmwareUpdateService> _logger;
    private readonly IBootloaderProtocol _bootloaderProtocol;
    private readonly IHidDeviceEnumerator _hidDeviceEnumerator;
    private readonly FirmwareUpdateServiceOptions _options;

    private string _currentOperation = "Idle";
    private double _lastReportedPercent;
    private int _bootloaderPollAttempts;
    private Exception? _lastBootloaderEnumerationError;
    private bool _disposed;

    /// <summary>
    /// Initializes a new firmware update service.
    /// </summary>
    public FirmwareUpdateService(
        IHidTransport hidTransport,
        IFirmwareDownloadService firmwareDownloadService,
        IExternalProcessRunner externalProcessRunner,
        ILogger<FirmwareUpdateService> logger,
        IBootloaderProtocol? bootloaderProtocol = null,
        IHidDeviceEnumerator? hidDeviceEnumerator = null,
        FirmwareUpdateServiceOptions? options = null)
    {
        _hidTransport = hidTransport ?? throw new ArgumentNullException(nameof(hidTransport));
        FirmwareDownloadService = firmwareDownloadService ?? throw new ArgumentNullException(nameof(firmwareDownloadService));
        _externalProcessRunner = externalProcessRunner ?? throw new ArgumentNullException(nameof(externalProcessRunner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _bootloaderProtocol = bootloaderProtocol ?? new Pic32BootloaderProtocol();
        _hidDeviceEnumerator = hidDeviceEnumerator ?? new HidLibraryDeviceEnumerator();
        _options = options ?? new FirmwareUpdateServiceOptions();
        _options.Validate();
    }

    /// <summary>
    /// Gets the composed firmware download service for callers that coordinate
    /// firmware acquisition and update orchestration from a shared service graph.
    /// </summary>
    public IFirmwareDownloadService FirmwareDownloadService { get; }

    /// <inheritdoc />
    public FirmwareUpdateState CurrentState { get; private set; } = FirmwareUpdateState.Idle;

    /// <inheritdoc />
    public event EventHandler<FirmwareUpdateStateChangedEventArgs>? StateChanged;

    /// <inheritdoc />
    public async Task UpdateFirmwareAsync(
        IStreamingDevice device,
        string hexFilePath,
        IProgress<FirmwareUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (string.IsNullOrWhiteSpace(hexFilePath))
        {
            throw new ArgumentException("HEX file path cannot be empty.", nameof(hexFilePath));
        }

        if (!File.Exists(hexFilePath))
        {
            throw new FileNotFoundException("Firmware HEX file was not found.", hexFilePath);
        }

        var hexLines = File.ReadAllLines(hexFilePath);
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

        await RunExclusiveAsync(
            ct => RunPic32UpdateAsync(device, hexRecords, crcRegions, totalBytes, progress, ct),
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
            ct => RunWifiUpdateAsync(device, firmwarePath, progress, skipVersionCheck, ct),
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
            return await CheckWifiFirmwareStatusCoreAsync(device, cancellationToken).ConfigureAwait(false);
        }

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        _isInsideOperation.Value = true;
        try
        {
            return await CheckWifiFirmwareStatusCoreAsync(device, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
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
            ResetIfTerminalState();

            if (CurrentState != FirmwareUpdateState.Idle)
            {
                throw new InvalidOperationException(
                    $"Cannot start firmware update while service is in state {CurrentState}.");
            }

            _lastReportedPercent = 0;
            _bootloaderPollAttempts = 0;
            _lastBootloaderEnumerationError = null;
            await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _isInsideOperation.Value = false;
            _operationLock.Release();
        }
    }

    private async Task RunPic32UpdateAsync(
        IStreamingDevice device,
        IReadOnlyList<byte[]> hexRecords,
        IReadOnlyList<FlashCrcRegion> crcRegions,
        long totalBytes,
        IProgress<FirmwareUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            TransitionToState(FirmwareUpdateState.PreparingDevice, "Preparing device for PIC32 firmware update.");
            ReportProgress(progress, FirmwareUpdateState.PreparingDevice, 0, _currentOperation, 0, totalBytes);

            await ExecuteWithStateTimeoutAsync(
                FirmwareUpdateState.PreparingDevice,
                "prepare the device for bootloader mode",
                async stateToken =>
                {
                    EnsureDeviceConnected(device);

                    if (device.IsStreaming)
                    {
                        device.StopStreaming();
                    }

                    device.Send(ScpiMessageProducer.ForceBootloader);
                    await Task.Delay(_options.PostForceBootDelay, stateToken).ConfigureAwait(false);
                    device.Disconnect();
                },
                cancellationToken).ConfigureAwait(false);

            TransitionToState(FirmwareUpdateState.WaitingForBootloader, "Waiting for HID bootloader device.");
            ReportProgress(progress, FirmwareUpdateState.WaitingForBootloader, 5, _currentOperation, 0, totalBytes);

            var hidDevice = await ExecuteWithStateTimeoutAsync(
                FirmwareUpdateState.WaitingForBootloader,
                "wait for HID bootloader enumeration",
                WaitForBootloaderDeviceAsync,
                cancellationToken).ConfigureAwait(false);

            TransitionToState(FirmwareUpdateState.Connecting, "Connecting to HID bootloader.");
            ReportProgress(progress, FirmwareUpdateState.Connecting, 10, _currentOperation, 0, totalBytes);

            await ExecuteWithStateTimeoutAsync(
                FirmwareUpdateState.Connecting,
                "connect HID transport",
                ct => ConnectToBootloaderWithRetryAsync(hidDevice, ct),
                cancellationToken).ConfigureAwait(false);

            var version = await ExecuteWithStateTimeoutAsync(
                FirmwareUpdateState.Connecting,
                "request bootloader version",
                RequestBootloaderVersionAsync,
                cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Bootloader version response: {BootloaderVersion}", version);

            TransitionToState(FirmwareUpdateState.ErasingFlash, "Erasing PIC32 flash.");
            ReportProgress(progress, FirmwareUpdateState.ErasingFlash, 15, _currentOperation, 0, totalBytes);

            await ExecuteWithStateTimeoutAsync(
                FirmwareUpdateState.ErasingFlash,
                "erase flash",
                EraseFlashWithRetryAsync,
                cancellationToken).ConfigureAwait(false);

            TransitionToState(FirmwareUpdateState.Programming, "Programming flash records.");
            ReportProgress(progress, FirmwareUpdateState.Programming, 20, _currentOperation, 0, totalBytes);

            await ExecuteWithStateTimeoutAsync(
                FirmwareUpdateState.Programming,
                "program flash records",
                ct => ProgramFlashAsync(hexRecords, totalBytes, progress, ct),
                cancellationToken).ConfigureAwait(false);

            TransitionToState(FirmwareUpdateState.Verifying, "Verifying flash contents via CRC.");
            ReportProgress(progress, FirmwareUpdateState.Verifying, 92, _currentOperation, totalBytes, totalBytes);

            await ExecuteWithStateTimeoutAsync(
                FirmwareUpdateState.Verifying,
                "verify flash contents via CRC",
                ct => VerifyFlashContentsAsync(crcRegions, progress, totalBytes, ct),
                cancellationToken).ConfigureAwait(false);

            TransitionToState(FirmwareUpdateState.JumpingToApp, "Jumping to application firmware.");
            ReportProgress(progress, FirmwareUpdateState.JumpingToApp, 95, _currentOperation, totalBytes, totalBytes);

            await ExecuteWithStateTimeoutAsync(
                FirmwareUpdateState.JumpingToApp,
                "jump to application and reconnect serial transport",
                ct => JumpToApplicationAndReconnectAsync(device, ct),
                cancellationToken).ConfigureAwait(false);

            TransitionToState(FirmwareUpdateState.Complete, "PIC32 firmware update completed.");
            ReportProgress(progress, FirmwareUpdateState.Complete, 100, _currentOperation, totalBytes, totalBytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TransitionToState(FirmwareUpdateState.Failed, "PIC32 firmware update canceled.");
            ReportProgress(progress, FirmwareUpdateState.Failed, _lastReportedPercent, _currentOperation, 0, totalBytes);
            _logger.LogWarning("PIC32 firmware update canceled.");
            throw;
        }
        catch (Exception ex)
        {
            var failedState = CurrentState;
            var failedOperation = _currentOperation;
            TransitionToState(FirmwareUpdateState.Failed, failedOperation);
            ReportProgress(progress, FirmwareUpdateState.Failed, _lastReportedPercent, failedOperation, 0, totalBytes);
            _logger.LogError(ex, "PIC32 firmware update failed in state {State}.", failedState);

            throw CreateFirmwareUpdateException(failedState, failedOperation, ex);
        }
        finally
        {
            await SafeDisconnectHidAsync().ConfigureAwait(false);
        }
    }

    private async Task RunWifiUpdateAsync(
        IStreamingDevice device,
        string firmwarePath,
        IProgress<FirmwareUpdateProgress>? progress,
        bool skipVersionCheck,
        CancellationToken cancellationToken)
    {
        const long totalBytes = 100;

        try
        {
            if (!skipVersionCheck
                && await IsWifiFirmwareUpToDateAsync(device, progress, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            TransitionToState(FirmwareUpdateState.PreparingDevice, "Preparing device for WiFi module update.");
            ReportProgress(progress, FirmwareUpdateState.PreparingDevice, 0, _currentOperation, 0, totalBytes);

            await ExecuteWithStateTimeoutAsync(
                FirmwareUpdateState.PreparingDevice,
                "prepare device for WiFi update mode",
                async stateToken =>
                {
                    EnsureDeviceConnected(device);

                    if (device.IsStreaming)
                    {
                        device.StopStreaming();
                    }

                    device.Send(ScpiMessageProducer.SetLanFirmwareUpdateMode);
                    await Task.Delay(_options.PostLanFirmwareModeDelay, stateToken).ConfigureAwait(false);
                    device.Disconnect();

                    // The OS does not free the USB-CDC COM handle the instant Disconnect returns.
                    // Wait so the external WINC flash tool can open the port; without this the tool
                    // fails to open it and exits in ~1s producing no programming output (caught by
                    // the output-based success verification below as a failure).
                    if (_options.PostLanDisconnectPortReleaseDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(_options.PostLanDisconnectPortReleaseDelay, stateToken).ConfigureAwait(false);
                    }
                },
                cancellationToken).ConfigureAwait(false);

            TransitionToState(FirmwareUpdateState.Programming, "Running WiFi module flash tool.");
            ReportProgress(progress, FirmwareUpdateState.Programming, 20, _currentOperation, 0, totalBytes);

            // Build a fresh request per attempt: the stdin prompt responder carries one-shot
            // state (it answers the WINC prompt exactly once), so reusing a request across a
            // retry would leave the responder already "spent". The factory takes the per-attempt
            // linked token so the prompt-delay wait stays cancellable.
            var processResult = await RunWifiFlashToolWithRetryAsync(
                ct => BuildWifiProcessRequest(device, firmwarePath, progress, ct),
                cancellationToken).ConfigureAwait(false);

            if (processResult.TimedOut)
            {
                throw new TimeoutException(
                    $"WiFi flashing process timed out after {_options.WifiProcessTimeout.TotalSeconds:F0} seconds " +
                    $"(exit code {processResult.ExitCode}). " +
                    BuildProcessLogExcerpt(processResult));
            }

            // Verify success from the tool's OWN output, not from its exit code or run duration.
            // A genuine flash ends with "verify passed" then the success marker; when the tool
            // cannot reach the WINC — most often because the device never released the serial port,
            // so the tool couldn't open it and bailed in ~1s — it produces none of these. The exit
            // code is unreliable in both directions (some WINC script/tool combinations emit failure
            // markers yet still exit 0), so the success marker is the authority.
            if (!ContainsAny(processResult.StandardOutputLines, _options.WifiFlashSuccessMarker))
            {
                throw new IOException(
                    $"WiFi flashing did not complete successfully — the flash tool never reported " +
                    $"'{_options.WifiFlashSuccessMarker}'. {DescribeWifiFlashFailure(processResult)} " +
                    BuildProcessLogExcerpt(processResult));
            }

            TransitionToState(FirmwareUpdateState.Verifying, "Reconnecting device and restoring LAN configuration.");
            ReportProgress(progress, FirmwareUpdateState.Verifying, 92, _currentOperation, 92, totalBytes);

            await ExecuteWithStateTimeoutAsync(
                FirmwareUpdateState.Verifying,
                "reconnect serial transport after WiFi flash",
                async stateToken =>
                {
                    await Task.Delay(_options.PostWifiReconnectDelay, stateToken).ConfigureAwait(false);
                    await WaitForSerialReconnectAsync(device, stateToken).ConfigureAwait(false);
                    device.Send(ScpiMessageProducer.EnableNetworkLan);
                    device.Send(ScpiMessageProducer.ApplyNetworkLan);
                    device.Send(ScpiMessageProducer.SaveNetworkLan);
                },
                cancellationToken).ConfigureAwait(false);

            TransitionToState(FirmwareUpdateState.Complete, "WiFi module update completed.");
            ReportProgress(progress, FirmwareUpdateState.Complete, 100, _currentOperation, totalBytes, totalBytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TransitionToState(FirmwareUpdateState.Failed, "WiFi module update canceled.");
            ReportProgress(progress, FirmwareUpdateState.Failed, _lastReportedPercent, _currentOperation, 0, totalBytes);
            _logger.LogWarning("WiFi module update canceled.");
            throw;
        }
        catch (Exception ex)
        {
            var failedState = CurrentState;
            var failedOperation = _currentOperation;
            TransitionToState(FirmwareUpdateState.Failed, failedOperation);
            ReportProgress(progress, FirmwareUpdateState.Failed, _lastReportedPercent, failedOperation, 0, totalBytes);
            _logger.LogError(ex, "WiFi module update failed in state {State}.", failedState);

            throw CreateFirmwareUpdateException(failedState, failedOperation, ex);
        }
    }

    private async Task<bool> IsWifiFirmwareUpToDateAsync(
        IStreamingDevice device,
        IProgress<FirmwareUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        // Internal callsite: in addition to deciding the boolean, we must
        // transition to Complete + report 100% progress so the caller's
        // single UpdateWifiModuleAsync(...) call observes the same end-state
        // as a successful flash. CheckWifiFirmwareStatusAsync (the public
        // planning API) does not have that side effect — its callers own
        // their own logging / UI transitions.
        var status = await CheckWifiFirmwareStatusCoreAsync(device, cancellationToken).ConfigureAwait(false);

        switch (status.Reason)
        {
            case WifiFirmwareStatusReason.UpdateAvailable:
                _logger.LogInformation(
                    "WiFi firmware update available: device has {DeviceVersion}, latest is {LatestVersion}.",
                    status.CurrentChipInfo!.FwVersion,
                    status.LatestRelease!.TagName);
                return false;

            case WifiFirmwareStatusReason.UpToDate:
                var message = $"WiFi firmware is already up to date (device: {status.CurrentChipInfo!.FwVersion}, latest: {status.LatestRelease!.TagName}).";
                _logger.LogInformation(message);
                TransitionToState(FirmwareUpdateState.Complete, message);
                ReportProgress(progress, FirmwareUpdateState.Complete, 100, message, 100, 100);
                return true;

            default:
                // DeviceDoesNotSupportLanQuery, ChipInfoUnavailable,
                // LatestReleaseUnavailable, VersionUnparseable — proceed
                // with the flash conservatively.
                return false;
        }
    }

    private async Task<WifiFirmwareStatus> CheckWifiFirmwareStatusCoreAsync(
        IStreamingDevice device,
        CancellationToken cancellationToken)
    {
        if (device is not ILanChipInfoProvider lanChipInfoProvider)
        {
            return new WifiFirmwareStatus
            {
                IsUpToDate = false,
                Reason = WifiFirmwareStatusReason.DeviceDoesNotSupportLanQuery,
            };
        }

        // Bounded retry for the LAN chip-info probe (closes #144). Right
        // after a PIC32 firmware update the application is up while WiFi
        // is still finishing startup, so the first chip-info query can
        // transiently fail; without retry, the WiFi version decision
        // would short-circuit to ChipInfoUnavailable and flow on to a
        // multi-minute reflash of already-current WiFi firmware. The
        // retry budget is bounded (LanChipInfoMaxAttempts × RetryDelay)
        // and observes cancellation between attempts.
        var chipInfo = await TryGetLanChipInfoWithRetryAsync(lanChipInfoProvider, cancellationToken).ConfigureAwait(false);
        if (chipInfo == null)
        {
            return new WifiFirmwareStatus
            {
                IsUpToDate = false,
                Reason = WifiFirmwareStatusReason.ChipInfoUnavailable,
            };
        }

        FirmwareReleaseInfo? latestWifi;
        try
        {
            latestWifi = await FirmwareDownloadService
                .GetLatestWifiReleaseAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to query latest WiFi firmware release; reporting status as LatestReleaseUnavailable.");
            return new WifiFirmwareStatus
            {
                CurrentChipInfo = chipInfo,
                IsUpToDate = false,
                Reason = WifiFirmwareStatusReason.LatestReleaseUnavailable,
            };
        }

        if (latestWifi == null)
        {
            return new WifiFirmwareStatus
            {
                CurrentChipInfo = chipInfo,
                IsUpToDate = false,
                Reason = WifiFirmwareStatusReason.LatestReleaseUnavailable,
            };
        }

        // Only the device-reported version needs parsing; latestWifi.Version
        // is already a strongly-typed FirmwareVersion from FirmwareDownloadService.
        // Re-parsing TagName would risk divergence from the canonical Version
        // (different tag prefix conventions, etc.) and cost an extra parse.
        if (!FirmwareVersion.TryParse(chipInfo.FwVersion, out var deviceVersion))
        {
            return new WifiFirmwareStatus
            {
                CurrentChipInfo = chipInfo,
                LatestRelease = latestWifi,
                IsUpToDate = false,
                Reason = WifiFirmwareStatusReason.VersionUnparseable,
            };
        }

        var isCurrent = deviceVersion >= latestWifi.Version;
        return new WifiFirmwareStatus
        {
            CurrentChipInfo = chipInfo,
            LatestRelease = latestWifi,
            IsUpToDate = isCurrent,
            Reason = isCurrent ? WifiFirmwareStatusReason.UpToDate : WifiFirmwareStatusReason.UpdateAvailable,
        };
    }

    private async Task<LanChipInfo?> TryGetLanChipInfoWithRetryAsync(
        ILanChipInfoProvider lanChipInfoProvider,
        CancellationToken cancellationToken)
    {
        var maxAttempts = Math.Max(1, _options.LanChipInfoMaxAttempts);
        var retryDelay = _options.LanChipInfoRetryDelay;
        var totalTimeout = _options.LanChipInfoTotalTimeout;

        // Wall-clock budget guards against the pathological case where
        // attempt-count × per-attempt-timeout + retry-delay sum vastly
        // exceeds the configured retry budget (e.g., 3 × 2s device timeout
        // + 2 × 2s delay = ~10s while _operationLock is held). Linking
        // the caller's CT preserves cancellation semantics; the timeout
        // CTS just adds a deadline.
        using var timeoutCts = new CancellationTokenSource(totalTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);
        var linkedToken = linkedCts.Token;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                linkedToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug(
                    "LAN chip-info probe hit total timeout ({Timeout}) before attempt {Attempt}/{Max}.",
                    totalTimeout,
                    attempt,
                    maxAttempts);
                return null;
            }

            try
            {
                var chipInfo = await lanChipInfoProvider.GetLanChipInfoAsync(linkedToken).ConfigureAwait(false);
                if (chipInfo != null)
                {
                    if (attempt > 1)
                    {
                        _logger.LogDebug(
                            "LAN chip-info query succeeded on attempt {Attempt}/{Max}.",
                            attempt,
                            maxAttempts);
                    }
                    return chipInfo;
                }
                _logger.LogDebug(
                    "LAN chip-info query returned null on attempt {Attempt}/{Max}.",
                    attempt,
                    maxAttempts);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug(
                    "LAN chip-info probe hit total timeout ({Timeout}) during attempt {Attempt}/{Max}.",
                    totalTimeout,
                    attempt,
                    maxAttempts);
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(
                    ex,
                    "LAN chip-info query failed on attempt {Attempt}/{Max}.",
                    attempt,
                    maxAttempts);
            }

            if (attempt < maxAttempts)
            {
                try
                {
                    await Task.Delay(retryDelay, linkedToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogDebug(
                        "LAN chip-info probe hit total timeout ({Timeout}) during retry delay after attempt {Attempt}/{Max}.",
                        totalTimeout,
                        attempt,
                        maxAttempts);
                    return null;
                }
            }
        }

        _logger.LogDebug(
            "LAN chip-info query exhausted {Max} attempts; reporting status as ChipInfoUnavailable.",
            maxAttempts);
        return null;
    }

    private ExternalProcessRequest BuildWifiProcessRequest(
        IStreamingDevice device,
        string firmwarePath,
        IProgress<FirmwareUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        var toolPath = ResolveWifiToolPath(firmwarePath);
        var port = ResolveWifiPort(device);

        var toolArguments = _options.WifiFlashToolArgumentsTemplate
            .Replace("{port}", QuoteArgument(port), StringComparison.Ordinal)
            .Replace("{firmwarePath}", QuoteArgument(firmwarePath), StringComparison.Ordinal);

        var executablePath = toolPath;
        var executableArguments = toolArguments;

        var extension = Path.GetExtension(toolPath);
        if ((extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
             extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)) &&
            OperatingSystem.IsWindows())
        {
            executablePath = "cmd.exe";
            executableArguments = $"/c \"{toolPath}\" {toolArguments}";
        }

        // Tracks the live device-flash phase (write/read/verify) from the tool's block-address
        // output so the bar advances across the multi-minute flash, instead of latching to the
        // image-build phase's "100%" lines and freezing. See WifiFlashProgressParser.
        var progressParser = new WifiFlashProgressParser();
        var progressLock = new object();

        return new ExternalProcessRequest
        {
            FileName = executablePath,
            Arguments = executableArguments,
            WorkingDirectory = Path.GetDirectoryName(toolPath),
            Timeout = _options.WifiProcessTimeout,
            OnStandardOutputLine = line =>
            {
                _logger.LogInformation("WiFi flash output: {Line}", line);

                double processPercent;
                lock (progressLock)
                {
                    var updated = progressParser.Observe(line);
                    if (!updated.HasValue)
                    {
                        return;
                    }

                    processPercent = updated.Value;
                }

                // Map the 0-100 device-flash percent into the Programming state's 20-90 overall band.
                var overallPercent = 20 + (processPercent * 0.70);
                ReportProgress(
                    progress,
                    FirmwareUpdateState.Programming,
                    overallPercent,
                    line,
                    (long)Math.Round(processPercent),
                    100);
            },
            OnStandardErrorLine = line => _logger.LogWarning("WiFi flash stderr: {Line}", line),
            StandardInputResponseFactory = BuildWifiPromptResponder(cancellationToken)
        };
    }

    /// <summary>
    /// Builds the stdin responder for the WINC flash tool's interactive prompts. At the
    /// "Power cycle WINC" prompt it fires the optional bridge-activation callback, waits
    /// <see cref="FirmwareUpdateServiceOptions.WincBootPromptResponseDelay"/> so the firmware can
    /// finish bridge-mode init, then sends the empty continue line. The returned delegate carries
    /// one-shot state, so a fresh responder must be built for each flash attempt.
    /// </summary>
    /// <param name="cancellationToken">
    /// The flash run's linked token (state timeout + caller cancellation). The prompt-response wait
    /// observes it so a timeout or cancel unblocks the output-pump thread promptly instead of
    /// sleeping out the full delay after the process has been killed.
    /// </param>
    private Func<string, string?> BuildWifiPromptResponder(CancellationToken cancellationToken)
    {
        var continueSignalSent = false;

        return line =>
        {
            if (line.Contains(WincBootPromptMarker, StringComparison.OrdinalIgnoreCase))
            {
                if (continueSignalSent)
                {
                    return null;
                }

                if (_options.WifiBridgeActivationCallback is { } activate)
                {
                    _logger.LogInformation("Activating WiFi bridge mode before WINC programming.");
                    try
                    {
                        activate();
                        _logger.LogInformation("Bridge activation callback completed; waiting for firmware bridge init.");
                    }
                    catch (Exception ex)
                    {
                        // The bridge activation is best-effort — a failure here must not abort the
                        // flash; the tool may still reach the WINC and the success verification is
                        // the source of truth for the outcome.
                        _logger.LogWarning(ex, "WiFi bridge activation callback threw; continuing with the flash.");
                    }
                }
                else
                {
                    _logger.LogInformation("WiFi flash tool requested WINC power-cycle; waiting before sending continue signal.");
                }

                if (_options.WincBootPromptResponseDelay > TimeSpan.Zero)
                {
                    // The responder runs inline on the process output-pump thread and the tool
                    // blocks on stdin until we return, so the wait must be synchronous (a fire-and-
                    // forget Task.Delay would not pause it). Block on a cancellable delay so a run
                    // timeout / cancel unblocks the pump immediately; if canceled, skip the continue
                    // signal — the process is being torn down anyway.
                    try
                    {
                        Task.Delay(_options.WincBootPromptResponseDelay, cancellationToken)
                            .GetAwaiter()
                            .GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogDebug("WINC prompt-response wait canceled; skipping the continue signal.");
                        return null;
                    }
                }

                continueSignalSent = true;
                _logger.LogInformation("Sending continue signal to WiFi flash tool.");
                return string.Empty;
            }

            if (!continueSignalSent &&
                line.Contains(WincContinuePromptMarker, StringComparison.OrdinalIgnoreCase))
            {
                continueSignalSent = true;
                _logger.LogInformation("Sending continue signal to WiFi flash tool.");
                return string.Empty;
            }

            return null;
        };
    }

    private async Task<ExternalProcessResult> RunWifiFlashToolWithRetryAsync(
        Func<CancellationToken, ExternalProcessRequest> requestFactory,
        CancellationToken cancellationToken)
    {
        var attempts = Math.Max(1, _options.WifiFlashAttempts);
        ExternalProcessResult result = null!;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            // Build the request inside the state-timeout lambda so the responder closes over the
            // linked token (state timeout + caller cancellation) and its prompt-delay wait unblocks
            // when the run is canceled or times out.
            result = await ExecuteWithStateTimeoutAsync(
                FirmwareUpdateState.Programming,
                "execute WiFi flash process",
                ct => _externalProcessRunner.RunAsync(requestFactory(ct), ct),
                cancellationToken).ConfigureAwait(false);

            // A timeout or a verified success ends the loop; so does a non-transient failure,
            // since re-running the tool only helps when the device hadn't yet settled into bridge
            // mode. Only a transient bridge-init failure with attempts remaining triggers a retry.
            if (result.TimedOut ||
                ContainsAny(result.StandardOutputLines, _options.WifiFlashSuccessMarker) ||
                attempt >= attempts ||
                !IsTransientWifiFlashFailure(result))
            {
                return result;
            }

            _logger.LogWarning(
                "WiFi flash tool reported a transient bridge-init failure on attempt {Attempt}/{Attempts}; retrying in {DelayMs} ms.",
                attempt,
                attempts,
                _options.WifiFlashRetryDelay.TotalMilliseconds);
            await Task.Delay(_options.WifiFlashRetryDelay, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// True when the result shows a transient bridge-init failure — the device hadn't finished
    /// entering bridge mode when the tool issued its first query. Re-running the tool once the
    /// device has settled typically succeeds, so these (and only these) are retried.
    /// </summary>
    private static bool IsTransientWifiFlashFailure(ExternalProcessResult result)
    {
        // Scan both streams for every transient marker: different WINC tool/script versions route
        // these lines to stdout vs stderr inconsistently, so don't assume a fixed stream per marker.
        return ContainsAny(result.StandardErrorLines, WifiBridgeIdQueryFailureMarker, WifiProgrammerInitFailureMarker, WifiProgrammingFailedMarker, WifiReadXoFailedMarker)
            || ContainsAny(result.StandardOutputLines, WifiBridgeIdQueryFailureMarker, WifiProgrammerInitFailureMarker, WifiProgrammingFailedMarker, WifiReadXoFailedMarker);
    }

    /// <summary>
    /// Produces a short human-readable reason for a flash that did not report the success marker,
    /// distinguishing "the tool never opened the port" from a device-reported programming failure.
    /// </summary>
    private static string DescribeWifiFlashFailure(ExternalProcessResult result)
    {
        if (ContainsAny(
                result.StandardErrorLines,
                WifiBridgeIdQueryFailureMarker,
                WifiProgrammerInitFailureMarker) ||
            ContainsAny(
                result.StandardOutputLines,
                WifiProgrammingFailedMarker,
                WifiReadXoFailedMarker,
                WifiBuildImageFailedMarker))
        {
            return "The flash tool reached the device but reported a programming failure.";
        }

        // "No output" must consider BOTH streams — some failure modes (tool/port errors) print
        // only to stderr, so checking stdout alone would mislabel them as "no output".
        if (result.StandardOutputLines.Count == 0 && result.StandardErrorLines.Count == 0)
        {
            return "The flash tool produced no output — it likely could not open the serial port " +
                   "(the device may not have released it).";
        }

        if (result.StandardOutputLines.Count == 0 && result.StandardErrorLines.Count > 0)
        {
            return $"The flash tool wrote only to stderr and never programmed the device (exit code {result.ExitCode}).";
        }

        return $"The flash tool exited with code {result.ExitCode} without completing the program.";
    }

    private static bool ContainsAny(IReadOnlyList<string> lines, params string[] markers)
    {
        foreach (var line in lines)
        {
            foreach (var marker in markers)
            {
                if (line.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private string ResolveWifiToolPath(string firmwarePath)
    {
        if (File.Exists(firmwarePath))
        {
            return firmwarePath;
        }

        if (Directory.Exists(firmwarePath))
        {
            var matches = Directory.GetFiles(
                firmwarePath,
                _options.WifiFlashToolFileName,
                SearchOption.AllDirectories);

            if (matches.Length == 0)
            {
                throw new FileNotFoundException(
                    $"Could not locate '{_options.WifiFlashToolFileName}' under '{firmwarePath}'.");
            }

            return matches[0];
        }

        throw new FileNotFoundException("WiFi firmware path was not found.", firmwarePath);
    }

    private string ResolveWifiPort(IStreamingDevice device)
    {
        if (!string.IsNullOrWhiteSpace(_options.WifiPortOverride))
        {
            return _options.WifiPortOverride;
        }

        if (!string.IsNullOrWhiteSpace(device.Name))
        {
            return device.Name;
        }

        throw new InvalidOperationException("Unable to resolve a serial port name for WiFi update.");
    }

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        var escaped = value.Replace("\"", "\\\"", StringComparison.Ordinal);
        return escaped.IndexOfAny([' ', '\t']) >= 0
            ? $"\"{escaped}\""
            : escaped;
    }

    private async Task<HidDeviceInfo> WaitForBootloaderDeviceAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _bootloaderPollAttempts++;

            IReadOnlyList<HidDeviceInfo> devices;
            try
            {
                devices = await _hidDeviceEnumerator
                    .EnumerateAsync(_options.BootloaderVendorId, _options.BootloaderProductId, cancellationToken)
                    .ConfigureAwait(false);
                _lastBootloaderEnumerationError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _lastBootloaderEnumerationError = ex;
                throw new InvalidOperationException(
                    $"HID enumeration failed while searching for bootloader device " +
                    $"VID=0x{_options.BootloaderVendorId:X4}, PID=0x{_options.BootloaderProductId:X4} " +
                    $"on poll attempt {_bootloaderPollAttempts}.",
                    ex);
            }

            var firstMatch = devices.FirstOrDefault();
            if (firstMatch != null)
            {
                return firstMatch;
            }

            await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ConnectToBootloaderWithRetryAsync(
        HidDeviceInfo bootloaderDevice,
        CancellationToken cancellationToken)
    {
        await ExecuteWithRetryAsync(
            "connect HID bootloader",
            _options.HidConnectRetryCount,
            _options.HidConnectRetryDelay,
            async ct =>
            {
                if (_hidTransport.IsConnected)
                {
                    await _hidTransport.DisconnectAsync().ConfigureAwait(false);
                }

                await _hidTransport.ConnectAsync(
                    _options.BootloaderVendorId,
                    _options.BootloaderProductId,
                    bootloaderDevice.SerialNumber,
                    ct).ConfigureAwait(false);
            },
            ex => ex is IOException or TimeoutException or InvalidOperationException,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> RequestBootloaderVersionAsync(CancellationToken cancellationToken)
    {
        await _hidTransport
            .WriteAsync(_bootloaderProtocol.CreateRequestVersionMessage(), cancellationToken)
            .ConfigureAwait(false);

        var response = await _hidTransport
            .ReadAsync(_options.BootloaderResponseTimeout, cancellationToken)
            .ConfigureAwait(false);

        var version = _bootloaderProtocol.DecodeVersionResponse(response);
        if (string.Equals(version, "Error", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Bootloader returned an invalid version response.");
        }

        return version;
    }

    private async Task EraseFlashWithRetryAsync(CancellationToken cancellationToken)
    {
        await ExecuteWithRetryAsync(
            "erase flash",
            _options.FlashWriteRetryCount,
            _options.FlashWriteRetryDelay,
            async ct =>
            {
                await _hidTransport
                    .WriteAsync(_bootloaderProtocol.CreateEraseFlashMessage(), ct)
                    .ConfigureAwait(false);

                var response = await _hidTransport
                    .ReadAsync(_options.BootloaderResponseTimeout, ct)
                    .ConfigureAwait(false);

                if (!_bootloaderProtocol.DecodeEraseFlashResponse(response))
                {
                    throw new InvalidDataException("Bootloader erase acknowledgment was invalid.");
                }
            },
            ex => ex is IOException or TimeoutException or InvalidDataException,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ProgramFlashAsync(
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
            await ExecuteWithRetryAsync(
                $"program flash record {index + 1}",
                _options.FlashWriteRetryCount,
                _options.FlashWriteRetryDelay,
                async ct =>
                {
                    var message = _bootloaderProtocol.CreateProgramFlashMessage(record);
                    await _hidTransport.WriteAsync(message, ct).ConfigureAwait(false);

                    var response = await _hidTransport
                        .ReadAsync(_options.BootloaderResponseTimeout, ct)
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
            ReportProgress(
                progress,
                FirmwareUpdateState.Programming,
                completion,
                $"Programming record {index + 1} of {hexRecords.Count}",
                bytesWritten,
                totalBytes);
        }
    }

    private async Task VerifyFlashContentsAsync(
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
            var version = await RequestBootloaderVersionAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "No flash CRC regions to verify; confirmed bootloader liveness: {BootloaderVersion}.",
                version);
            return;
        }

        for (var index = 0; index < crcRegions.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var region = crcRegions[index];
            await ExecuteWithRetryAsync(
                $"read flash CRC for region {index + 1} at 0x{region.Address:X8}",
                _options.FlashWriteRetryCount,
                _options.FlashWriteRetryDelay,
                async ct =>
                {
                    await _hidTransport
                        .WriteAsync(_bootloaderProtocol.CreateReadCrcMessage(region.Address, region.Length), ct)
                        .ConfigureAwait(false);

                    var response = await _hidTransport
                        .ReadAsync(_options.BootloaderResponseTimeout, ct)
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
            ReportProgress(
                progress,
                FirmwareUpdateState.Verifying,
                verifyPercent,
                $"Verified flash CRC region {index + 1} of {crcRegions.Count}",
                totalBytes,
                totalBytes);
        }

        _logger.LogInformation(
            "Flash CRC verification passed for {RegionCount} region(s).",
            crcRegions.Count);
    }

    private async Task JumpToApplicationAndReconnectAsync(
        IStreamingDevice device,
        CancellationToken cancellationToken)
    {
        await _hidTransport
            .WriteAsync(_bootloaderProtocol.CreateJumpToApplicationMessage(), cancellationToken)
            .ConfigureAwait(false);

        await SafeDisconnectHidAsync().ConfigureAwait(false);
        await WaitForSerialReconnectAsync(device, cancellationToken).ConfigureAwait(false);

        // Discard the race-winning serial handle from the USB CDC re-enumeration
        // window. On macOS the first SerialPort.Open() that succeeds after a PIC32
        // reset is typically a "shadow" handle: IsOpen==true, but the kernel
        // device-node isn't fully wired yet — writes silently drop and reads see
        // zero bytes. A fresh open after a brief settling delay yields a clean
        // binding. Symptom without this step: SCPI Sends after reconnect appear
        // to succeed but the device never responds (LEDs stay off, readiness
        // probe returns null indefinitely until budget expires).
        // Opt out by setting PostReconnectStaleHandleDelay = TimeSpan.Zero
        // (callers on platforms where the first open is already clean).
        if (_options.PostReconnectStaleHandleDelay > TimeSpan.Zero)
        {
            _logger.LogInformation(
                "Discarding race-winning serial handle; closing and re-opening after {Delay} to obtain a clean USB CDC binding.",
                _options.PostReconnectStaleHandleDelay);
            device.Disconnect();
            await Task.Delay(_options.PostReconnectStaleHandleDelay, cancellationToken).ConfigureAwait(false);
            await WaitForSerialReconnectAsync(device, cancellationToken).ConfigureAwait(false);
        }

        // Wake the post-reset device. PIC32 application firmware boots
        // dormant (LEDs off, WiFi subsystem unpowered, won't answer LAN
        // queries) until SYSTem:POWer:STATe 1 is sent. InitializeAsync
        // handles that plus the rest of the standard init sequence
        // (echo off, stream format, etc.). Without this, callers writing
        // a "natural" probe like GetLanChipInfoAsync would silently fail
        // for tens of seconds because the device is still dormant.
        // Skipped for non-DaqifiDevice transports (e.g. test fakes); they
        // are responsible for their own readiness if needed.
        if (device is DaqifiDevice initializableDevice)
        {
            _logger.LogInformation("Waking post-reset device via InitializeAsync.");
            try
            {
                // Pass the update's token so a cancel during the post-reset wake isn't ignored
                // while InitializeAsync waits (up to ChannelPopulationTimeout) for channels.
                await initializableDevice.InitializeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Don't fail the firmware update outright — the readiness
                // probe (if configured) is the source of truth for "ready".
                // Surface the init failure as a warning so a probe timeout
                // later isn't mysterious.
                _logger.LogWarning(ex, "InitializeAsync after reconnect threw; continuing to readiness probe.");
            }
        }

        // Application-readiness probe (closes #145). Serial transport
        // re-enumeration succeeds well before the PIC32 application
        // firmware is actually ready to answer protobuf status queries;
        // if a downstream flow (LAN chip info, WiFi prep) starts before
        // the app is up, those queries fail and callers reimplement
        // their own retry. The probe is opt-in via options — when null,
        // the legacy "serial reopened == done" semantics apply.
        if (_options.PostReconnectReadinessProbe is { } probe)
        {
            await WaitForApplicationReadyAsync(device, probe, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WaitForApplicationReadyAsync(
        IStreamingDevice device,
        Func<IStreamingDevice, CancellationToken, Task<bool>> probe,
        CancellationToken cancellationToken)
    {
        var totalTimeout = _options.PostReconnectReadinessTimeout;
        var retryDelay = _options.PostReconnectReadinessRetryDelay;

        // Surface the wait at Information level so observers tailing the
        // log can distinguish "stuck" from "deliberately polling". The
        // wait can take up to PostReconnectReadinessTimeout (default 30s);
        // without this, the JumpingToApp state appears hung beyond the
        // initial transport reopen.
        _logger.LogInformation(
            "Waiting up to {Timeout} for device to become application-ready (post-reconnect readiness probe).",
            totalTimeout);
        var waitStart = DateTime.UtcNow;

        using var timeoutCts = new CancellationTokenSource(totalTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);
        var linkedToken = linkedCts.Token;

        // Capture the most recent probe-thrown exception so a TimeoutException
        // can carry the underlying cause as InnerException. Without this,
        // deterministic probe failures (e.g. transport says it's open but
        // the device never responds to status queries) report only as
        // "timed out" — losing the actual error context unless Debug logs
        // are on.
        Exception? lastProbeException = null;

        // Tracks how many probe invocations have actually run. Distinct from
        // the loop iteration counter so the timeout messages don't claim
        // "attempt N" when the timeout fired before a probe ever executed.
        var probesExecuted = 0;
        while (true)
        {
            try
            {
                linkedToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Device did not become application-ready within {totalTimeout} (probes executed: {probesExecuted}). " +
                    "The transport reconnected but the readiness probe never returned true; the device may still be initializing or the firmware may have failed to start.",
                    lastProbeException);
            }

            try
            {
                probesExecuted++;
                // WaitAsync(linkedToken) enforces the timeout deadline even
                // when the probe ignores its own CancellationToken and would
                // otherwise hang or return after the budget elapses. When
                // the deadline fires, WaitAsync throws OperationCanceledException
                // immediately — we don't keep waiting for the rogue probe.
                var isReady = await probe(device, linkedToken)
                    .WaitAsync(linkedToken)
                    .ConfigureAwait(false);

                // Successful probe invocation (true OR false) means the most
                // recent attempt completed normally. Clear lastProbeException
                // so a later timeout doesn't carry forward a stale exception
                // from an earlier failed attempt as its InnerException.
                lastProbeException = null;

                if (isReady)
                {
                    var elapsed = DateTime.UtcNow - waitStart;
                    _logger.LogInformation(
                        "Device became application-ready after {Elapsed} on probe attempt {Attempt}.",
                        elapsed,
                        probesExecuted);
                    return;
                }
                _logger.LogDebug("Application-ready probe returned false on attempt {Attempt}; will retry.", probesExecuted);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Two cases reach here:
                // 1. The wait deadline fired (timeoutCts canceled) — surface
                //    as TimeoutException so callers see the readiness budget.
                // 2. The probe itself threw OperationCanceledException for
                //    some unrelated reason (its own internal CTS, etc). That
                //    must NOT crash the update loop — treat it as a probe
                //    failure and retry, same as any other thrown exception.
                if (timeoutCts.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"Device did not become application-ready within {totalTimeout} (probes executed: {probesExecuted}). " +
                        "The wait for the readiness probe was canceled by the timeout — note the probe may ignore cancellation and continue running in the background.",
                        lastProbeException ?? ex);
                }

                lastProbeException = ex;
                _logger.LogDebug(
                    ex,
                    "Application-ready probe was canceled on attempt {Attempt}; treating as not-ready and retrying.",
                    probesExecuted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastProbeException = ex;
                _logger.LogDebug(
                    ex,
                    "Application-ready probe threw on attempt {Attempt}; treating as not-ready and retrying.",
                    probesExecuted);
            }

            try
            {
                await Task.Delay(retryDelay, linkedToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Device did not become application-ready within {totalTimeout} (probes executed: {probesExecuted}). " +
                    "The transport reconnected but the readiness probe never returned true; the device may still be initializing or the firmware may have failed to start.",
                    lastProbeException);
            }
        }
    }

    private async Task WaitForSerialReconnectAsync(
        IStreamingDevice device,
        CancellationToken cancellationToken)
    {
        // This loop is bounded by the caller's state timeout via ExecuteWithStateTimeoutAsync.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (device.IsConnected)
            {
                return;
            }

            try
            {
                device.Connect();
                if (device.IsConnected)
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Serial reconnect attempt failed.");
            }

            await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ExecuteWithRetryAsync(
        string operation,
        int maxAttempts,
        TimeSpan retryDelay,
        Func<CancellationToken, Task> action,
        Func<Exception, bool> isTransient,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await action(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && isTransient(ex))
            {
                _logger.LogWarning(
                    ex,
                    "Operation '{Operation}' failed on attempt {Attempt}/{MaxAttempts}; retrying in {DelayMs} ms.",
                    operation,
                    attempt,
                    maxAttempts,
                    retryDelay.TotalMilliseconds);

                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ExecuteWithStateTimeoutAsync(
        FirmwareUpdateState state,
        string operation,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        var timeout = _options.GetStateTimeout(state);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await action(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(BuildStateTimeoutMessage(state, operation, timeout));
        }
    }

    private async Task<T> ExecuteWithStateTimeoutAsync<T>(
        FirmwareUpdateState state,
        string operation,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var timeout = _options.GetStateTimeout(state);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            return await action(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(BuildStateTimeoutMessage(state, operation, timeout));
        }
    }

    private string BuildStateTimeoutMessage(
        FirmwareUpdateState state,
        string operation,
        TimeSpan timeout)
    {
        var message =
            $"State '{state}' timed out while attempting to {operation} after {timeout.TotalSeconds:F1} seconds.";

        if (state != FirmwareUpdateState.WaitingForBootloader)
        {
            return message;
        }

        var details =
            $"No matching HID bootloader device was enumerated for VID=0x{_options.BootloaderVendorId:X4}, " +
            $"PID=0x{_options.BootloaderProductId:X4} after {_bootloaderPollAttempts} poll attempt(s).";

        if (_lastBootloaderEnumerationError == null)
        {
            return $"{message} {details}";
        }

        var errorSummary = FormatExceptionSummary(_lastBootloaderEnumerationError);
        return $"{message} {details} Last HID enumeration error: {errorSummary}.";
    }

    private static string FormatExceptionSummary(Exception exception)
    {
        var builder = new StringBuilder();
        var current = exception;
        var firstSegment = true;

        while (current != null)
        {
            if (!firstSegment)
            {
                builder.Append(" | Inner ");
            }

            builder.Append(current.GetType().Name);
            builder.Append(": ");
            builder.Append(current.Message);
            current = current.InnerException;
            firstSegment = false;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Derives a 0-100 progress percentage for the WiFi (WINC) flash from the flash tool's live
    /// stdout. The tool runs a fast local image-build phase (whose "written … (NN%)" lines reach
    /// 100% and must be ignored, or they latch the bar near the top before the real flash starts)
    /// followed by the multi-minute on-device write → read → verify phases. Those phases emit
    /// block-address lines like <c>0x000000:[wwwwwwww] 0x008000:[wwwwwwww] …</c> with no percent,
    /// so this parser advances the bar from the highest block address seen relative to the flashed
    /// range. Each phase occupies its own monotonically increasing band; <see cref="Observe"/>
    /// returns the new percent when it advances, or <c>null</c> when a line carries no progress.
    /// </summary>
    internal sealed class WifiFlashProgressParser
    {
        private static readonly Regex BlockAddressRegex = new(
            @"0x(?<addr>[0-9a-fA-F]+)\s*:",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex VerifyRangeRegex = new(
            @"verify range\s+0x(?<start>[0-9a-fA-F]+)\s+to\s+0x(?<end>[0-9a-fA-F]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Block size between consecutive addresses in the tool's progress lines (0x8000).
        private const long BlockSize = 0x8000;

        // Default flashed range when the tool hasn't yet announced "verify range" (the WINC
        // programmed region is 0x80000 = 512 KB); expanded if a larger address is observed.
        private long _totalRange = 0x80000;

        // Base address of the flashed range. Block addresses in the tool output are absolute, so
        // the covered fraction is measured relative to this start (0 unless "verify range" reports
        // a non-zero base).
        private long _rangeStart;

        private Phase _phase = Phase.PreFlash;
        private double _lastPercent;

        private enum Phase
        {
            PreFlash,
            Write,
            Read,
            Verify
        }

        // Per-phase overall bands (write is weighted heaviest — it is by far the longest phase).
        private static (double Start, double End) BandFor(Phase phase) => phase switch
        {
            Phase.Write => (5, 60),
            Phase.Read => (60, 78),
            Phase.Verify => (78, 100),
            _ => (0, 0)
        };

        public double? Observe(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            if (line.Contains("begin write operation", StringComparison.OrdinalIgnoreCase))
            {
                return Advance(Phase.Write, BandFor(Phase.Write).Start);
            }

            if (line.Contains("begin read operation", StringComparison.OrdinalIgnoreCase))
            {
                return Advance(Phase.Read, BandFor(Phase.Read).Start);
            }

            var verifyRange = VerifyRangeRegex.Match(line);
            if (verifyRange.Success &&
                long.TryParse(verifyRange.Groups["start"].Value, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var rangeStart) &&
                long.TryParse(verifyRange.Groups["end"].Value, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var rangeEnd) &&
                rangeEnd > rangeStart)
            {
                _rangeStart = rangeStart;
                _totalRange = rangeEnd - rangeStart;
                return null;
            }

            if (line.Contains("begin verify operation", StringComparison.OrdinalIgnoreCase))
            {
                return Advance(Phase.Verify, BandFor(Phase.Verify).Start);
            }

            // Block-address lines advance the current phase. Ignored before the device flash
            // starts (PreFlash) so the image-build phase never moves the bar.
            if (_phase != Phase.PreFlash)
            {
                var highestAddress = HighestBlockAddress(line);
                if (highestAddress.HasValue)
                {
                    // Block addresses are absolute; measure coverage from the range base so a
                    // non-zero start doesn't make the fraction saturate to 1 immediately.
                    var covered = highestAddress.Value - _rangeStart + BlockSize;
                    if (covered > _totalRange)
                    {
                        _totalRange = covered;
                    }

                    var fraction = Math.Clamp(covered / (double)_totalRange, 0, 1);
                    var (start, end) = BandFor(_phase);
                    return Advance(_phase, start + (fraction * (end - start)));
                }
            }

            return null;
        }

        private double? Advance(Phase phase, double candidatePercent)
        {
            if (phase > _phase)
            {
                _phase = phase;
            }

            var clamped = Math.Clamp(candidatePercent, 0, 100);

            // Monotonic: never let the bar move backward (e.g. address resets to 0 at each new phase).
            if (clamped <= _lastPercent)
            {
                return null;
            }

            _lastPercent = clamped;
            return clamped;
        }

        private static long? HighestBlockAddress(string line)
        {
            long? highest = null;
            foreach (Match match in BlockAddressRegex.Matches(line))
            {
                if (long.TryParse(
                        match.Groups["addr"].Value,
                        System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var address))
                {
                    if (highest is null || address > highest.Value)
                    {
                        highest = address;
                    }
                }
            }

            return highest;
        }
    }

    private static string BuildProcessLogExcerpt(ExternalProcessResult result)
    {
        var excerpt = result.StandardErrorLines
            .Concat(result.StandardOutputLines)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Take(5)
            .ToArray();

        if (excerpt.Length == 0)
        {
            return "No process output captured.";
        }

        return $"Process output excerpt: {string.Join(" | ", excerpt)}";
    }

    private static void EnsureDeviceConnected(IStreamingDevice device)
    {
        if (!device.IsConnected)
        {
            throw new InvalidOperationException("Device must be connected before starting firmware update.");
        }
    }

    private FirmwareUpdateException CreateFirmwareUpdateException(
        FirmwareUpdateState failedState,
        string failedOperation,
        Exception exception)
    {
        if (exception is FirmwareUpdateException firmwareUpdateException)
        {
            return firmwareUpdateException;
        }

        var recoveryGuidance = BuildRecoveryGuidance(failedState);
        var message = $"Firmware update failed in state '{failedState}' while {failedOperation}.";

        return new FirmwareUpdateException(
            failedState,
            failedOperation,
            message,
            recoveryGuidance,
            exception);
    }

    private static string BuildRecoveryGuidance(FirmwareUpdateState failedState)
    {
        return failedState switch
        {
            FirmwareUpdateState.PreparingDevice =>
                "Ensure the device is connected over USB and not currently busy streaming.",
            FirmwareUpdateState.WaitingForBootloader =>
                "The device did not enter bootloader mode. Try unplugging/replugging USB, then retry.",
            FirmwareUpdateState.Connecting =>
                "Bootloader was found but HID connection failed. Check USB cable stability and retry.",
            FirmwareUpdateState.ErasingFlash =>
                "Flash erase failed. Retry update; if this persists, power-cycle the device and re-enter bootloader mode.",
            FirmwareUpdateState.Programming =>
                "Programming failed. Retry update while keeping USB connected; device may still be recoverable in bootloader mode.",
            FirmwareUpdateState.Verifying =>
                "Flash verification failed — the device's flash CRC did not match the firmware image. " +
                "Retry the update and confirm the expected firmware package was selected.",
            FirmwareUpdateState.JumpingToApp =>
                "The device did not return to application mode. Power-cycle the device and reconnect.",
            _ =>
                "Retry the update. If repeated failures occur, reconnect the device and attempt manual bootloader recovery."
        };
    }

    private void ReportProgress(
        IProgress<FirmwareUpdateProgress>? progress,
        FirmwareUpdateState state,
        double percentComplete,
        string currentOperation,
        long bytesWritten,
        long totalBytes)
    {
        var clampedPercent = Math.Clamp(percentComplete, 0, 100);
        _lastReportedPercent = clampedPercent;

        progress?.Report(new FirmwareUpdateProgress
        {
            State = state,
            PercentComplete = clampedPercent,
            CurrentOperation = currentOperation,
            BytesWritten = Math.Max(0, bytesWritten),
            TotalBytes = Math.Max(0, totalBytes)
        });
    }

    private void TransitionToState(FirmwareUpdateState nextState, string operation)
    {
        if (CurrentState == nextState)
        {
            _currentOperation = operation;
            return;
        }

        if (!AllowedTransitions.TryGetValue(CurrentState, out var allowedStates) ||
            !allowedStates.Contains(nextState))
        {
            throw new InvalidOperationException(
                $"Invalid firmware update transition: {CurrentState} -> {nextState}.");
        }

        var previousState = CurrentState;
        CurrentState = nextState;
        _currentOperation = operation;

        _logger.LogInformation(
            "Firmware update state transition: {PreviousState} -> {CurrentState} ({Operation})",
            previousState,
            nextState,
            operation);

        StateChanged?.Invoke(this, new FirmwareUpdateStateChangedEventArgs(previousState, nextState, operation));
    }

    private void ResetIfTerminalState()
    {
        if (CurrentState is FirmwareUpdateState.Complete or FirmwareUpdateState.Failed)
        {
            TransitionToState(FirmwareUpdateState.Idle, "Resetting state for next firmware update operation.");
        }
    }

    private async Task SafeDisconnectHidAsync()
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
            _logger.LogWarning(ex, "Failed to disconnect HID transport during cleanup.");
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
