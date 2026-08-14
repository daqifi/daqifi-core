using System.IO;
using System.Net;
using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Discovery;
using Daqifi.Core.Firmware;

namespace Daqifi.Core.Tests.Firmware;

// Test doubles used by more than one firmware-update test file. All of these started out nested
// inside FirmwareUpdateServiceTests, which was fine while that file was the only caller;
// WifiModuleUpdaterTests is the second, and a second hand-written IStreamingDevice would be ~145
// lines of duplicated interface surface to keep in step by hand. The HID/bootloader doubles below
// moved out for the same reason when Pic32BootloaderSessionTests / Pic32FirmwareUpdaterTests
// arrived. Moved verbatim (same names, same behavior) so every existing call site still binds to
// the same double; the only additions are the extra observation hooks noted below. Doubles with a
// single caller deliberately stay nested where they are used.

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

internal sealed class FakeHidTransport : IHidTransport
{
    private readonly Queue<byte[]> _readQueue = new();

    public bool IsConnected { get; private set; }
    public int? VendorId { get; private set; }
    public int? ProductId { get; private set; }
    public string? SerialNumber { get; private set; }
    public string? DevicePath { get; private set; }
    public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan WriteTimeout { get; set; } = TimeSpan.FromSeconds(1);

    public List<byte[]> Writes { get; } = [];
    public int ConnectAttempts { get; private set; }
    public int DisconnectCalls { get; private set; }

    public void EnqueueRead(byte[] response)
    {
        _readQueue.Enqueue(response);
    }

    /// <summary>
    /// Failures to inject into the next connect attempts — one per attempt, either overload —
    /// before a connect is allowed through. This is what makes the HID connect retry policy
    /// observable: which failure kinds buy another attempt and which fail on the spot is only
    /// visible when a connect can actually fail, and with a chosen exception type.
    /// </summary>
    public Queue<Exception> ConnectFailures { get; } = new();

    public Task ConnectAsync(int vendorId, int productId, string? serialNumber = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConnectAttempts++;
        if (ConnectFailures.Count > 0)
        {
            return Task.FromException(ConnectFailures.Dequeue());
        }

        IsConnected = true;
        VendorId = vendorId;
        ProductId = productId;
        SerialNumber = serialNumber;
        DevicePath = "fake-path";
        return Task.CompletedTask;
    }

    public void Connect(int vendorId, int productId, string? serialNumber = null)
    {
        ConnectAsync(vendorId, productId, serialNumber).GetAwaiter().GetResult();
    }

    public int ConnectByPathAttempts { get; private set; }
    public string? LastConnectByPath { get; private set; }

    public Task ConnectByPathAsync(string devicePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConnectByPathAttempts++;
        LastConnectByPath = devicePath;
        if (ConnectFailures.Count > 0)
        {
            return Task.FromException(ConnectFailures.Dequeue());
        }

        IsConnected = true;
        DevicePath = devicePath;
        return Task.CompletedTask;
    }

    public void ConnectByPath(string devicePath)
    {
        ConnectByPathAsync(devicePath).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Optional hook invoked by <see cref="WriteAsync"/> before the write is recorded.
    /// Lets a test make a write hang (honoring the linked cancellation token) to verify
    /// per-state timeout enforcement.
    /// </summary>
    public Func<byte[], CancellationToken, Task>? WriteHook { get; set; }

    public async Task WriteAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsConnected)
        {
            throw new InvalidOperationException("Not connected.");
        }

        if (WriteHook is not null)
        {
            await WriteHook(data, cancellationToken).ConfigureAwait(false);
        }

        Writes.Add(data.ToArray());
    }

    public void Write(byte[] data)
    {
        WriteAsync(data).GetAwaiter().GetResult();
    }

    private bool _dropWhenReadQueueEmpty;

    /// <summary>
    /// Makes the first ReadAsync after the queued responses are exhausted
    /// throw and drop the connection, simulating the device disappearing
    /// from the bus mid-operation.
    /// </summary>
    public void DropWhenReadQueueEmpty() => _dropWhenReadQueueEmpty = true;

    public Task<byte[]> ReadAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsConnected)
        {
            throw new InvalidOperationException("Not connected.");
        }

        if (_readQueue.Count == 0)
        {
            if (_dropWhenReadQueueEmpty)
            {
                _dropWhenReadQueueEmpty = false;
                IsConnected = false;
                throw new IOException("Simulated HID transport drop.");
            }

            throw new InvalidOperationException("No queued HID response available.");
        }

        return Task.FromResult(_readQueue.Dequeue());
    }

    public byte[] Read(TimeSpan? timeout = null)
    {
        return ReadAsync(timeout).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Optional failure injected by <see cref="DisconnectAsync"/>. Real HID handles do throw on
    /// close (the device was yanked, the handle is already dead), and cleanup paths are supposed
    /// to absorb that rather than let it mask the failure they were cleaning up after — which is
    /// only observable with a disconnect that actually throws.
    /// </summary>
    public Exception? DisconnectFailure { get; set; }

    public Task DisconnectAsync()
    {
        DisconnectCalls++;
        IsConnected = false;
        VendorId = null;
        ProductId = null;
        SerialNumber = null;
        DevicePath = null;

        return DisconnectFailure is { } failure
            ? Task.FromException(failure)
            : Task.CompletedTask;
    }

    public void Disconnect()
    {
        DisconnectAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
    }
}

