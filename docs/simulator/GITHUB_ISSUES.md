# GitHub Issues for Device Simulator Implementation

This document contains the GitHub issues to be created for the device simulator implementation.

---

## Epic Issue: C# Native Device Simulator for Hardware-Independent Testing

### Title
Implement C# Native Device Simulator for Hardware-Independent Integration Testing

### Labels
`enhancement`, `testing`, `infrastructure`

### Description

## Problem Statement

The daqifi-core library currently lacks comprehensive integration testing capabilities that don't require physical DAQiFi hardware. This creates several challenges:

1. **Manual Testing Dependency**: Developers must have physical Nyquist devices to validate protocol implementation
2. **CI/CD Limitations**: Automated testing is limited to unit tests with mocked transports
3. **Third-Party Developer Risk**: Breaking changes to protocols could affect external users without detection
4. **Regression Testing**: Difficult to validate bug fixes end-to-end without hardware

## Proposed Solution

Implement a pure C# device simulator that accurately emulates DAQiFi Nyquist 1 and Nyquist 3 devices, enabling:

- Full protocol validation (UDP discovery, TCP connections, SCPI commands, Protobuf streaming)
- Hardware-independent integration testing in CI/CD pipelines
- Deterministic test execution
- Fault injection for error handling validation
- Fast test execution (no hardware setup delays)

## Design Documents

- **Design Document**: `SIMULATOR_DESIGN.md`
- **Implementation Plan**: `SIMULATOR_IMPLEMENTATION_PLAN.md`
- **Protocol Specification**: See design document appendices

## Architecture Overview

```
DeviceSimulator (orchestrator)
├── UdpDiscoveryServer (port 30303)
├── TcpDataServer (port 9760 or auto-assigned)
├── ScpiCommandProcessor (command parser)
├── VirtualDevice (Nyquist1Device, Nyquist3Device)
└── SignalGenerator (data generation)
```

## Implementation Phases

This epic is broken down into 5 implementation phases (see child issues):

1. **Phase 1**: Core Infrastructure (~6h)
2. **Phase 2**: UDP Discovery (~4h)
3. **Phase 3**: TCP Connection & SCPI (~8h)
4. **Phase 4**: Streaming Implementation (~8h)
5. **Phase 5**: Testing & Integration (~6h)

**Total Estimated Effort**: 32 hours

## Success Criteria

- [ ] Can discover simulator via `WiFiDeviceFinder`
- [ ] Can connect to simulator via `TcpStreamTransport`
- [ ] Can send SCPI commands and receive responses
- [ ] Can configure channels and start/stop streaming
- [ ] Receives valid protobuf streaming data at configured frequency (1-1000 Hz)
- [ ] All integration tests pass with simulator
- [ ] Code coverage >80% on simulator code
- [ ] Zero external dependencies (no Java runtime required)
- [ ] Documentation complete with usage examples

## Non-Goals

- High-fidelity analog simulation (realistic noise, non-linearities)
- Firmware update simulation
- Performance benchmarking (use real hardware)
- Serial port simulation (future enhancement)

## Benefits

1. **Developer Productivity**: Fast, local integration testing without hardware
2. **CI/CD Enablement**: Automated integration tests in GitHub Actions
3. **Quality Assurance**: Catch protocol regressions before release
4. **Third-Party Confidence**: Validated protocol stability
5. **Maintainability**: Single-language codebase (no Java dependency)

## Related Work

- Existing Java emulator (`daqifi-java-api`) provides validation reference
- Current mock transports (`MockMemoryStreamTransport`) demonstrate in-memory testing approach
- Firmware repository (`daqifi-nyquist-firmware`) provides protocol reference

## Questions/Discussion

- Should we support multiple simultaneous devices in a single simulator? (Answer: Phase 6)
- Should we implement serial port simulation? (Answer: Future enhancement)
- How closely should signal generation match real hardware? (Answer: Simplified sine waves sufficient)

---

## Child Issues

The following child issues should be created and linked to this epic:

---

## Issue #1: Phase 1 - Core Infrastructure

### Title
Device Simulator: Core Infrastructure and Virtual Device Abstraction

### Labels
`enhancement`, `testing`, `phase-1`

### Description

## Objective

Establish the foundational architecture for the device simulator, including configuration management, lifecycle orchestration, and virtual device abstraction.

