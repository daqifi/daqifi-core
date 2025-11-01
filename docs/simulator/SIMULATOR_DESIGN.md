# DAQiFi Device Simulator - Design Document

## Overview

This document outlines the design for a C# native device simulator that will enable comprehensive hardware-independent integration testing of the daqifi-core library. The simulator will accurately emulate DAQiFi hardware devices (Nyquist 1 and Nyquist 3) without requiring physical hardware or external dependencies.

## Goals

1. **Zero External Dependencies**: Pure C# implementation, no Java runtime or external processes required
2. **Protocol Accuracy**: Faithful implementation of SCPI, Protobuf, and UDP discovery protocols
3. **Test Enablement**: Unlock full integration testing without hardware
4. **Fault Injection**: Support simulated errors, timeouts, and edge cases
5. **Developer Productivity**: Fast test execution, easy debugging, deterministic behavior
6. **CI/CD Friendly**: Runs in any environment that supports .NET 8.0+

## Non-Goals

1. **Performance Testing**: Not designed for benchmarking (use real hardware)
2. **High-Fidelity Simulation**: Simplified analog signal generation (sine waves, not hardware-accurate noise)
3. **Firmware Emulation**: Does not emulate bootloader, firmware updates, or low-level hardware behavior
4. **Third-Party App Testing**: Designed for daqifi-core test suite, not as a general-purpose emulator

## Architecture

### Component Structure

```
Daqifi.Core.Tests/
├── Simulator/
│   ├── DeviceSimulator.cs              # Main orchestrator
│   ├── SimulatorConfiguration.cs       # Configuration options
│   ├── Protocols/
│   │   ├── UdpDiscoveryServer.cs       # UDP broadcast responder (port 30303)
│   │   ├── TcpDataServer.cs            # TCP data connection handler
│   │   └── ScpiCommandProcessor.cs     # SCPI command parser and executor
│   ├── Device/
│   │   ├── VirtualDevice.cs            # Base virtual device
│   │   ├── Nyquist1Device.cs           # NQ1-specific implementation
│   │   └── Nyquist3Device.cs           # NQ3-specific implementation
│   ├── Data/
│   │   ├── SignalGenerator.cs          # Base signal generation
│   │   ├── SineWaveGenerator.cs        # Sine wave data source
│   │   └── SquareWaveGenerator.cs      # Square wave (digital)
│   └── Testing/
│       └── FaultInjector.cs            # Simulated failures
├── Fixtures/
│   └── DeviceSimulatorFixture.cs       # xUnit collection fixture
└── Integration/
    └── SimulatedHardwareTests.cs       # Integration tests
```

### Class Responsibilities

#### 1. `DeviceSimulator`
**Purpose**: Main entry point for simulator lifecycle management

**Responsibilities**:
- Start/stop UDP discovery server
- Start/stop TCP data server
- Coordinate between protocol handlers and virtual devices
- Manage simulator lifecycle
- Thread-safe state management

**Public API**:
```csharp
public class DeviceSimulator : IDisposable
{
    public DeviceSimulator(SimulatorConfiguration config);
    public Task StartAsync();
    public Task StopAsync();
    public int TcpPort { get; }
    public int UdpPort { get; }
    public VirtualDevice Device { get; }
    public void Dispose();
}
```

#### 2. `SimulatorConfiguration`
**Purpose**: Configuration options for simulator behavior

**Responsibilities**:
- Device type selection (NQ1, NQ3)
- Network configuration (ports, serial number)
- Signal generation parameters
- Fault injection settings

**Public API**:
```csharp
public record SimulatorConfiguration
{
    public DeviceType DeviceType { get; init; } = DeviceType.Nyquist1;
    public int TcpPort { get; init; } = 0; // 0 = auto-assign
    public int UdpPort { get; init; } = 0; // 0 = auto-assign
    public ulong SerialNumber { get; init; } = 0; // 0 = auto-generate
    public string HostName { get; init; } = "SIMULATOR";
    public string FirmwareVersion { get; init; } = "1.0.0-test";
    public FaultInjectionOptions? FaultInjection { get; init; } = null;
}
```

#### 3. `UdpDiscoveryServer`
**Purpose**: Respond to UDP discovery broadcasts

**Responsibilities**:
- Listen on UDP port 30303 (or configured port)
- Parse incoming discovery queries (`"DAQiFi?\r\n"`)
- Send protobuf discovery responses
- Handle multiple simultaneous queries

**Protocol Details**:
- Receive: `"DAQiFi?\r\n"` (ASCII)
- Send: Length-delimited `DaqifiOutMessage` with device metadata

#### 4. `TcpDataServer`
**Purpose**: Handle TCP connections for SCPI commands and streaming

**Responsibilities**:
- Accept TCP connections on configured port
- Route data to ScpiCommandProcessor
- Send streaming data when active
- Handle multiple concurrent connections (if needed)

