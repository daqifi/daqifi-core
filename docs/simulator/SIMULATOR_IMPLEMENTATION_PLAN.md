# DAQiFi Device Simulator - Implementation Plan

## Overview

This document provides a detailed, phase-by-phase implementation plan for building the C# native device simulator. Each phase is designed to deliver incremental, testable value while building toward the complete solution.

## Principles

1. **Incremental Delivery**: Each phase produces working, testable code
2. **Test-First**: Write tests alongside (or before) implementation
3. **Simple First**: Start with minimum viable implementation, refactor later
4. **Protocol Accuracy**: Validate against real devices and existing tests
5. **No Premature Optimization**: Focus on correctness, then performance

## Phase Breakdown

### Phase 1: Core Infrastructure (Estimated: 4-6 hours)

#### Objectives
- Establish project structure
- Create basic simulator lifecycle management
- Implement configuration system
- Set up virtual device abstraction

#### Tasks

**1.1 Create Directory Structure**
```bash
mkdir -p src/Daqifi.Core.Tests/Simulator/{Protocols,Device,Data,Testing}
mkdir -p src/Daqifi.Core.Tests/Fixtures
```

**1.2 Implement `SimulatorConfiguration.cs`**
```csharp
namespace Daqifi.Core.Tests.Simulator;

/// <summary>
/// Configuration options for the device simulator.
/// </summary>
public record SimulatorConfiguration
{
    /// <summary>
    /// Type of device to simulate (Nyquist1, Nyquist3).
    /// </summary>
    public DeviceType DeviceType { get; init; } = DeviceType.Nyquist1;

    /// <summary>
    /// TCP port for data connection. Use 0 for auto-assignment.
    /// </summary>
    public int TcpPort { get; init; } = 0;

    /// <summary>
    /// UDP port for discovery. Use 0 for auto-assignment.
    /// </summary>
    public int UdpPort { get; init; } = 0;

    /// <summary>
    /// Device serial number. Use 0 for auto-generation.
    /// </summary>
    public ulong SerialNumber { get; init; } = 0;

    /// <summary>
    /// Device hostname.
    /// </summary>
    public string HostName { get; init; } = "SIMULATOR";

    /// <summary>
    /// Firmware version string.
    /// </summary>
    public string FirmwareVersion { get; init; } = "1.0.0-sim";

    /// <summary>
    /// Hardware version string.
    /// </summary>
    public string HardwareVersion { get; init; } = "1.0";
}
```

**1.3 Implement `VirtualDevice.cs` (Abstract Base)**
```csharp
namespace Daqifi.Core.Tests.Simulator.Device;

/// <summary>
/// Base class for virtual device implementations.
/// </summary>
public abstract class VirtualDevice
{
    protected VirtualDevice(SimulatorConfiguration config)
    {
        Configuration = config;
        SerialNumber = config.SerialNumber != 0
            ? config.SerialNumber
            : GenerateSerialNumber();
    }

    public SimulatorConfiguration Configuration { get; }
    public ulong SerialNumber { get; }
    public abstract string PartNumber { get; }
    public abstract int AnalogInputChannels { get; }
    public abstract int AnalogOutputChannels { get; }
    public abstract int DigitalChannels { get; }
    public abstract int AdcResolution { get; }

    // State
    public bool IsStreaming { get; protected set; }
    public int StreamingFrequency { get; protected set; }
    public uint ChannelMask { get; protected set; }

    // Methods
    public abstract DaqifiOutMessage BuildDeviceInfoMessage(int tcpPort);
    public abstract DaqifiOutMessage BuildStreamingDataMessage(uint timestamp);

    protected virtual ulong GenerateSerialNumber()
    {
        return 4788544735461581972UL + (ulong)Random.Shared.Next(1000);
    }

    public virtual void StartStreaming(int frequency)
    {
        if (frequency < 1 || frequency > 1000)
            throw new ArgumentException("Frequency must be 1-1000 Hz", nameof(frequency));

        IsStreaming = true;
        StreamingFrequency = frequency;
    }

    public virtual void StopStreaming()
    {
        IsStreaming = false;
    }

    public virtual void SetChannelMask(uint mask)
    {
        ChannelMask = mask;
    }
}
```