## Tasks

- [ ] Create directory structure: `src/Daqifi.Core.Tests/Simulator/{Protocols,Device,Data,Testing}`
- [ ] Implement `SimulatorConfiguration` record with device type, ports, serial number, etc.
- [ ] Implement abstract `VirtualDevice` base class
- [ ] Implement `Nyquist1Device` concrete class (16 analog channels)
- [ ] Implement `DeviceSimulator` orchestrator with Start/Stop lifecycle
- [ ] Implement `BuildDeviceInfoMessage()` to generate `DaqifiOutMessage`
- [ ] Write unit tests for configuration, lifecycle, and device info generation

## Acceptance Criteria

- [ ] All unit tests pass
- [ ] Can create `DeviceSimulator` with `SimulatorConfiguration`
- [ ] Can start/stop simulator without errors
- [ ] `Nyquist1Device` generates valid device info protobuf message
- [ ] Configuration options are respected (hostname, serial number, firmware version)
- [ ] Proper resource disposal (implements `IDisposable`)
- [ ] No external dependencies

## Files to Create

- `Simulator/SimulatorConfiguration.cs`
- `Simulator/DeviceSimulator.cs`
- `Simulator/Device/VirtualDevice.cs`
- `Simulator/Device/Nyquist1Device.cs`
- `SimulatorLifecycleTests.cs`
- `VirtualDeviceTests.cs`

## Estimated Effort

6 hours

## Dependencies

None

## Related Issues

Part of #[Epic Issue Number]

---

## Issue #2: Phase 2 - UDP Discovery Protocol

### Title
Device Simulator: UDP Discovery Protocol Implementation

### Labels
`enhancement`, `testing`, `phase-2`

### Description

## Objective

Implement UDP discovery server that responds to broadcast queries on port 30303, enabling `WiFiDeviceFinder` to discover the simulated device.

## Tasks

- [ ] Implement `UdpDiscoveryServer` class
- [ ] Listen for UDP broadcasts on configurable port (default 30303)
- [ ] Parse incoming `"DAQiFi?\r\n"` queries
- [ ] Respond with length-delimited `DaqifiOutMessage` protobuf
- [ ] Support port auto-assignment (port 0 = auto-assign)
- [ ] Integrate UDP server with `DeviceSimulator.StartAsync()`
- [ ] Write integration tests with `WiFiDeviceFinder`

## Protocol Details

**Query Format**: ASCII string `"DAQiFi?\r\n"` broadcast to UDP port 30303

**Response Format**: Length-delimited protobuf (`DaqifiOutMessage.WriteDelimitedTo()`)

**Response Contents**:
- `device_port` (TCP port for data connection)
- `host_name`, `device_sn`, `device_fw_rev`, `device_pn`
- `mac_addr`, `ip_addr`
- `pwr_status = 1`

## Acceptance Criteria

- [ ] All integration tests pass
- [ ] `WiFiDeviceFinder.DiscoverAsync()` finds simulated device
- [ ] Discovery response contains correct device metadata
- [ ] Multiple discovery calls return consistent results
- [ ] Port auto-assignment works (port 0)
- [ ] No port conflicts in concurrent tests
- [ ] Response is valid protobuf (parseable by `DaqifiOutMessage.Parser.ParseDelimitedFrom()`)

## Files to Create

- `Simulator/Protocols/UdpDiscoveryServer.cs`
- `UdpDiscoveryTests.cs`

## Files to Modify

- `Simulator/DeviceSimulator.cs` (add UDP server start/stop)

## Estimated Effort

4 hours

## Dependencies

- Issue #1 (Phase 1) must be complete

## Related Issues

Part of #[Epic Issue Number]

---

## Issue #3: Phase 3 - TCP Connection and SCPI Command Processing

### Title
Device Simulator: TCP Data Server and SCPI Command Implementation

### Labels
`enhancement`, `testing`, `phase-3`

### Description

## Objective

Implement TCP server for data connections and SCPI command processing, enabling `DaqifiDevice` to connect and send commands to the simulator.

## Tasks