**Connection Lifecycle**:
1. Accept connection
2. Read SCPI commands (line-based, `\r\n` terminated)
3. Execute commands via ScpiCommandProcessor
4. Send responses
5. If streaming active, send continuous protobuf messages

#### 5. `ScpiCommandProcessor`
**Purpose**: Parse and execute SCPI commands

**Responsibilities**:
- Parse SCPI command strings
- Execute commands against VirtualDevice
- Generate appropriate responses
- Handle command errors

**Supported Commands**:
```
# System
SYSTem:REboot
SYSTem:SYSInfoPB?
SYSTem:FORceBoot
SYSTem:ECHO <-1|1>

# Streaming
SYSTem:StartStreamData <freq>
SYSTem:StopStreamData
SYSTem:STReam:FORmat <0|1|2>
SYSTem:STReam:FORmat?

# Configuration
ENAble:VOLTage:DC <binary_string>

# Digital I/O
DIO:PORt:DIRection <ch>,<dir>
DIO:PORt:STATe <ch>,<value>
DIO:PORt:ENAble <1|0>
```

#### 6. `VirtualDevice` (Abstract Base)
**Purpose**: Encapsulate device state and behavior

**Responsibilities**:
- Maintain device configuration
- Generate device info protobuf messages
- Manage channel state
- Abstract device-specific behavior

**State**:
- Serial number, hostname, firmware version
- Channel configuration (enabled, ranges, calibration)
- Streaming state (active, frequency)
- Power/battery/temperature status

#### 7. `Nyquist1Device` / `Nyquist3Device`
**Purpose**: Device-specific implementations

**Responsibilities**:
- Device-specific channel counts (NQ1: 16 analog, NQ3: 8 analog + 8 DAC)
- Device-specific metadata (part number, etc.)
- Device-specific capabilities

#### 8. `SignalGenerator` and Implementations
**Purpose**: Generate realistic analog/digital data

**Responsibilities**:
- Time-based signal generation
- Per-channel signal configuration
- Deterministic output for testing

**Implementations**:
- `SineWaveGenerator`: Configurable frequency, amplitude, phase offset
- `SquareWaveGenerator`: Digital signal generation

#### 9. `FaultInjector`
**Purpose**: Simulate error conditions

**Responsibilities**:
- Network errors (timeouts, disconnects)
- Protocol errors (malformed messages)
- Data errors (out-of-range values)
- Timing issues (delayed responses)

**Configuration**:
```csharp
public record FaultInjectionOptions
{
    public double? ConnectionFailureRate { get; init; } // 0.0 - 1.0
    public TimeSpan? ResponseDelay { get; init; }
    public bool SendMalformedProtobuf { get; init; } = false;
    public bool RandomDisconnects { get; init; } = false;
}
```

#### 10. `DeviceSimulatorFixture`
**Purpose**: xUnit fixture for test integration

**Responsibilities**:
- One simulator instance per test collection
- Automatic lifecycle management
- Port allocation to avoid conflicts

**Usage**:
```csharp
[Collection("DeviceSimulator")]
public class SimulatedHardwareTests
{
    private readonly DeviceSimulator _simulator;

    public SimulatedHardwareTests(DeviceSimulatorFixture fixture)
    {
        _simulator = fixture.Simulator;
    }

    [Fact]
    public async Task CanDiscoverSimulatedDevice() { ... }
}
```

## Protocol Implementation Details

### UDP Discovery Protocol

**Request**:
```
Broadcast to 255.255.255.255:30303
Content: "DAQiFi?\r\n" (ASCII)
```

**Response**:
```
Length-delimited DaqifiOutMessage containing:
- device_port (TCP port)
- host_name
- device_sn (serial number)
- device_fw_rev (firmware version)
- device_pn ("nq1" or "nq3")
- mac_addr (generated from port/serial)
- ip_addr (local IP)
- pwr_status (1 = on)
```

**Encoding**: Protobuf delimited format
- Varint length prefix
- Serialized DaqifiOutMessage

### SCPI Protocol

**Format**: ASCII text, `\r\n` line termination

**Command Structure**:
```
<COMMAND> [parameters]\r\n
```

**Response Types**:
1. **No response**: Most commands
2. **Query response**: `<value>\r\n` for commands ending with `?`
3. **Protobuf response**: `SYSTem:SYSInfoPB?` returns delimited protobuf

**Example Exchange**:
```
Client -> Server: "ENAble:VOLTage:DC 0000000011\r\n"
Server -> Client: (no response)

Client -> Server: "SYSTem:StartStreamData 100\r\n"
Server -> Client: (begins streaming protobuf messages)
```

### Streaming Protocol

**Activation**: `SYSTem:StartStreamData <frequency>`