**1.4 Implement `Nyquist1Device.cs`**
```csharp
namespace Daqifi.Core.Tests.Simulator.Device;

/// <summary>
/// Nyquist 1 device implementation (16 analog input channels).
/// </summary>
public class Nyquist1Device : VirtualDevice
{
    public Nyquist1Device(SimulatorConfiguration config) : base(config) { }

    public override string PartNumber => "nq1";
    public override int AnalogInputChannels => 16;
    public override int AnalogOutputChannels => 0;
    public override int DigitalChannels => 8;
    public override int AdcResolution => 4096;

    public override DaqifiOutMessage BuildDeviceInfoMessage(int tcpPort)
    {
        var message = new DaqifiOutMessage
        {
            DevicePort = (uint)tcpPort,
            HostName = Configuration.HostName,
            DeviceSn = SerialNumber,
            DeviceFwRev = Configuration.FirmwareVersion,
            DeviceHwRev = Configuration.HardwareVersion,
            DevicePn = PartNumber,
            PwrStatus = 1,
            BattStatus = 100,
            TempStatus = 25,
            DeviceStatus = 1,

            // Capabilities
            AnalogInPortNum = (uint)AnalogInputChannels,
            AnalogInRes = (uint)AdcResolution,
            DigitalPortNum = (uint)DigitalChannels,
            TimestampFreq = 1_000_000,

            // Network info
            MacAddr = ByteString.CopyFrom(GenerateMacAddress()),
            IpAddr = ByteString.CopyFrom(new byte[] { 127, 0, 0, 1 }),
            NetMask = ByteString.CopyFrom(Encoding.ASCII.GetBytes("255.255.255.0")),
            PrimaryDns = ByteString.CopyFrom(Encoding.ASCII.GetBytes("8.8.8.8")),
            SecondaryDns = ByteString.CopyFrom(Encoding.ASCII.GetBytes("1.1.1.1")),
            Ssid = "DAQiFi"
        };

        // Add range information for all channels
        for (int i = 0; i < AnalogInputChannels; i++)
        {
            message.AnalogInPortAvRange.Add(5.0f);
        }

        return message;
    }

    public override DaqifiOutMessage BuildStreamingDataMessage(uint timestamp)
    {
        // Implemented in Phase 4
        throw new NotImplementedException("Streaming not yet implemented");
    }

    private byte[] GenerateMacAddress()
    {
        // Generate deterministic MAC based on serial number
        var mac = new byte[6];
        mac[0] = 0x02; // Locally administered
        mac[1] = 0x00;
        var serialBytes = BitConverter.GetBytes(SerialNumber);
        Array.Copy(serialBytes, 0, mac, 2, 4);
        return mac;
    }
}
```

**1.5 Implement `DeviceSimulator.cs` (Main Orchestrator)**
```csharp
namespace Daqifi.Core.Tests.Simulator;

/// <summary>
/// Main device simulator orchestrator.
/// </summary>
public class DeviceSimulator : IDisposable
{
    private readonly SimulatorConfiguration _config;
    private VirtualDevice? _device;
    private bool _disposed;

    public DeviceSimulator(SimulatorConfiguration config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public int TcpPort { get; private set; }
    public int UdpPort { get; private set; }
    public VirtualDevice Device => _device ?? throw new InvalidOperationException("Simulator not started");

    public Task StartAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DeviceSimulator));
        if (_device != null) throw new InvalidOperationException("Already started");

        // Create virtual device
        _device = _config.DeviceType switch
        {
            DeviceType.Nyquist1 => new Nyquist1Device(_config),
            DeviceType.Nyquist3 => throw new NotImplementedException("NQ3 not yet supported"),
            _ => throw new ArgumentException($"Unknown device type: {_config.DeviceType}")
        };

        // Phase 2: Start UDP server
        // Phase 3: Start TCP server

        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // Phase 2: Stop UDP server
        // Phase 3: Stop TCP server
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;

        StopAsync().Wait();
        _disposed = true;
    }
}
```

#### Tests for Phase 1

