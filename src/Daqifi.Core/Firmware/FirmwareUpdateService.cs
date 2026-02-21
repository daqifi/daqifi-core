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
    private static readonly Regex PercentRegex = new(
        @"(?<percent>\d{1,3})\s*%",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly IReadOnlyDictionary<FirmwareUpdateState, IReadOnlySet<FirmwareUpdateState>> AllowedTransitions
        = new Dictionary<FirmwareUpdateState, IReadOnlySet<FirmwareUpdateState>>
        {
            [FirmwareUpdateState.Idle] = new HashSet<FirmwareUpdateState>
            {
                FirmwareUpdateState.PreparingDevice,
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

        var hexRecords = ParseHexRecords(hexFilePath);
        var totalBytes = hexRecords.Sum(record => (long)record.Length);
        if (totalBytes <= 0)
        {
            throw new InvalidDataException("Firmware HEX file did not contain any writable records.");
        }

        await RunExclusiveAsync(
            ct => RunPic32UpdateAsync(device, hexRecords, totalBytes, progress, ct),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpdateWifiModuleAsync(
        IStreamingDevice device,
        string firmwarePath,
        IProgress<FirmwareUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
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
            ct => RunWifiUpdateAsync(device, firmwarePath, progress, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RunExclusiveAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            _operationLock.Release();
        }
    }

    private async Task RunPic32UpdateAsync(
        IStreamingDevice device,
        IReadOnlyList<byte[]> hexRecords,
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

            TransitionToState(FirmwareUpdateState.Verifying, "Verifying bootloader response.");
            ReportProgress(progress, FirmwareUpdateState.Verifying, 92, _currentOperation, totalBytes, totalBytes);

            var verificationVersion = await ExecuteWithStateTimeoutAsync(
                FirmwareUpdateState.Verifying,
                "verify bootloader response",
                RequestBootloaderVersionAsync,
                cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Bootloader verification response: {BootloaderVersion}", verificationVersion);

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
        CancellationToken cancellationToken)
    {
        const long totalBytes = 100;

        try
        {
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
                },
                cancellationToken).ConfigureAwait(false);

            TransitionToState(FirmwareUpdateState.Programming, "Running WiFi module flash tool.");
            ReportProgress(progress, FirmwareUpdateState.Programming, 20, _currentOperation, 0, totalBytes);

            var request = BuildWifiProcessRequest(device, firmwarePath, progress);
            var processResult = await ExecuteWithStateTimeoutAsync(
                FirmwareUpdateState.Programming,
                "execute WiFi flash process",
                ct => _externalProcessRunner.RunAsync(request, ct),
                cancellationToken).ConfigureAwait(false);

            if (processResult.TimedOut)
            {
                throw new TimeoutException(
                    $"WiFi flashing process timed out after {request.Timeout.TotalSeconds:F0} seconds.");
            }

            if (ContainsWifiProgrammingFailure(processResult.StandardOutputLines))
            {
                throw new IOException("WiFi flashing tool reported a programming failure.");
            }

            if (processResult.ExitCode != 0)
            {
                throw new IOException(
                    $"WiFi flashing process exited with code {processResult.ExitCode}. " +
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

    private ExternalProcessRequest BuildWifiProcessRequest(
        IStreamingDevice device,
        string firmwarePath,
        IProgress<FirmwareUpdateProgress>? progress)
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

        var lastProcessPercent = 0;
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

                var parsedPercent = TryParseWifiToolPercent(line);
                if (!parsedPercent.HasValue)
                {
                    return;
                }

                var processPercent = parsedPercent.Value;
                lock (progressLock)
                {
                    if (processPercent < lastProcessPercent)
                    {
                        processPercent = lastProcessPercent;
                    }
                    else
                    {
                        lastProcessPercent = processPercent;
                    }
                }

                var overallPercent = 20 + (processPercent * 0.70);
                ReportProgress(
                    progress,
                    FirmwareUpdateState.Programming,
                    overallPercent,
                    line,
                    processPercent,
                    100);
            },
            OnStandardErrorLine = line => _logger.LogWarning("WiFi flash stderr: {Line}", line),
            StandardInputResponseFactory = line =>
                line.Contains("Power cycle WINC and set to bootloader mode", StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : null
        };
    }

    private static bool ContainsWifiProgrammingFailure(IEnumerable<string> outputLines)
    {
        foreach (var line in outputLines)
        {
            if (line.Contains("Programming device failed", StringComparison.OrdinalIgnoreCase))
            {
                return true;
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

    private IReadOnlyList<byte[]> ParseHexRecords(string hexFilePath)
    {
        var lines = File.ReadAllLines(hexFilePath);
        return _bootloaderProtocol.ParseHexFile(lines);
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

    private async Task JumpToApplicationAndReconnectAsync(
        IStreamingDevice device,
        CancellationToken cancellationToken)
    {
        await _hidTransport
            .WriteAsync(_bootloaderProtocol.CreateJumpToApplicationMessage(), cancellationToken)
            .ConfigureAwait(false);

        await SafeDisconnectHidAsync().ConfigureAwait(false);
        await WaitForSerialReconnectAsync(device, cancellationToken).ConfigureAwait(false);
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

    private static int? TryParseWifiToolPercent(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var match = PercentRegex.Match(line);
        if (match.Success &&
            int.TryParse(match.Groups["percent"].Value, out var parsedPercent))
        {
            return Math.Clamp(parsedPercent, 0, 100);
        }

        if (line.Contains("begin write operation", StringComparison.OrdinalIgnoreCase))
        {
            return 33;
        }

        if (line.Contains("begin read operation", StringComparison.OrdinalIgnoreCase))
        {
            return 66;
        }

        if (line.Contains("begin verify operation", StringComparison.OrdinalIgnoreCase))
        {
            return 90;
        }

        return null;
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
                "Verification failed. Retry update and confirm the expected firmware package was selected.",
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