**Behavior**:
1. Validate frequency (1-1000 Hz)
2. Begin continuous transmission of length-delimited protobuf messages
3. Each message contains:
   - `msg_time_stamp`: Incrementing timestamp
   - `analog_in_data`: Array of ADC values for enabled channels
   - `digital_data`: Digital I/O state
   - Channel metadata (ranges, calibration)

**Timing**:
- Messages sent at configured frequency (e.g., 100 Hz = every 10ms)
- Timestamp increments based on `timestamp_freq` (typically 1 MHz)
- Deterministic for testing (no jitter simulation)

**Deactivation**: `SYSTem:StopStreamData`

### Protobuf Message Generation

**Device Info Message** (for discovery and SYSInfoPB):
```csharp
var message = new DaqifiOutMessage
{
    DevicePort = (uint)tcpPort,
    HostName = "SIMULATOR",
    DeviceSn = 4788544735461581972,
    DeviceFwRev = "1.0.0-test",
    DevicePn = "nq1",
    DeviceHwRev = "1.0",
    MacAddr = ByteString.CopyFrom(GenerateMacAddress()),
    IpAddr = ByteString.CopyFrom(GetLocalIpAddress()),
    PwrStatus = 1,
    AnalogInPortNum = 16, // NQ1
    AnalogInRes = 4096,
    DigitalPortNum = 8,
    TimestampFreq = 1_000_000
};
```

**Streaming Data Message**:
```csharp
var message = new DaqifiOutMessage
{
    MsgTimeStamp = currentTimestamp,
    AnalogInData = { generatedValues }, // Only enabled channels
    DigitalData = ByteString.CopyFrom(digitalState),
    AnalogInPortRange = { enabledChannelRanges },
    AnalogInCalM = { calibrationSlopes },
    AnalogInCalB = { calibrationOffsets }
};
```

**Serialization**:
```csharp
message.WriteDelimitedTo(networkStream);
await networkStream.FlushAsync();
```

## Testing Integration

### Test Structure

**Collection Fixture Pattern**:
```csharp
[CollectionDefinition("DeviceSimulator")]
public class DeviceSimulatorCollection : ICollectionFixture<DeviceSimulatorFixture>
{
}

public class DeviceSimulatorFixture : IAsyncLifetime
{
    public DeviceSimulator Simulator { get; private set; }

    public async Task InitializeAsync()
    {
        var config = new SimulatorConfiguration
        {
            DeviceType = DeviceType.Nyquist1,
            TcpPort = 0, // Auto-assign
            UdpPort = 0
        };
        Simulator = new DeviceSimulator(config);
        await Simulator.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await Simulator.StopAsync();
        Simulator.Dispose();
    }
}
```

### Example Test Cases

**Discovery Test**:
```csharp
[Fact]
public async Task CanDiscoverSimulatedDevice()
{
    using var finder = new WiFiDeviceFinder(_simulator.UdpPort);
    var devices = await finder.DiscoverAsync(TimeSpan.FromSeconds(2));

    Assert.Single(devices);
    Assert.Equal("SIMULATOR", devices[0].HostName);
    Assert.Equal(DeviceType.Nyquist1, devices[0].DeviceType);
}
```

**Connection and Streaming Test**:
```csharp
[Fact]
public async Task CanConnectAndStreamData()
{
    var transport = new TcpStreamTransport("localhost", _simulator.TcpPort);
    using var device = new DaqifiStreamingDevice("Test", transport);

    device.Connect();

    // Configure channels
    device.Send(ScpiMessageProducer.EnableAnalogChannels("0000000011"));

    // Start streaming
    var messagesReceived = new List<DaqifiOutMessage>();
    device.MessageReceived += (s, e) =>
    {
        if (e.Message is ProtobufMessage pb)
            messagesReceived.Add(pb.Data);
    };

    device.StartStreaming(100);
    await Task.Delay(1000); // Collect ~100 messages
    device.StopStreaming();

    Assert.InRange(messagesReceived.Count, 90, 110); // ~100 messages ±10%
    Assert.All(messagesReceived, m => Assert.Equal(2, m.AnalogInData.Count)); // 2 channels
}
```

**Fault Injection Test**:
```csharp
[Fact]
public async Task HandlesUnexpectedDisconnect()
{
    var config = new SimulatorConfiguration
    {
        FaultInjection = new FaultInjectionOptions
        {
            RandomDisconnects = true
        }
    };

    using var simulator = new DeviceSimulator(config);
    await simulator.StartAsync();

    // Test that library handles disconnects gracefully
    // ...
}
```

## Implementation Plan

### Phase 1: Core Infrastructure
**Scope**: Basic simulator framework
**Deliverables**:
- `DeviceSimulator` class
- `SimulatorConfiguration` record
- `VirtualDevice` base class
- `Nyquist1Device` implementation
- Basic lifecycle management