**`SimulatorLifecycleTests.cs`**
```csharp
public class SimulatorLifecycleTests
{
    [Fact]
    public void Constructor_WithValidConfig_ShouldSucceed()
    {
        var config = new SimulatorConfiguration();
        using var simulator = new DeviceSimulator(config);
        Assert.NotNull(simulator);
    }

    [Fact]
    public async Task StartAsync_CreatesVirtualDevice()
    {
        var config = new SimulatorConfiguration { DeviceType = DeviceType.Nyquist1 };
        using var simulator = new DeviceSimulator(config);

        await simulator.StartAsync();

        Assert.NotNull(simulator.Device);
        Assert.IsType<Nyquist1Device>(simulator.Device);
    }

    [Fact]
    public async Task StartAsync_TwiceThrows()
    {
        using var simulator = new DeviceSimulator(new SimulatorConfiguration());
        await simulator.StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => simulator.StartAsync());
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var simulator = new DeviceSimulator(new SimulatorConfiguration());
        simulator.Dispose();
        simulator.Dispose(); // Should not throw
    }
}

public class VirtualDeviceTests
{
    [Fact]
    public void Nyquist1Device_HasCorrectCapabilities()
    {
        var config = new SimulatorConfiguration();
        var device = new Nyquist1Device(config);

        Assert.Equal("nq1", device.PartNumber);
        Assert.Equal(16, device.AnalogInputChannels);
        Assert.Equal(8, device.DigitalChannels);
        Assert.Equal(4096, device.AdcResolution);
    }

    [Fact]
    public void BuildDeviceInfoMessage_ReturnsValidMessage()
    {
        var config = new SimulatorConfiguration
        {
            HostName = "TEST",
            FirmwareVersion = "1.2.3",
            SerialNumber = 12345
        };
        var device = new Nyquist1Device(config);

        var message = device.BuildDeviceInfoMessage(9760);

        Assert.Equal("TEST", message.HostName);
        Assert.Equal("1.2.3", message.DeviceFwRev);
        Assert.Equal(12345u, message.DeviceSn);
        Assert.Equal(9760u, message.DevicePort);
        Assert.Equal("nq1", message.DevicePn);
    }
}
```

#### Acceptance Criteria
- [ ] All Phase 1 tests pass
- [ ] Can create simulator with configuration
- [ ] Can start/stop simulator
- [ ] Virtual device is created correctly
- [ ] Device info message is generated
- [ ] No external dependencies

---

### Phase 2: UDP Discovery (Estimated: 3-4 hours)

#### Objectives
- Implement UDP discovery server
- Respond to discovery queries
- Integrate with WiFiDeviceFinder

#### Tasks

**2.1 Implement `UdpDiscoveryServer.cs`**
```csharp
namespace Daqifi.Core.Tests.Simulator.Protocols;

/// <summary>
/// UDP discovery server that responds to broadcast queries.
/// </summary>
internal class UdpDiscoveryServer : IDisposable
{
    private readonly VirtualDevice _device;
    private readonly int _port;
    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;
    private Task? _listenerTask;

    public UdpDiscoveryServer(VirtualDevice device, int port, int tcpPort)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _port = port;
        TcpPort = tcpPort;
    }

    public int Port { get; private set; }
    public int TcpPort { get; }

    public Task StartAsync()
    {
        if (_udpClient != null) throw new InvalidOperationException("Already started");

        _udpClient = _port == 0
            ? new UdpClient(new IPEndPoint(IPAddress.Any, 0))
            : new UdpClient(_port);

        Port = ((IPEndPoint)_udpClient.Client.LocalEndPoint!).Port;

        _cts = new CancellationTokenSource();
        _listenerTask = Task.Run(() => ListenAsync(_cts.Token));

        return Task.CompletedTask;
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await _udpClient!.ReceiveAsync(cancellationToken);
                var query = Encoding.ASCII.GetString(result.Buffer);

                if (query == "DAQiFi?\r\n")
                {
                    await SendDiscoveryResponseAsync(result.RemoteEndPoint);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // Log and continue
            }
        }
    }

    private async Task SendDiscoveryResponseAsync(IPEndPoint remoteEndPoint)
    {
        var message = _device.BuildDeviceInfoMessage(TcpPort);

        using var memoryStream = new MemoryStream();
        message.WriteDelimitedTo(memoryStream);
        var bytes = memoryStream.ToArray();

        await _udpClient!.SendAsync(bytes, bytes.Length, remoteEndPoint);
    }

    public async Task StopAsync()
    {
        if (_cts == null) return;

        _cts.Cancel();
        if (_listenerTask != null)
        {
            await _listenerTask;
        }
    }

    public void Dispose()
    {
        StopAsync().Wait();
        _cts?.Dispose();
        _udpClient?.Dispose();
    }
}
```

**2.2 Update `DeviceSimulator.StartAsync()` to start UDP server**

#### Tests for Phase 2

