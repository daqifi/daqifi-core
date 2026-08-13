using System.Net;
using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Core.Firmware;

namespace Daqifi.Core.Tests.Firmware;

// Test doubles used by more than one firmware-update test file. All of these started out nested
// inside FirmwareUpdateServiceTests, which was fine while that file was the only caller;
// WifiModuleUpdaterTests is the second, and a second hand-written IStreamingDevice would be ~145
// lines of duplicated interface surface to keep in step by hand. Moved verbatim (same names, same
// behavior) so every existing call site still binds to the same double; the only additions are the
// extra observation hooks noted below. Doubles with a single caller deliberately stay nested where
// they are used.

internal class FakeStreamingDevice : IStreamingDevice
{
    private ConnectionStatus _status = ConnectionStatus.Connected;

    public FakeStreamingDevice(string name)
    {
        Name = name;
        IsConnected = true;
    }

    public string Name { get; }
    public IPAddress? IpAddress => null;
    public bool IsConnected { get; private set; }
    public ConnectionStatus Status => _status;
    public DeviceMetadata Metadata { get; } = new();
    public IReadOnlyList<IChannel> Channels => Array.Empty<IChannel>();
    public IReadOnlyList<IChannel> GetChannelsSnapshot() => Array.Empty<IChannel>();
    public int StreamingFrequency { get; set; }
    public bool IsStreaming { get; private set; }

    public int ConnectAttempts { get; private set; }
    public int DisconnectCalls { get; private set; }

    public int ConnectFailuresBeforeSuccess { get; set; }
    public List<string> SentCommands { get; } = [];

    /// <summary>
    /// Invoked synchronously after each text command is recorded, so a test can react to a
    /// specific command (e.g. cancel the operation) at the exact point the device sees it.
    /// </summary>
    public Action<string>? OnCommandSent { get; set; }

    public event EventHandler<DeviceStatusEventArgs>? StatusChanged;
    public event EventHandler<MessageReceivedEventArgs>? MessageReceived
    {
        add { }
        remove { }
    }

    public event EventHandler<DeviceErrorEventArgs>? ErrorOccurred
    {
        add { }
        remove { }
    }

    public event EventHandler<ChannelsPopulatedEventArgs>? ChannelsPopulated
    {
        add { }
        remove { }
    }

    public void Connect()
    {
        ConnectAttempts++;
        if (ConnectFailuresBeforeSuccess > 0)
        {
            ConnectFailuresBeforeSuccess--;
            throw new IOException("Simulated serial reconnect failure.");
        }

        IsConnected = true;
        _status = ConnectionStatus.Connected;
        StatusChanged?.Invoke(this, new DeviceStatusEventArgs(_status));
    }

    public void Disconnect()
    {
        DisconnectCalls++;
        IsConnected = false;
        _status = ConnectionStatus.Disconnected;
        StatusChanged?.Invoke(this, new DeviceStatusEventArgs(_status));
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Connect();
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        Disconnect();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disconnect();
        return ValueTask.CompletedTask;
    }

    public void Send<T>(IOutboundMessage<T> message)
    {
        if (message is IOutboundMessage<string> textMessage)
        {
            SentCommands.Add(textMessage.Data);
            OnCommandSent?.Invoke(textMessage.Data);
        }
    }

    public void StartStreaming()
    {
        IsStreaming = true;
    }

    public void StopStreaming()
    {
        IsStreaming = false;
    }

    public void EnableChannel(IChannel channel) { }
    public void EnableChannels(IEnumerable<IChannel> channels) { }
    public void DisableChannel(IChannel channel) { }
    public void DisableAllChannels() { }
    public void SetDioDirection(IChannel channel, ChannelDirection direction) { }
    public void SetDioValue(IChannel channel, bool value) { }
    public void SetPwmEnabled(IChannel channel, bool enabled) { }
    public void SetPwmDutyCycle(IChannel channel, int dutyCyclePercent) { }
    public void SetPwmFrequency(int frequencyHz) { }
    public int PwmFrequencyHz => 0;
    public void SetAnalogOutput(int channelNumber, double voltage) { }
    public void Reboot() => Disconnect();
    public void SaveAdcCalibration() { }
    public void LoadAdcCalibration() { }
    public void SaveVoltagePrecision() { }
    public void LoadVoltagePrecision() { }
    public void SetAdcCalibrationSlope(int channelNumber, double calM) { }
    public void SetAdcCalibrationOffset(int channelNumber, double calB) { }
    public void SaveFactoryAdcCalibration() { }
    public void LoadFactoryAdcCalibration() { }
    public void UseAdcCalibration(int bank) { }