internal sealed class FakeHidDeviceEnumerator : IHidDeviceEnumerator
{
    private readonly Queue<IReadOnlyList<HidDeviceInfo>> _responses;
    private readonly IReadOnlyList<HidDeviceInfo> _fallback;

    public FakeHidDeviceEnumerator(
        IReadOnlyList<IReadOnlyList<HidDeviceInfo>> responses,
        IReadOnlyList<HidDeviceInfo>? fallback = null)
    {
        _responses = new Queue<IReadOnlyList<HidDeviceInfo>>(responses);
        _fallback = fallback ?? Array.Empty<HidDeviceInfo>();
    }

    public Task<IReadOnlyList<HidDeviceInfo>> EnumerateAsync(
        int? vendorId = null,
        int? productId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_responses.Count > 0)
        {
            return Task.FromResult(_responses.Dequeue());
        }

        return Task.FromResult(_fallback);
    }
}

internal sealed class ThrowingHidDeviceEnumerator : IHidDeviceEnumerator
{
    private readonly Exception _exception;

    public ThrowingHidDeviceEnumerator(Exception exception)
    {
        _exception = exception;
    }

    public Task<IReadOnlyList<HidDeviceInfo>> EnumerateAsync(
        int? vendorId = null,
        int? productId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException<IReadOnlyList<HidDeviceInfo>>(_exception);
    }
}

internal sealed class FakeUsbLocationProvider : IUsbLocationProvider
{
    private readonly IReadOnlyDictionary<string, string> _locationsByDevicePath;

    public FakeUsbLocationProvider(IReadOnlyDictionary<string, string> locationsByDevicePath)
    {
        _locationsByDevicePath = locationsByDevicePath;
    }

    public List<string> Requests { get; } = [];

    public string? GetLocationKey(string portNameOrDevicePath)
    {
        Requests.Add(portNameOrDevicePath);
        return _locationsByDevicePath.TryGetValue(portNameOrDevicePath, out var location) ? location : null;
    }
}

internal sealed class FakeBootloaderProtocol : IBootloaderProtocol
{
    private readonly IReadOnlyList<byte[]> _hexRecords;
    private readonly IReadOnlyList<FlashCrcRegion> _crcRegions;

    public FakeBootloaderProtocol(
        IReadOnlyList<byte[]> hexRecords,
        IReadOnlyList<FlashCrcRegion>? crcRegions = null)
    {
        _hexRecords = hexRecords;
        _crcRegions = crcRegions ?? Array.Empty<FlashCrcRegion>();
    }

    public byte[] CreateRequestVersionMessage() => [0x11];
    public byte[] CreateEraseFlashMessage() => [0x22];
    public byte[] CreateProgramFlashMessage(byte[] hexRecord) => [0x33, .. hexRecord];
    public byte[] CreateReadCrcMessage(uint address, uint length) =>
        [0x44, .. BitConverter.GetBytes(address), .. BitConverter.GetBytes(length)];
    public byte[] CreateJumpToApplicationMessage() => [0x55];

    /// <summary>
    /// Optional override for <see cref="DecodeVersionResponse"/>. The default decoder only ever
    /// answers the literal "Error", which cannot show whether the session's rejection of a bad
    /// version is case-sensitive — a real protocol implementation is free to spell it differently.
    /// </summary>
    public Func<byte[], string>? VersionDecoder { get; set; }

    public string DecodeVersionResponse(byte[] data)
    {
        if (VersionDecoder is { } decoder)
        {
            return decoder(data);
        }

        if (data.Length == 0 || data[0] == 0xEE)
        {
            return "Error";
        }

        return "1.0";
    }

    public bool DecodeProgramFlashResponse(byte[] data)
    {
        return data.Length >= 2 && data[0] == 0x01 && data[1] == 0x03;
    }

    public bool DecodeEraseFlashResponse(byte[] data)
    {
        return data.Length >= 2 && data[0] == 0x01 && data[1] == 0x02;
    }

    // The enqueued READ_CRC HID response carries the device-reported CRC in
    // its first two bytes (little-endian), so a test drives match/mismatch
    // by enqueuing specific bytes.
    public ushort DecodeReadCrcResponse(byte[] data)
    {
        if (data.Length < 2)
        {
            throw new InvalidDataException("Fake READ_CRC response was too short.");
        }

        return (ushort)(data[0] | (data[1] << 8));
    }

    public IReadOnlyList<FlashCrcRegion> ComputeCrcRegions(string[] hexFileLines) => _crcRegions;

    public List<byte[]> ParseHexFile(string[] hexFileLines)
    {
        return _hexRecords.Select(record => record.ToArray()).ToList();
    }
}

internal sealed class CapturingProgress<T> : IProgress<T>
{
    private readonly ICollection<T> _items;

    public CapturingProgress(ICollection<T> items)
    {
        _items = items;
    }

    public void Report(T value)
    {
        _items.Add(value);
    }
}