- [ ] Implement `TcpDataServer` class with TCP listener
- [ ] Accept TCP connections on configurable port (default 9760)
- [ ] Implement line-based SCPI command reading (`\r\n` terminated)
- [ ] Implement `ScpiCommandProcessor` class
- [ ] Support essential SCPI commands:
  - `SYSTem:REboot`
  - `SYSTem:SYSInfoPB?` (returns protobuf)
  - `SYSTem:ECHO <-1|1>`
  - `SYSTem:StartStreamData <freq>`
  - `SYSTem:StopStreamData`
  - `ENAble:VOLTage:DC <binary_string>`
- [ ] Integrate TCP server with `DeviceSimulator.StartAsync()`
- [ ] Write integration tests with `DaqifiDevice` and `TcpStreamTransport`

## SCPI Protocol Details

**Command Format**: ASCII text, `\r\n` line termination
```
<COMMAND> [parameters]\r\n
```

**Response Types**:
- No response for most commands
- Query commands (`?` suffix) return value + `\r\n`
- `SYSInfoPB?` returns length-delimited protobuf

**Example**:
```
Client: "ENAble:VOLTage:DC 0000000011\r\n"
Server: (no response)

Client: "SYSTem:SYSInfoPB?\r\n"
Server: [length-delimited DaqifiOutMessage bytes]
```

## Acceptance Criteria

- [ ] All integration tests pass
- [ ] `DaqifiDevice` can connect to simulator via `TcpStreamTransport`
- [ ] SCPI commands are parsed correctly (case-insensitive)
- [ ] `SYSInfoPB?` returns valid protobuf message
- [ ] Channel configuration commands update virtual device state
- [ ] Multiple sequential commands work correctly
- [ ] Port auto-assignment works
- [ ] Connection lifecycle events fire correctly

## Files to Create

- `Simulator/Protocols/TcpDataServer.cs`
- `Simulator/Protocols/ScpiCommandProcessor.cs`
- `TcpConnectionTests.cs`
- `ScpiCommandTests.cs`

## Files to Modify

- `Simulator/DeviceSimulator.cs` (add TCP server start/stop)
- `Simulator/Device/VirtualDevice.cs` (add command execution methods)

## Estimated Effort

8 hours

## Dependencies

- Issue #1 (Phase 1) must be complete

## Related Issues

Part of #[Epic Issue Number]

---

## Issue #4: Phase 4 - Streaming Data Implementation

### Title
Device Simulator: Streaming Data Generation and Transmission

### Labels
`enhancement`, `testing`, `phase-4`

### Description

## Objective

Implement continuous streaming data generation and transmission, enabling `DaqifiStreamingDevice` to receive simulated analog/digital data at configured frequencies.

## Tasks

- [ ] Implement `SignalGenerator` abstract base class
- [ ] Implement `SineWaveGenerator` for analog data
- [ ] Implement `SquareWaveGenerator` for digital data
- [ ] Update `VirtualDevice.BuildStreamingDataMessage()` to generate data samples
- [ ] Implement streaming loop in `TcpDataServer`
- [ ] Send length-delimited protobuf messages at configured frequency
- [ ] Respect channel enable mask (only send data for enabled channels)
- [ ] Implement accurate timing (±5% of configured frequency)
- [ ] Update timestamp generation (`msg_time_stamp` field)
- [ ] Write streaming integration tests with `DaqifiStreamingDevice`

## Streaming Protocol Details

**Activation**: `SYSTem:StartStreamData <frequency>` (1-1000 Hz)

**Data Format**: Continuous length-delimited `DaqifiOutMessage` with:
- `msg_time_stamp`: Incrementing timestamp (units: 1/timestamp_freq)
- `analog_in_data`: Array of int32 ADC values (enabled channels only)
- `digital_data`: Byte array of digital I/O state
- `analog_in_port_range`: Voltage ranges for channels
- `analog_in_cal_m`, `analog_in_cal_b`: Calibration values

**Signal Generation**:
- Analog: Sine waves with configurable frequency (1 Hz default)
- Digital: Square wave (1 Hz default, 50% duty cycle)
- Phase offset per channel for visualization

**Timing**: Messages sent at configured rate using high-precision timer

## Acceptance Criteria