    public Task SaveAdcCalibrationAsync(CancellationToken cancellationToken = default) { SaveAdcCalibration(); return Task.CompletedTask; }
    public Task LoadAdcCalibrationAsync(CancellationToken cancellationToken = default) { LoadAdcCalibration(); return Task.CompletedTask; }
    public Task SetAdcCalibrationSlopeAsync(int channelNumber, double calM, CancellationToken cancellationToken = default) { SetAdcCalibrationSlope(channelNumber, calM); return Task.CompletedTask; }
    public Task SetAdcCalibrationOffsetAsync(int channelNumber, double calB, CancellationToken cancellationToken = default) { SetAdcCalibrationOffset(channelNumber, calB); return Task.CompletedTask; }
    public Task SaveFactoryAdcCalibrationAsync(CancellationToken cancellationToken = default) { SaveFactoryAdcCalibration(); return Task.CompletedTask; }
    public Task LoadFactoryAdcCalibrationAsync(CancellationToken cancellationToken = default) { LoadFactoryAdcCalibration(); return Task.CompletedTask; }
    public Task UseAdcCalibrationAsync(int bank, CancellationToken cancellationToken = default) { UseAdcCalibration(bank); return Task.CompletedTask; }
    public Task SaveVoltagePrecisionAsync(CancellationToken cancellationToken = default) { SaveVoltagePrecision(); return Task.CompletedTask; }
    public Task LoadVoltagePrecisionAsync(CancellationToken cancellationToken = default) { LoadVoltagePrecision(); return Task.CompletedTask; }
}

internal sealed class FakeFirmwareDownloadService : IFirmwareDownloadService
{
    public FirmwareReleaseInfo? LatestWifiRelease { get; set; }

    /// <summary>
    /// When set, <see cref="GetLatestWifiReleaseAsync"/> faults with this instead of
    /// returning <see cref="LatestWifiRelease"/>. Models the *throwing* half of the
    /// release-lookup failure (offline / DNS / rate limit), which reaches a different
    /// catch block than the null-return half.
    /// </summary>
    public Exception? LatestWifiReleaseException { get; set; }

    public Task<FirmwareReleaseInfo?> GetLatestReleaseAsync(bool includePreRelease = false, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<FirmwareReleaseInfo?>(null);
    }

    public Task<FirmwareUpdateCheckResult> CheckForUpdateAsync(string deviceVersionString, bool includePreRelease = false, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new FirmwareUpdateCheckResult
        {
            UpdateAvailable = false
        });
    }

    public Task<string?> DownloadLatestFirmwareAsync(string destinationDirectory, bool includePreRelease = false, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<string?>(null);
    }

    public Task<string?> DownloadFirmwareByTagAsync(string tagName, string destinationDirectory, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<string?>(null);
    }

    public Task<(string ExtractedPath, string Version)?> DownloadWifiFirmwareAsync(string destinationDirectory, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<(string ExtractedPath, string Version)?>(null);
    }

    /// <summary>
    /// Invoked at the start of <see cref="GetLatestWifiReleaseAsync"/>. Gives a test a hook
    /// inside the WiFi version check — the last thing that runs before the update's device-prep
    /// step — so it can, for example, cancel the operation at exactly that point.
    /// </summary>
    public Action? OnGetLatestWifiRelease { get; set; }

    public Task<FirmwareReleaseInfo?> GetLatestWifiReleaseAsync(CancellationToken cancellationToken = default)
    {
        OnGetLatestWifiRelease?.Invoke();

        return LatestWifiReleaseException is not null
            ? Task.FromException<FirmwareReleaseInfo?>(LatestWifiReleaseException)
            : Task.FromResult(LatestWifiRelease);
    }

    public void InvalidateCache()
    {
    }
}