**`UdpDiscoveryTests.cs`**
```csharp
public class UdpDiscoveryTests : IDisposable
{
    private readonly DeviceSimulator _simulator;

    public UdpDiscoveryTests()
    {
        var config = new SimulatorConfiguration { DeviceType = DeviceType.Nyquist1 };
        _simulator = new DeviceSimulator(config);
        _simulator.StartAsync().Wait();
    }

    [Fact]
    public async Task DiscoverAsync_FindsSimulatedDevice()
    {
        using var finder = new WiFiDeviceFinder(_simulator.UdpPort);

        var devices = await finder.DiscoverAsync(TimeSpan.FromSeconds(2));

        Assert.Single(devices);
        var device = devices[0];
        Assert.Equal("SIMULATOR", device.HostName);
        Assert.Equal(DeviceType.Nyquist1, device.DeviceType);
    }

    [Fact]
    public async Task DiscoverAsync_MultipleTimes_ConsistentResults()
    {
        using var finder = new WiFiDeviceFinder(_simulator.UdpPort);

        var devices1 = await finder.DiscoverAsync(TimeSpan.FromSeconds(1));
        var devices2 = await finder.DiscoverAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(devices1.Count, devices2.Count);
        Assert.Equal(devices1[0].SerialNumber, devices2[0].SerialNumber);
    }

    public void Dispose()
    {
        _simulator.Dispose();
    }
}
```

#### Acceptance Criteria
- [ ] All Phase 2 tests pass
- [ ] WiFiDeviceFinder can discover simulator
- [ ] Discovery response contains valid device info
- [ ] Multiple discoveries work consistently
- [ ] Port auto-assignment works

---

### Phase 3: TCP Connection & SCPI (Estimated: 6-8 hours)

#### Objectives
- Implement TCP server
- Parse SCPI commands
- Execute basic commands
- Enable device connection

#### Tasks

**3.1 Implement `ScpiCommandProcessor.cs`**

```csharp
namespace Daqifi.Core.Tests.Simulator.Protocols;

/// <summary>
/// Processes SCPI commands and executes them against a virtual device.
/// </summary>
internal class ScpiCommandProcessor
{
    private readonly VirtualDevice _device;
    private readonly int _tcpPort;

    public ScpiCommandProcessor(VirtualDevice device, int tcpPort)
    {
        _device = device;
        _tcpPort = tcpPort;
    }

    public async Task<byte[]?> ProcessCommandAsync(string command)
    {
        command = command.Trim().ToLowerInvariant();

        // System commands
        if (command == "system:reboot")
        {
            return null; // No response
        }
        else if (command == "system:sysinfopb?")
        {
            return await BuildProtobufResponseAsync();
        }
        else if (command.StartsWith("system:echo "))
        {
            return null;
        }
        else if (command.StartsWith("system:startstreamdata "))
        {
            var freq = int.Parse(command.Split(' ')[1]);
            _device.StartStreaming(freq);
            return null;
        }
        else if (command == "system:stopstreamdata")
        {
            _device.StopStreaming();
            return null;
        }
        // Channel configuration
        else if (command.StartsWith("enable:voltage:dc "))
        {
            var binaryString = command.Split(' ')[1];
            var mask = Convert.ToUInt32(binaryString, 2);
            _device.SetChannelMask(mask);
            return null;
        }

        // Unknown command
        return null;
    }

    private async Task<byte[]> BuildProtobufResponseAsync()
    {
        var message = _device.BuildDeviceInfoMessage(_tcpPort);
        using var memoryStream = new MemoryStream();
        message.WriteDelimitedTo(memoryStream);
        return memoryStream.ToArray();
    }
}
```

**3.2 Implement `TcpDataServer.cs`**

```csharp
namespace Daqifi.Core.Tests.Simulator.Protocols;

/// <summary>
/// TCP server for handling data connections and SCPI commands.
/// </summary>
internal class TcpDataServer : IDisposable
{
    private readonly VirtualDevice _device;
    private readonly int _port;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;

    public TcpDataServer(VirtualDevice device, int port)
    {
        _device = device;
        _port = port;
    }

    public int Port { get; private set; }

    public Task StartAsync()
    {
        if (_listener != null) throw new InvalidOperationException("Already started");

        var endpoint = _port == 0
            ? new IPEndPoint(IPAddress.Loopback, 0)
            : new IPEndPoint(IPAddress.Loopback, _port);

        _listener = new TcpListener(endpoint);
        _listener.Start();

        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _cts = new CancellationTokenSource();
        _acceptTask = Task.Run(() => AcceptClientsAsync(_cts.Token));

        return Task.CompletedTask;
    }

    private async Task AcceptClientsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(() => HandleClientAsync(client, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII);
            var processor = new ScpiCommandProcessor(_device, Port);

            try
            {
                while (!cancellationToken.IsCancellationRequested && client.Connected)
                {
                    var command = await reader.ReadLineAsync(cancellationToken);
                    if (command == null) break;

                    var response = await processor.ProcessCommandAsync(command);
                    if (response != null)
                    {
                        await stream.WriteAsync(response, 0, response.Length, cancellationToken);
                        await stream.FlushAsync(cancellationToken);
                    }

                    // TODO Phase 4: If streaming, send continuous data
                }
            }
            catch (Exception)
            {
                // Client disconnected or error
            }
        }
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        _listener?.Stop();
        if (_acceptTask != null)
        {
            await _acceptTask;
        }
    }

    public void Dispose()
    {
        StopAsync().Wait();
        _cts?.Dispose();
    }
}
```

