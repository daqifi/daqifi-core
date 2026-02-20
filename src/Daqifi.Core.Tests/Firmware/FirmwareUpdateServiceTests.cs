using System.Net;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Daqifi.Core.Firmware;
using Microsoft.Extensions.Logging.Abstractions;

namespace Daqifi.Core.Tests.Firmware;

public class FirmwareUpdateServiceTests
{
    [Fact]
    public async Task UpdateFirmwareAsync_HappyPath_TransitionsThroughExpectedStatesAndReportsProgress()
    {
        var device = new FakeStreamingDevice("COM3");
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0x01, 0x10]); // version
        hidTransport.EnqueueRead([0x01, 0x02]); // erase ack
        hidTransport.EnqueueRead([0x01, 0x03]); // program ack 1
        hidTransport.EnqueueRead([0x01, 0x03]); // program ack 2
        hidTransport.EnqueueRead([0x01, 0x10]); // verify version

        var enumerator = new FakeHidDeviceEnumerator([
            Array.Empty<HidDeviceInfo>(),
            [new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN-1", "DAQiFi Bootloader")]
        ]);

        var bootloaderProtocol = new FakeBootloaderProtocol(
            [
                [0xA1, 0x01],
                [0xA1, 0x02]
            ]);

        var options = CreateFastOptions();
        var service = new FirmwareUpdateService(
            hidTransport,
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            bootloaderProtocol,
            enumerator,
            options);

        var stateTransitions = new List<FirmwareUpdateState>();
        service.StateChanged += (_, args) => stateTransitions.Add(args.CurrentState);

        var progressEvents = new List<FirmwareUpdateProgress>();
        var progress = new CapturingProgress<FirmwareUpdateProgress>(progressEvents);

        var hexPath = CreateTempFile();
        try
        {
            await service.UpdateFirmwareAsync(device, hexPath, progress);
        }
        finally
        {
            File.Delete(hexPath);
        }

        Assert.Equal(FirmwareUpdateState.Complete, service.CurrentState);
        Assert.Equal(
            [
                FirmwareUpdateState.PreparingDevice,
                FirmwareUpdateState.WaitingForBootloader,
                FirmwareUpdateState.Connecting,
                FirmwareUpdateState.ErasingFlash,
                FirmwareUpdateState.Programming,
                FirmwareUpdateState.Verifying,
                FirmwareUpdateState.JumpingToApp,
                FirmwareUpdateState.Complete
            ],
            stateTransitions);

        Assert.Equal("SYSTem:FORceBoot", Assert.Single(device.SentCommands));
        Assert.Equal(6, hidTransport.Writes.Count);
        Assert.Equal(1, device.DisconnectCalls);
        Assert.True(device.ConnectAttempts >= 1);

        var terminalProgress = Assert.Single(progressEvents, p => p.State == FirmwareUpdateState.Complete);
        Assert.Equal(100, terminalProgress.PercentComplete);
    }

    [Fact]
    public async Task UpdateFirmwareAsync_WhenBootloaderNeverAppears_ThrowsFirmwareUpdateExceptionWithStateContext()
    {
        var device = new FakeStreamingDevice("COM3");
        var hidTransport = new FakeHidTransport();
        var enumerator = new FakeHidDeviceEnumerator([], Array.Empty<HidDeviceInfo>());
        var bootloaderProtocol = new FakeBootloaderProtocol([[0x10]]);

        var options = CreateFastOptions();
        options.WaitingForBootloaderTimeout = TimeSpan.FromMilliseconds(180);
        options.PollInterval = TimeSpan.FromMilliseconds(25);

        var service = new FirmwareUpdateService(
            hidTransport,
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            bootloaderProtocol,
            enumerator,
            options);

        var hexPath = CreateTempFile();
        FirmwareUpdateException exception;
        try
        {
            exception = await Assert.ThrowsAsync<FirmwareUpdateException>(
                () => service.UpdateFirmwareAsync(device, hexPath));
        }
        finally
        {
            File.Delete(hexPath);
        }

        Assert.Equal(FirmwareUpdateState.WaitingForBootloader, exception.FailedState);
        Assert.Equal(FirmwareUpdateState.Failed, service.CurrentState);
        Assert.NotNull(exception.RecoveryGuidance);

        var timeoutException = Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.Contains("No matching HID bootloader device was enumerated", timeoutException.Message);
        Assert.Contains("VID=0x04D8", timeoutException.Message);
        Assert.Contains("PID=0x003C", timeoutException.Message);
    }

    [Fact]
    public async Task UpdateFirmwareAsync_WhenCanceled_ThrowsOperationCanceledExceptionAndTransitionsToFailed()
    {
        var device = new FakeStreamingDevice("COM3");
        var hidTransport = new FakeHidTransport();
        var enumerator = new FakeHidDeviceEnumerator([], Array.Empty<HidDeviceInfo>());
        var bootloaderProtocol = new FakeBootloaderProtocol([[0x10]]);
        var options = CreateFastOptions();
        options.WaitingForBootloaderTimeout = TimeSpan.FromSeconds(5);

        var service = new FirmwareUpdateService(
            hidTransport,
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            bootloaderProtocol,
            enumerator,
            options);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));
        var hexPath = CreateTempFile();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => service.UpdateFirmwareAsync(device, hexPath, cancellationToken: cts.Token));
        }
        finally
        {
            File.Delete(hexPath);
        }

        Assert.Equal(FirmwareUpdateState.Failed, service.CurrentState);
    }

    [Fact]
    public async Task UpdateFirmwareAsync_WhenProgramAckFails_RetriesAndCompletes()
    {
        var device = new FakeStreamingDevice("COM3");
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0x01, 0x10]); // version
        hidTransport.EnqueueRead([0x01, 0x02]); // erase ack
        hidTransport.EnqueueRead([0x00]);       // invalid program ack (first attempt)
        hidTransport.EnqueueRead([0x01, 0x03]); // retry program ack (record 1)
        hidTransport.EnqueueRead([0x01, 0x03]); // record 2
        hidTransport.EnqueueRead([0x01, 0x10]); // verify version

        var enumerator = new FakeHidDeviceEnumerator([
            [new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN-1", "DAQiFi Bootloader")]
        ]);

        var bootloaderProtocol = new FakeBootloaderProtocol(
            [
                [0xA1, 0x01],
                [0xA1, 0x02]
            ]);

        var options = CreateFastOptions();
        options.FlashWriteRetryCount = 2;
        options.FlashWriteRetryDelay = TimeSpan.FromMilliseconds(5);

        var service = new FirmwareUpdateService(
            hidTransport,
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            bootloaderProtocol,
            enumerator,
            options);

        var hexPath = CreateTempFile();
        try
        {
            await service.UpdateFirmwareAsync(device, hexPath);
        }
        finally
        {
            File.Delete(hexPath);
        }

        Assert.Equal(FirmwareUpdateState.Complete, service.CurrentState);
        Assert.Equal(7, hidTransport.Writes.Count); // request version + erase + 3 program sends + verify + jump
        Assert.Equal(3, hidTransport.Writes.Count(write => write.Length > 0 && write[0] == 0x33));
    }

    [Fact]
    public async Task UpdateFirmwareAsync_WhenHidEnumerationThrows_ThrowsFirmwareUpdateExceptionWithEnumerationContext()
    {
        var device = new FakeStreamingDevice("COM3");
        var hidTransport = new FakeHidTransport();
        var enumerator = new ThrowingHidDeviceEnumerator(new InvalidOperationException("HID backend unavailable."));
        var bootloaderProtocol = new FakeBootloaderProtocol([[0x10]]);
        var options = CreateFastOptions();

        var service = new FirmwareUpdateService(
            hidTransport,
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            bootloaderProtocol,
            enumerator,
            options);

        var hexPath = CreateTempFile();
        FirmwareUpdateException exception;
        try
        {
            exception = await Assert.ThrowsAsync<FirmwareUpdateException>(
                () => service.UpdateFirmwareAsync(device, hexPath));
        }
        finally
        {
            File.Delete(hexPath);
        }

        Assert.Equal(FirmwareUpdateState.WaitingForBootloader, exception.FailedState);
        Assert.Equal(FirmwareUpdateState.Failed, service.CurrentState);

        var hidEnumerationException = Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("HID enumeration failed while searching for bootloader device", hidEnumerationException.Message);
        Assert.Contains("VID=0x04D8", hidEnumerationException.Message);
        Assert.Contains("PID=0x003C", hidEnumerationException.Message);
        Assert.NotNull(hidEnumerationException.InnerException);
    }

    [Fact]
    public async Task UpdateWifiModuleAsync_HappyPath_SendsExpectedCommandsAndReportsProgressFromProcessOutput()
    {
        var device = new FakeStreamingDevice("COM7");
        var externalProcessRunner = new FakeExternalProcessRunner
        {
            NextResult = new ExternalProcessResult(
                0,
                timedOut: false,
                TimeSpan.FromMilliseconds(10),
                ["begin write operation", "67%", "begin verify operation"],
                [])
        };

        var options = CreateFastOptions();
        options.PostLanFirmwareModeDelay = TimeSpan.FromMilliseconds(5);
        options.PostWifiReconnectDelay = TimeSpan.FromMilliseconds(5);

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService(),
            externalProcessRunner,
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            options);

        var progressEvents = new List<FirmwareUpdateProgress>();
        var progress = new CapturingProgress<FirmwareUpdateProgress>(progressEvents);

        var firmwareDir = CreateTempDirectory();
        File.WriteAllText(Path.Combine(firmwareDir, "winc_flash_tool.cmd"), "@echo off");

        try
        {
            await service.UpdateWifiModuleAsync(device, firmwareDir, progress);
        }
        finally
        {
            Directory.Delete(firmwareDir, recursive: true);
        }

        Assert.Equal(FirmwareUpdateState.Complete, service.CurrentState);
        Assert.Equal(
            [
                "SYSTem:COMMUnicate:LAN:FWUpdate",
                "SYSTem:COMMunicate:LAN:ENAbled 1",
                "SYSTem:COMMunicate:LAN:APPLY",
                "SYSTem:COMMunicate:LAN:SAVE"
            ],
            device.SentCommands);

        Assert.Equal(1, device.DisconnectCalls);
        Assert.True(device.ConnectAttempts >= 1);

        Assert.NotNull(externalProcessRunner.LastRequest);
        Assert.Contains(progressEvents, p => p.State == FirmwareUpdateState.Programming && p.PercentComplete > 20);
        Assert.Contains(progressEvents, p => p.State == FirmwareUpdateState.Complete && p.PercentComplete == 100);
    }

    [Fact]
    public async Task UpdateWifiModuleAsync_WhenProcessFails_ThrowsFirmwareUpdateExceptionWithProgrammingState()
    {
        var device = new FakeStreamingDevice("COM8");
        var externalProcessRunner = new FakeExternalProcessRunner
        {
            NextResult = new ExternalProcessResult(
                1,
                timedOut: false,
                TimeSpan.FromMilliseconds(10),
                ["some output"],
                ["fatal"])
        };

        var service = new FirmwareUpdateService(
            new FakeHidTransport(),
            new FakeFirmwareDownloadService(),
            externalProcessRunner,
            NullLogger<FirmwareUpdateService>.Instance,
            new FakeBootloaderProtocol([[0x10]]),
            new FakeHidDeviceEnumerator([]),
            CreateFastOptions());

        var firmwareDir = CreateTempDirectory();
        File.WriteAllText(Path.Combine(firmwareDir, "winc_flash_tool.cmd"), "@echo off");

        try
        {
            var exception = await Assert.ThrowsAsync<FirmwareUpdateException>(
                () => service.UpdateWifiModuleAsync(device, firmwareDir));

            Assert.Equal(FirmwareUpdateState.Programming, exception.FailedState);
            Assert.Equal(FirmwareUpdateState.Failed, service.CurrentState);
        }
        finally
        {
            Directory.Delete(firmwareDir, recursive: true);
        }
    }

    private static FirmwareUpdateServiceOptions CreateFastOptions()
    {
        return new FirmwareUpdateServiceOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(20),
            PreparingDeviceTimeout = TimeSpan.FromSeconds(2),
            WaitingForBootloaderTimeout = TimeSpan.FromSeconds(2),
            ConnectingTimeout = TimeSpan.FromSeconds(2),
            ErasingFlashTimeout = TimeSpan.FromSeconds(2),
            ProgrammingTimeout = TimeSpan.FromSeconds(5),
            VerifyingTimeout = TimeSpan.FromSeconds(2),
            JumpingToApplicationTimeout = TimeSpan.FromSeconds(2),
            BootloaderResponseTimeout = TimeSpan.FromMilliseconds(250),
            PostForceBootDelay = TimeSpan.FromMilliseconds(5),
            PostLanFirmwareModeDelay = TimeSpan.FromMilliseconds(5),
            PostWifiReconnectDelay = TimeSpan.FromMilliseconds(5),
            HidConnectRetryDelay = TimeSpan.FromMilliseconds(5),
            FlashWriteRetryDelay = TimeSpan.FromMilliseconds(5),
            WifiProcessTimeout = TimeSpan.FromSeconds(2)
        };
    }

    private static string CreateTempFile()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "test");
        return path;
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "daqifi-core-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeStreamingDevice : IStreamingDevice
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
        public int StreamingFrequency { get; set; }
        public bool IsStreaming { get; private set; }

        public int ConnectAttempts { get; private set; }
        public int DisconnectCalls { get; private set; }

        public int ConnectFailuresBeforeSuccess { get; set; }
        public List<string> SentCommands { get; } = [];

        public event EventHandler<DeviceStatusEventArgs>? StatusChanged;
        public event EventHandler<MessageReceivedEventArgs>? MessageReceived
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

        public void Send<T>(IOutboundMessage<T> message)
        {
            if (message is IOutboundMessage<string> textMessage)
            {
                SentCommands.Add(textMessage.Data);
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
    }

    private sealed class FakeHidTransport : IHidTransport
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

        public Task ConnectAsync(int vendorId, int productId, string? serialNumber = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConnectAttempts++;
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

        public Task WriteAsync(byte[] data, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsConnected)
            {
                throw new InvalidOperationException("Not connected.");
            }

            Writes.Add(data.ToArray());
            return Task.CompletedTask;
        }

        public void Write(byte[] data)
        {
            WriteAsync(data).GetAwaiter().GetResult();
        }

        public Task<byte[]> ReadAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsConnected)
            {
                throw new InvalidOperationException("Not connected.");
            }

            if (_readQueue.Count == 0)
            {
                throw new InvalidOperationException("No queued HID response available.");
            }

            return Task.FromResult(_readQueue.Dequeue());
        }

        public byte[] Read(TimeSpan? timeout = null)
        {
            return ReadAsync(timeout).GetAwaiter().GetResult();
        }

        public Task DisconnectAsync()
        {
            DisconnectCalls++;
            IsConnected = false;
            VendorId = null;
            ProductId = null;
            SerialNumber = null;
            DevicePath = null;
            return Task.CompletedTask;
        }

        public void Disconnect()
        {
            DisconnectAsync().GetAwaiter().GetResult();
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeHidDeviceEnumerator : IHidDeviceEnumerator
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

    private sealed class ThrowingHidDeviceEnumerator : IHidDeviceEnumerator
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

    private sealed class FakeBootloaderProtocol : IBootloaderProtocol
    {
        private readonly IReadOnlyList<byte[]> _hexRecords;

        public FakeBootloaderProtocol(IReadOnlyList<byte[]> hexRecords)
        {
            _hexRecords = hexRecords;
        }

        public byte[] CreateRequestVersionMessage() => [0x11];
        public byte[] CreateEraseFlashMessage() => [0x22];
        public byte[] CreateProgramFlashMessage(byte[] hexRecord) => [0x33, .. hexRecord];
        public byte[] CreateJumpToApplicationMessage() => [0x55];

        public string DecodeVersionResponse(byte[] data)
        {
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

        public List<byte[]> ParseHexFile(string[] hexFileLines)
        {
            return _hexRecords.Select(record => record.ToArray()).ToList();
        }
    }

    private sealed class FakeExternalProcessRunner : IExternalProcessRunner
    {
        public ExternalProcessResult NextResult { get; set; } = new(0, false, TimeSpan.Zero, [], []);
        public ExternalProcessRequest? LastRequest { get; private set; }

        public Task<ExternalProcessResult> RunAsync(
            ExternalProcessRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;

            foreach (var line in NextResult.StandardOutputLines)
            {
                request.OnStandardOutputLine?.Invoke(line);
                request.StandardInputResponseFactory?.Invoke(line);
            }

            foreach (var line in NextResult.StandardErrorLines)
            {
                request.OnStandardErrorLine?.Invoke(line);
            }

            return Task.FromResult(NextResult);
        }
    }

    private sealed class FakeFirmwareDownloadService : IFirmwareDownloadService
    {
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

        public void InvalidateCache()
        {
        }
    }

    private sealed class CapturingProgress<T> : IProgress<T>
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
}