/// <summary>
/// A <see cref="FakeStreamingDevice"/> that also answers the WiFi chip-info query, for the paths
/// that go through <c>GetLanChipInfoWithRetryAsync</c>. Derived rather than written from scratch so
/// the plain fake stays the "device that does NOT implement <see cref="ILanChipInfoProvider"/>"
/// case that <c>DeviceDoesNotSupportLanQuery</c> depends on.
/// </summary>
internal sealed class FakeLanChipInfoDevice(string name) : FakeStreamingDevice(name), ILanChipInfoProvider
{
    /// <summary>
    /// Scripted per-attempt outcomes. Each query dequeues one; returning null models an
    /// unparseable response and throwing models a device-reported failure. Once drained,
    /// <see cref="DefaultChipInfoResponse"/> answers every further query, so a test asserting
    /// <see cref="ChipInfoQueryCount"/> still notices unexpected extra attempts.
    /// </summary>
    public Queue<Func<LanChipInfo?>> ChipInfoResponses { get; } = new();

    public Func<LanChipInfo?> DefaultChipInfoResponse { get; set; } = () => null;

    public int ChipInfoQueryCount { get; private set; }

    public Task<LanChipInfo?> GetLanChipInfoAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ChipInfoQueryCount++;

        var next = ChipInfoResponses.Count > 0 ? ChipInfoResponses.Dequeue() : DefaultChipInfoResponse;

        // Faulted task rather than a synchronous throw: the real provider is async, and the retry
        // loop's `catch` clauses must be exercised through an awaited task like production.
        try
        {
            return Task.FromResult(next());
        }
        catch (Exception ex)
        {
            return Task.FromException<LanChipInfo?>(ex);
        }
    }
}

internal sealed class FakeExternalProcessRunner : IExternalProcessRunner
{
    public ExternalProcessResult NextResult { get; set; } = new(0, false, TimeSpan.Zero, [], []);

    /// <summary>
    /// Optional per-attempt results. When non-empty each <see cref="RunAsync"/> call dequeues
    /// the next result, letting tests model the WINC flash retry path (transient failure →
    /// success). Once the sequence is drained, subsequent calls return <see cref="NextResult"/>
    /// — kept deliberately distinct from the scripted results so a test asserting
    /// <see cref="RunCount"/> can still catch unexpected extra attempts.
    /// </summary>
    public Queue<ExternalProcessResult> ResultSequence { get; } = new();

    public ExternalProcessRequest? LastRequest { get; private set; }
    public int RunCount { get; private set; }

    /// <summary>
    /// Every request seen, in order. <see cref="LastRequest"/> answers "how was the tool invoked",
    /// which is all the facade tests ever needed; a retry test also has to show that each attempt
    /// got its OWN request — the stdin responder is one-shot, so a reused one would be spent.
    /// </summary>
    public List<ExternalProcessRequest> Requests { get; } = [];

    /// <summary>
    /// The non-null answers the request's stdin responder produced, paired with the output line
    /// that triggered them. The responder's return value is what the flash tool actually receives
    /// on stdin, so this is the only way to observe the prompt handshake.
    /// </summary>
    public List<(string Line, string Response)> PromptResponses { get; } = [];

    public Task<ExternalProcessResult> RunAsync(
        ExternalProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastRequest = request;
        Requests.Add(request);
        RunCount++;

        var result = ResultSequence.Count > 0 ? ResultSequence.Dequeue() : NextResult;

        foreach (var line in result.StandardOutputLines)
        {
            request.OnStandardOutputLine?.Invoke(line);
            var response = request.StandardInputResponseFactory?.Invoke(line);
            if (response is not null)
            {
                PromptResponses.Add((line, response));
            }
        }

        foreach (var line in result.StandardErrorLines)
        {
            request.OnStandardErrorLine?.Invoke(line);
        }

        return Task.FromResult(result);
    }
}