**3.3 Update `DeviceSimulator` to start TCP server**

#### Tests for Phase 3

**`TcpConnectionTests.cs`**
```csharp
[Collection("DeviceSimulator")]
public class TcpConnectionTests
{
    private readonly DeviceSimulator _simulator;

    public TcpConnectionTests(DeviceSimulatorFixture fixture)
    {
        _simulator = fixture.Simulator;
    }

    [Fact]
    public void CanConnectToSimulator()
    {
        var transport = new TcpStreamTransport("localhost", _simulator.TcpPort);
        using var device = new DaqifiDevice("Test", transport);

        device.Connect();

        Assert.Equal(ConnectionStatus.Connected, device.Status);
    }

    [Fact]
    public void CanSendScpiCommand()
    {
        var transport = new TcpStreamTransport("localhost", _simulator.TcpPort);
        using var device = new DaqifiDevice("Test", transport);

        device.Connect();
        device.Send(ScpiMessageProducer.GetDeviceInfo);

        // Should not throw
    }

    [Fact]
    public async Task ReceivesDeviceInfoProtobuf()
    {
        var transport = new TcpStreamTransport("localhost", _simulator.TcpPort);
        using var device = new DaqifiDevice("Test", transport);

        DaqifiOutMessage? receivedMessage = null;
        device.MessageReceived += (s, e) =>
        {
            if (e.Message is ProtobufMessage pb)
                receivedMessage = pb.Data;
        };

        device.Connect();
        device.Send(ScpiMessageProducer.GetDeviceInfo);

        await Task.Delay(500); // Allow message processing

        Assert.NotNull(receivedMessage);
        Assert.Equal("SIMULATOR", receivedMessage.HostName);
    }
}
```

#### Acceptance Criteria
- [ ] All Phase 3 tests pass
- [ ] Can connect to simulator via TCP
- [ ] SCPI commands are parsed correctly
- [ ] SYSInfoPB? returns valid protobuf
- [ ] Channel configuration commands work

---

### Phase 4: Streaming Implementation (Estimated: 6-8 hours)

#### Objectives
- Implement signal generation
- Send continuous streaming data
- Accurate timing

#### Tasks

**4.1 Implement `SignalGenerator.cs` and `SineWaveGenerator.cs`**

(See design document for details)

**4.2 Update `VirtualDevice.BuildStreamingDataMessage()`**

**4.3 Update `TcpDataServer` to send streaming data**

**4.4 Comprehensive streaming tests**

#### Acceptance Criteria
- [ ] Streaming starts/stops correctly
- [ ] Data is generated at configured frequency
- [ ] Only enabled channels send data
- [ ] Timing is accurate (±5%)

---

### Phase 5: Testing & Integration (Estimated: 4-6 hours)

#### Objectives
- Create xUnit fixture
- Comprehensive test suite
- Documentation

#### Tasks

**5.1 Implement `DeviceSimulatorFixture.cs`**

**5.2 Create comprehensive integration tests**

**5.3 Update README with examples**

#### Acceptance Criteria
- [ ] All integration tests pass
- [ ] Code coverage >80%
- [ ] Documentation complete

---

## Implementation Timeline

| Phase | Duration | Dependencies | Deliverable |
|-------|----------|--------------|-------------|
| 1. Core Infrastructure | 4-6h | None | Basic simulator lifecycle |
| 2. UDP Discovery | 3-4h | Phase 1 | Discovery working |
| 3. TCP & SCPI | 6-8h | Phase 1 | Connection & commands |
| 4. Streaming | 6-8h | Phase 3 | Full streaming |
| 5. Testing | 4-6h | Phase 4 | Production ready |

**Total Estimated Time**: 23-32 hours

## Success Criteria

- [ ] All unit tests pass
- [ ] All integration tests pass
- [ ] Code coverage >80% on simulator code
- [ ] Can discover simulated device
- [ ] Can connect to simulated device
- [ ] Can configure channels
- [ ] Can stream data at 1-1000 Hz
- [ ] Protobuf messages are valid
- [ ] Documentation is complete
- [ ] Zero external dependencies

## Next Steps

After implementation is complete:
1. Create GitHub issues based on phases
2. Implement phase-by-phase
3. Code review after each phase
4. Merge to main branch
5. Consider Phase 6 (fault injection, NQ3)