- [ ] All streaming tests pass
- [ ] Streaming starts when `StartStreamData` command received
- [ ] Streaming stops when `StopStreamData` command received
- [ ] Data is generated at configured frequency (±5% tolerance)
- [ ] Only enabled channels appear in `analog_in_data` array
- [ ] Timestamp increments correctly based on `timestamp_freq`
- [ ] Can stream at 1 Hz, 100 Hz, and 1000 Hz
- [ ] Continuous streaming works for >10 seconds without errors
- [ ] Signal data is deterministic (same config = same data)
- [ ] Protobuf messages are valid and parseable

## Files to Create

- `Simulator/Data/SignalGenerator.cs`
- `Simulator/Data/SineWaveGenerator.cs`
- `Simulator/Data/SquareWaveGenerator.cs`
- `StreamingDataTests.cs`

## Files to Modify

- `Simulator/Protocols/TcpDataServer.cs` (add streaming loop)
- `Simulator/Device/VirtualDevice.cs` (implement `BuildStreamingDataMessage()`)

## Estimated Effort

8 hours

## Dependencies

- Issue #3 (Phase 3) must be complete

## Related Issues

Part of #[Epic Issue Number]

---

## Issue #5: Phase 5 - Testing Infrastructure and Documentation

### Title
Device Simulator: xUnit Fixture and Comprehensive Test Suite

### Labels
`enhancement`, `testing`, `documentation`, `phase-5`

### Description

## Objective

Create xUnit test infrastructure for easy simulator usage in tests, write comprehensive integration tests, and document usage patterns.

## Tasks

- [ ] Implement `DeviceSimulatorFixture` xUnit collection fixture
- [ ] Implement `IAsyncLifetime` for async setup/teardown
- [ ] Create `DeviceSimulatorCollection` attribute
- [ ] Write comprehensive integration tests:
  - Discovery → Connection → Configuration → Streaming full flow
  - Multiple devices concurrently
  - Connection retry scenarios
  - Disconnect/reconnect cycles
  - Edge cases (invalid commands, out-of-range values)
- [ ] Update `README.md` with simulator usage examples
- [ ] Create `TESTING.md` documentation
- [ ] Add XML documentation comments to all public APIs
- [ ] Ensure code coverage >80% on simulator code
- [ ] Add simulator examples to existing test patterns

## Test Coverage Requirements

**Unit Tests**:
- Configuration validation
- Signal generation accuracy
- Command parsing edge cases
- Protobuf message construction

**Integration Tests**:
- End-to-end discovery and connection
- Full streaming lifecycle
- Protocol compliance
- Error handling
- Resource cleanup

## Documentation Requirements

**README.md Updates**:
- Add "Testing with the Simulator" section
- Show basic usage example
- Link to detailed docs

**TESTING.md** (new file):
- Architecture overview
- Usage examples for each phase
- How to write tests with simulator
- Common patterns and recipes
- Troubleshooting

**XML Comments**:
- All public classes
- All public methods and properties
- Include example usage where appropriate

## Acceptance Criteria

- [ ] All tests pass
- [ ] `DeviceSimulatorFixture` works with xUnit collection pattern
- [ ] Can run full integration test suite in <10 seconds
- [ ] Code coverage >80% for simulator code (measured by Coverlet)
- [ ] No flaky tests (run 10 times, all pass)
- [ ] Documentation is clear and complete
- [ ] Examples compile and run correctly
- [ ] All public APIs have XML documentation

## Files to Create

- `Fixtures/DeviceSimulatorFixture.cs`
- `Integration/FullProtocolFlowTests.cs`
- `Integration/SimulatedHardwareTests.cs`
- `TESTING.md`

## Files to Modify

- `README.md` (add simulator section)
- All simulator source files (add XML comments)

## Estimated Effort

6 hours

## Dependencies

- Issue #4 (Phase 4) must be complete

## Related Issues

Part of #[Epic Issue Number]

---

## Issue Metadata

**Create these issues in this order**:
1. Epic issue (references child issues)
2. Phase 1 issue (link to epic)
3. Phase 2 issue (link to epic, dependency on #1)
4. Phase 3 issue (link to epic, dependency on #1)
5. Phase 4 issue (link to epic, dependency on #3)
6. Phase 5 issue (link to epic, dependency on #4)

**Labels to create** (if they don't exist):
- `enhancement`
- `testing`
- `infrastructure`
- `documentation`
- `phase-1`, `phase-2`, `phase-3`, `phase-4`, `phase-5`

**Milestone**: Consider creating "Device Simulator v1.0" milestone