**Acceptance Criteria**:
- Can create and dispose simulator
- Can start/stop simulator without errors
- No external dependencies

### Phase 2: UDP Discovery
**Scope**: Discovery protocol implementation
**Deliverables**:
- `UdpDiscoveryServer` class
- Discovery response generation
- Protobuf serialization

**Acceptance Criteria**:
- Responds to `"DAQiFi?\r\n"` broadcasts
- Returns valid `DaqifiOutMessage`
- WiFiDeviceFinder can discover simulator
- Tests pass with simulator

### Phase 3: TCP Connection & SCPI
**Scope**: TCP server and SCPI command handling
**Deliverables**:
- `TcpDataServer` class
- `ScpiCommandProcessor` class
- Command parsing and execution
- Essential SCPI commands

**Acceptance Criteria**:
- Accepts TCP connections
- Parses SCPI commands correctly
- Executes SYSInfoPB?, channel config commands
- DaqifiDevice can connect to simulator

### Phase 4: Streaming Implementation
**Scope**: Data streaming functionality
**Deliverables**:
- `SignalGenerator` and implementations
- Streaming state management
- Continuous protobuf message transmission
- StartStreamData/StopStreamData commands

**Acceptance Criteria**:
- Sends messages at configured frequency
- Generates realistic data
- Respects channel enable mask
- DaqifiStreamingDevice works with simulator
- Timing is accurate (±5%)

### Phase 5: Testing & Integration
**Scope**: Test suite and fixture
**Deliverables**:
- `DeviceSimulatorFixture` xUnit fixture
- Comprehensive integration tests
- Test coverage for all protocol paths
- Documentation and examples

**Acceptance Criteria**:
- All integration tests pass
- Code coverage >80% for simulator code
- Documentation is clear
- Examples are working

### Phase 6: Advanced Features (Optional)
**Scope**: Fault injection, NQ3 support
**Deliverables**:
- `FaultInjector` class
- `Nyquist3Device` implementation
- Additional SCPI commands
- Advanced test scenarios

**Acceptance Criteria**:
- Can simulate network failures
- Can simulate protocol errors
- NQ3 device type works
- Fault injection tests pass

## Testing Strategy

### Unit Tests
- Individual component tests (signal generators, parsers)
- Mock dependencies where appropriate
- Fast, isolated tests

### Integration Tests
- Full protocol flow tests
- Discovery → Connection → Configuration → Streaming
- Multi-device scenarios
- Error handling

### Regression Tests
- Ensure simulator matches real hardware behavior
- Validate against known-good captures
- Cross-check with Java emulator (optional)

## Success Metrics

1. **Test Coverage**: >80% code coverage on simulator code
2. **Test Speed**: Full integration test suite runs in <10 seconds
3. **Reliability**: Zero flaky tests due to simulator
4. **Maintainability**: <500 LOC per component
5. **Documentation**: All public APIs documented

## Risks and Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| Protocol mismatch with real hardware | High | Reference firmware code, validate with real devices |
| Port conflicts in CI | Medium | Use port 0 (auto-assign), fixture isolation |
| Flaky timing tests | Medium | Use generous timeouts, avoid absolute timing |
| Protobuf compatibility | High | Use same .proto files as firmware |
| Overcomplicated design | Medium | Start simple, iterate based on test needs |

## Future Enhancements

1. **Recording/Playback**: Capture real device traffic and replay
2. **Web UI**: Visual simulator control for demos
3. **Multiple Devices**: Simultaneous device simulation
4. **Performance Metrics**: Latency, throughput monitoring
5. **Serial Port Simulation**: Virtual COM port support

## References

- SCPI Commands: `/src/Daqifi.Core/Communication/Producers/ScpiMessageProducer.cs`
- Protobuf Schema: `/daqifi-java-api/src/main/proto/DaqifiOutMessage.proto`
- Discovery Protocol: `/src/Daqifi.Core/Device/Discovery/WiFiDeviceFinder.cs`
- Firmware Reference: `daqifi-nyquist-firmware` repository

## Appendix A: Network Protocol Summary

### UDP Discovery (Port 30303)
- **Query**: `"DAQiFi?\r\n"` (broadcast)
- **Response**: Length-delimited protobuf
- **Timeout**: 2 seconds typical

### TCP Data (Port 9760 or configured)
- **Connection**: Single persistent connection
- **Commands**: SCPI ASCII, `\r\n` terminated
- **Streaming**: Continuous protobuf messages
- **Encoding**: Mixed (ASCII commands, binary protobuf data)

## Appendix B: Protobuf Delimited Format

**Format**: `[varint length][message bytes]`

**Encoding**:
```csharp
// Write
message.WriteDelimitedTo(stream);

// Read
var message = DaqifiOutMessage.Parser.ParseDelimitedFrom(stream);
```

**Why Delimited**: Allows multiple messages on a single stream without framing protocol
