# GitHub Issues — DAQiFi MCP v0.1

Ready-to-paste issue drafts for the v0.1 launch of the AI-native DAQiFi MCP. See [`RFC.md`](RFC.md) for the full proposal.

**Suggested milestone:** `DAQiFi MCP v0.1`
**Suggested labels:** `enhancement`, `mcp`, `ai-integration` (plus per-issue labels noted below)

Create issues in the order listed. Dependencies are noted on each issue.

---

## Epic: DAQiFi MCP v0.1 — AI-Native Control Surface

### Title
Epic: Ship DAQiFi MCP v0.1 — agent-driven control surface for Nyquist devices

### Labels
`epic`, `enhancement`, `mcp`, `ai-integration`

### Description

#### Problem statement

Today, every workflow involving a DAQiFi Nyquist device requires writing C# code against `Daqifi.Core` or driving the Desktop application by hand. There is no way for an AI agent (Claude, Cursor, Cline, etc.) to control a device end-to-end, and no MCP server exists for the test & measurement category at all. The window to plant a flag in this position is open *right now*; every major T&M vendor will bolt LLM chat onto an existing GUI within 12 months.

#### Proposed solution

Ship a first-class Model Context Protocol (MCP) server (`Daqifi.Mcp`) in this repository, built on top of `Daqifi.Core`, exposing discovery, channel configuration, streaming (with LLM-safe summaries), and SD card operations. Reposition the product around "AI-native data acquisition."

See [`docs/planning/mcp/RFC.md`](docs/planning/mcp/RFC.md) for the full design rationale, stack decision, tool specification, safety model, and launch plan.

#### Architecture overview

```
src/
├── Daqifi.Core/         # existing SDK — referenced, not modified
└── Daqifi.Mcp/          # new project
    ├── Tools/           # MCP tool implementations
    ├── Resources/       # MCP resources (daqifi://...)
    ├── Prompts/         # MCP prompts ("recipes")
    ├── Streaming/       # ring buffer, summarizer, window reader
    ├── Safety/          # mode gating, clamps, confirmation
    └── Server/          # MCP host wiring
```

#### Stack decision

.NET, using the official `ModelContextProtocol` NuGet package. In-process reference to `Daqifi.Core` — no IPC, no protocol translation. Distribution via AOT binary, `dotnet tool`, and an `npx` shim. Python audience served by a separate v0.3 client package.

#### Implementation phases

This epic is broken down into 12 implementation issues, designed to be worked roughly sequentially with some parallelism possible (see dependency graph at the bottom of this doc).

| Issue | Title | Effort | Depends on |
|---|---|---|---|
| #mcp-1 | Project scaffold + CI | 4h | — |
| #mcp-2 | Discovery & connection tools | 6h | #mcp-1 |
| #mcp-3 | Device introspection tools | 3h | #mcp-2 |
| #mcp-4 | Channel configuration tools | 5h | #mcp-2 |
| #mcp-5 | Streaming infrastructure (ring buffer, summarizer) | 8h | #mcp-1 |
| #mcp-6 | Streaming tools | 6h | #mcp-4, #mcp-5 |
| #mcp-7 | SD card tools (non-destructive) | 5h | #mcp-2 |
| #mcp-8 | Resources (daqifi://) | 4h | #mcp-3, #mcp-6 |
| #mcp-9 | Safety model | 5h | #mcp-1 |
| #mcp-10 | Starter recipe prompts | 6h | #mcp-6, #mcp-7 |
| #mcp-11 | Distribution (AOT, dotnet tool, npx) | 8h | all tool issues |
| #mcp-12 | Documentation rewrite | 6h | #mcp-10, #mcp-11 |

**Total estimated effort:** ~66 hours (~4–6 calendar weeks part-time).

#### Success criteria

- [ ] `Daqifi.Mcp` project builds and produces an AOT binary per supported OS
- [ ] Claude Desktop (or any STDIO-MCP client) can launch the server, discover a Nyquist device, configure 4 analog channels, start streaming, and retrieve a stream summary
- [ ] All v0.1 tools have integration tests covering happy-path behavior
- [ ] Safety modes (`read-only`, `control`, `admin`) gate destructive operations correctly
- [ ] At least 5 recipe prompts ship and are demonstrable on real hardware
- [ ] `npx @daqifi/mcp` works on macOS, Linux, and Windows
- [ ] README is rewritten to lead with the agent workflow
- [ ] Launch post drafted and ready for v0.1.0 tag

#### Non-goals (v0.1)

- Destructive SD ops (`delete_sd_file`, `format_sd_card`) — v0.2
- Network reconfiguration, firmware updates — v0.2
- `send_scpi` escape hatch — v0.2
- `wait_for_condition` event waiting — v0.2
- Python client package — v0.3
- REST/HTTP transport — v0.2
- Cloud-managed device fleet — v0.3+

#### Open questions

See [RFC §Open questions](RFC.md#open-questions). Most pressing decisions for the epic:

- Default safety mode for the `npx` distribution: `read-only` or `control`?
- Telemetry opt-in: yes or no?
- MCP stability contract: what do we promise users?

---

## #mcp-1: Project scaffold and CI

### Title
MCP: Scaffold `Daqifi.Mcp` project and wire CI

### Labels
`mcp`, `infrastructure`, `phase-1`

### Objective

Create the `Daqifi.Mcp` .NET project, wire it up to `Daqifi.Core` and the `ModelContextProtocol` SDK, and add CI coverage so subsequent issues can land green.

### Tasks

- [ ] Create `src/Daqifi.Mcp/Daqifi.Mcp.csproj` (.NET 10, executable)
- [ ] Add project reference to `Daqifi.Core`
- [ ] Add NuGet reference to `ModelContextProtocol` (latest)
- [ ] Implement `Program.cs` with minimal CLI: `--mode`, `--max-sample-rate-hz`, `--max-voltage-range`, `--allow-raw-scpi`, `--transport=stdio|http`, `--port`
- [ ] Implement `Server/McpServerHost.cs` — wires up STDIO transport, registers (empty) tool/resource/prompt collections
- [ ] Implement `Server/DeviceRegistry.cs` — in-memory `device_id` → `IDaqifiDevice` map with thread-safe add/remove/get
- [ ] Add `src/Daqifi.Mcp.Tests/Daqifi.Mcp.Tests.csproj` (xUnit)
- [ ] Add `MinimalStartupTest`: server starts, accepts an MCP `initialize`, returns capabilities, shuts down cleanly
- [ ] Update `.github/workflows/*.yml` to build and test the new project
- [ ] Update solution file to include both new projects

### Acceptance criteria

- [ ] `dotnet build` succeeds across all target frameworks
- [ ] `dotnet test src/Daqifi.Mcp.Tests` passes
- [ ] `dotnet run --project src/Daqifi.Mcp -- --help` prints the CLI flags
- [ ] CI passes on the PR

### Estimated effort

4 hours

### Dependencies

None

### Related

Part of the [DAQiFi MCP v0.1 epic](#epic-daqifi-mcp-v01--ai-native-control-surface)

---

## #mcp-2: Discovery and connection tools

### Title
MCP: Implement discovery and connection tools

### Labels
`mcp`, `tools`, `phase-2`

### Objective

Implement the four foundational tools that let an agent find devices and open connections.

### Tools

- `discover_devices(timeout_ms?, transports?)`
- `connect_device(device_id)`
- `disconnect_device(device_id)`
- `list_connected_devices()`

### Tasks

- [ ] Implement `Tools/DiscoveryTools.cs` with the four tools above
- [ ] `discover_devices` wraps `WiFiDeviceFinder` + `SerialDeviceFinder`, merges results, mints stable `device_id` (suggest `{transport}:{serial_or_address}`)
- [ ] `connect_device` calls `ConnectFromDeviceInfoAsync()`, registers in `DeviceRegistry`
- [ ] `disconnect_device` removes from registry and disposes the device
- [ ] `list_connected_devices` enumerates `DeviceRegistry`
- [ ] All inputs validated; errors return MCP error responses with actionable messages
- [ ] Integration tests using a real device (or simulator if available; otherwise skip-on-no-hardware pattern)

### Acceptance criteria

- [ ] Agent can call `discover_devices` and receive a list of devices
- [ ] Agent can call `connect_device` with a `device_id` from discovery and the device becomes connected
- [ ] `list_connected_devices` reflects the connected state
- [ ] `disconnect_device` cleans up cleanly; subsequent `list_connected_devices` reflects disconnection
- [ ] Tool descriptions are written for agent ergonomics (clear, one-sentence purpose; parameter docs include units and examples)
- [ ] Integration test passes against real hardware

### Files to create

- `src/Daqifi.Mcp/Tools/DiscoveryTools.cs`
- `src/Daqifi.Mcp.Tests/Tools/DiscoveryToolsTests.cs`

### Estimated effort

6 hours

### Dependencies

- #mcp-1 (Project scaffold)

---

## #mcp-3: Device introspection tools

### Title
MCP: Implement device introspection tools

### Labels
`mcp`, `tools`, `phase-2`

### Objective

Implement read-only tools that let an agent learn about a device's identity, capabilities, and current state.

### Tools

- `get_device_info(device_id)`
- `get_device_status(device_id)`

### Tasks

- [ ] Implement `Tools/DeviceTools.cs`
- [ ] `get_device_info` returns `{part_number, firmware_version, channel_counts: {analog, digital}, resolution_bits, voltage_range, capabilities[]}`
- [ ] `get_device_status` returns `{connection_state, streaming, sample_rate_hz, sd_logging, sd_free_bytes, channels_enabled[]}`
- [ ] Both tools surface meaningful errors when device is not connected
- [ ] Unit tests with a fake `IDaqifiDevice`

### Acceptance criteria

- [ ] Both tools return the documented shapes
- [ ] Calling either on a disconnected device returns a clean error
- [ ] Tool descriptions tell the agent when to prefer one over the other

### Files to create

- `src/Daqifi.Mcp/Tools/DeviceTools.cs`
- `src/Daqifi.Mcp.Tests/Tools/DeviceToolsTests.cs`

### Estimated effort

3 hours

### Dependencies

- #mcp-2 (Discovery & connection)

---

## #mcp-4: Channel configuration tools

### Title
MCP: Implement channel configuration tools

### Labels
`mcp`, `tools`, `phase-2`

### Objective

Implement tools that let an agent enumerate and configure analog/digital channels and set the sample rate.

### Tools

- `list_channels(device_id, kind?)`
- `configure_analog_channel(device_id, channel, enabled, range?, label?)`
- `configure_digital_channel(device_id, channel, direction, label?)`
- `set_sample_rate(device_id, rate_hz)`

### Tasks

- [ ] Implement `Tools/ChannelTools.cs`
- [ ] `list_channels` returns the full channel inventory with current state
- [ ] `configure_analog_channel` wraps `IAnalogChannel` configuration
- [ ] `configure_digital_channel` wraps `IDigitalChannel` configuration
- [ ] `set_sample_rate` respects `--max-sample-rate-hz` clamp (set in #mcp-9; for now, parameter is wired but enforcement lands with safety issue)
- [ ] Validate channel indices against device capabilities; clear error for out-of-range
- [ ] Unit tests + an integration test that configures 4 channels and verifies via `get_device_status`

### Acceptance criteria

- [ ] Agent can list channels, configure several, and see the changes via `get_device_status`
- [ ] Out-of-range channel index returns clean error
- [ ] Out-of-range sample rate returns clean error (after #mcp-9)
- [ ] Configuration persists across `list_channels` calls within a session

### Files to create

- `src/Daqifi.Mcp/Tools/ChannelTools.cs`
- `src/Daqifi.Mcp.Tests/Tools/ChannelToolsTests.cs`

### Estimated effort

5 hours

### Dependencies

- #mcp-2 (Discovery & connection)

---

## #mcp-5: Streaming infrastructure (ring buffer + summarizer)

### Title
MCP: Streaming infrastructure — ring buffer, summarizer, window reader

### Labels
`mcp`, `infrastructure`, `streaming`, `phase-3`

### Objective

Build the server-side infrastructure that lets agents reason about live waveform data without pulling raw samples into context. **This is the differentiating technical capability** of the v0.1 release.

### Components

- `Streaming/RingBuffer.cs` — bounded per-stream, per-channel ring buffer. Configurable size in seconds. Thread-safe single-producer multi-consumer.
- `Streaming/StreamSummarizer.cs` — computes per-channel `{n, min, max, mean, rms, std, dominant_freq_hz, sparkline}` over a configurable window. Sparkline is a 40-char Unicode block-character string.
- `Streaming/WindowReader.cs` — given `(start_s, end_s, max_points)`, returns decimated samples (decimation factor chosen to stay under `max_points`).
- `Server/StreamRegistry.cs` — tracks active streams by `stream_id`.

### Tasks

- [ ] Implement `RingBuffer<T>` with bounded capacity, overwrite-oldest semantics, timestamped samples
- [ ] Implement `StreamSummarizer` with min/max/mean/rms/std (single-pass Welford) and a simple Goertzel-based dominant frequency estimate
- [ ] Implement sparkline rendering using `▁▂▃▄▅▆▇█`
- [ ] Implement `WindowReader` with server-side decimation (averaging buckets)
- [ ] Implement `StreamRegistry` with stream lifecycle (start, stop, dispose)
- [ ] Wire streaming subscription to `IDaqifiDevice` message events
- [ ] Unit tests for ring buffer correctness, summarizer math (against a known sine wave), and window decimation
- [ ] Benchmark: 1000 Hz × 16 channels × 60 s ring buffer must stay under 50 MB RAM

### Acceptance criteria

- [ ] Ring buffer correctly retains the last N seconds of samples per channel
- [ ] Summarizer returns mathematically correct stats for a known input (sine wave: mean ≈ 0, rms ≈ amplitude/√2, dominant_freq matches input)
- [ ] Window reader respects `max_points` cap and returns decimated data when input exceeds cap
- [ ] All unit tests pass
- [ ] Benchmark target met

### Files to create

- `src/Daqifi.Mcp/Streaming/RingBuffer.cs`
- `src/Daqifi.Mcp/Streaming/StreamSummarizer.cs`
- `src/Daqifi.Mcp/Streaming/WindowReader.cs`
- `src/Daqifi.Mcp/Server/StreamRegistry.cs`
- `src/Daqifi.Mcp.Tests/Streaming/*Tests.cs`

### Estimated effort

8 hours

### Dependencies

- #mcp-1 (Project scaffold)

---

## #mcp-6: Streaming tools

### Title
MCP: Implement streaming tools (start, stop, summary, window)

### Labels
`mcp`, `tools`, `streaming`, `phase-3`

### Objective

Expose the streaming infrastructure built in #mcp-5 as MCP tools.

### Tools

- `start_streaming(device_id, duration_s?, ring_buffer_s?)`
- `stop_streaming(stream_id)`
- `get_stream_summary(stream_id, channels?, window_s?)`
- `read_stream_window(stream_id, channels, start_s, end_s, max_points?)`

### Tasks

- [ ] Implement `Tools/StreamingTools.cs`
- [ ] `start_streaming` creates a `StreamRegistry` entry, subscribes to device data events, returns `stream_id`
- [ ] `stop_streaming` tears down the subscription, returns total samples + duration
- [ ] `get_stream_summary` delegates to `StreamSummarizer`
- [ ] `read_stream_window` delegates to `WindowReader`
- [ ] Auto-stop streams when their `duration_s` elapses
- [ ] Auto-cleanup streams when their device disconnects
- [ ] Integration test: start streaming for 5s on a real device (or simulated waveform if no hardware), call `get_stream_summary` mid-stream, verify stats look reasonable

### Acceptance criteria

- [ ] Agent can start a 10-second stream, sleep, call `get_stream_summary`, and get a non-empty result
- [ ] `read_stream_window` returns decimated samples for a specified time range
- [ ] Disconnecting a device cleans up its active streams
- [ ] Tool descriptions explicitly tell the agent: "prefer `get_stream_summary` over `read_stream_window` for reasoning about waveforms; only use `read_stream_window` for zoom-in"

### Files to create

- `src/Daqifi.Mcp/Tools/StreamingTools.cs`
- `src/Daqifi.Mcp.Tests/Tools/StreamingToolsTests.cs`

### Estimated effort

6 hours

### Dependencies

- #mcp-4 (Channel configuration)
- #mcp-5 (Streaming infrastructure)

---

## #mcp-7: SD card tools (non-destructive)

### Title
MCP: Implement non-destructive SD card tools

### Labels
`mcp`, `tools`, `sd-card`, `phase-3`

### Objective

Expose SD card listing, downloading, and logging start/stop operations as MCP tools. Destructive operations (delete, format) are deferred to v0.2.

### Tools

- `list_sd_files(device_id)`
- `download_sd_file(device_id, remote_name, local_path?)`
- `start_sd_logging(device_id, filename?)`
- `stop_sd_logging(device_id)`

### Tasks

- [ ] Implement `Tools/SdCardTools.cs`
- [ ] Wrap `ISdCardOperations` (already in `Daqifi.Core`)
- [ ] `download_sd_file` defaults `local_path` to a sensible temp directory; returns post-download summary (channels, duration, basic stats)
- [ ] `start_sd_logging` auto-generates a filename if not provided (`yyyy-MM-dd-HHmmss.csv`)
- [ ] All operations respect connection state; clean errors when not connected or SD card not present
- [ ] Integration test against a real device with an SD card

### Acceptance criteria

- [ ] Agent can list, download, and start/stop logging
- [ ] Downloads include a useful post-download summary (so the agent has something to talk about without re-reading the file)
- [ ] Errors for "SD card not present" / "not connected" are clear

### Files to create

- `src/Daqifi.Mcp/Tools/SdCardTools.cs`
- `src/Daqifi.Mcp.Tests/Tools/SdCardToolsTests.cs`

### Estimated effort

5 hours

### Dependencies

- #mcp-2 (Discovery & connection)

---

## #mcp-8: MCP resources (daqifi://)

### Title
MCP: Implement read-only resources (`daqifi://...`)

### Labels
`mcp`, `resources`, `phase-3`

### Objective

Expose live device state as MCP resources, which agents can subscribe to without explicit tool calls.

### Resources

- `daqifi://devices`
- `daqifi://devices/{id}/info`
- `daqifi://devices/{id}/channels`
- `daqifi://streams/{id}/summary` (auto-refresh ~1 Hz)

### Tasks

- [ ] Implement `Resources/DaqifiResources.cs`
- [ ] Wire resource list to `DeviceRegistry` and `StreamRegistry`
- [ ] Implement resource change notifications for `daqifi://streams/{id}/summary` (~1 Hz throttled)
- [ ] Resource URIs are stable and documented in README

### Acceptance criteria

- [ ] Listing resources via MCP returns the documented URIs
- [ ] Reading `daqifi://devices` returns the same data as `list_connected_devices()`
- [ ] Reading `daqifi://streams/{id}/summary` returns a stream summary
- [ ] Stream summary resource emits change notifications during active streaming

### Files to create

- `src/Daqifi.Mcp/Resources/DaqifiResources.cs`
- `src/Daqifi.Mcp.Tests/Resources/DaqifiResourcesTests.cs`

### Estimated effort

4 hours

### Dependencies

- #mcp-3 (Device introspection)
- #mcp-6 (Streaming tools)

---

## #mcp-9: Safety model

### Title
MCP: Implement safety model (modes, clamps, confirmation)

### Labels
`mcp`, `safety`, `phase-3`

### Objective

Implement the three server-launch modes (`read-only`, `control`, `admin`) and the parameter clamps that prevent an agent from over-driving hardware. Also lay groundwork for the confirmation pattern used by destructive tools (full destructive tools land in v0.2).

### Tasks

- [ ] Implement `Safety/ServerMode.cs` enum and CLI parsing in `Program.cs`
- [ ] Implement tool-level gating: each tool declares the minimum mode it requires; the MCP server filters the published tool list accordingly
- [ ] Wire `--max-sample-rate-hz` clamp into `set_sample_rate`
- [ ] Wire `--max-voltage-range` clamp into `configure_analog_channel`
- [ ] Implement `Safety/Confirmation.cs` — pattern for tools that take `confirmed: bool` and return a preview if `confirmed == false` (used by v0.2 destructive tools, but defined here)
- [ ] Default mode for the `npx @daqifi/mcp` distribution: **resolve via [open question #3 on the RFC](RFC.md#open-questions) before merging**
- [ ] Document modes prominently in the README
- [ ] Tests covering: mode filtering hides correct tools; clamps enforce limits; confirmation preview shape

### Acceptance criteria

- [ ] Launching with `--mode=read-only` exposes only read-only tools
- [ ] `set_sample_rate` above `--max-sample-rate-hz` returns a clean error
- [ ] `configure_analog_channel` above `--max-voltage-range` returns a clean error
- [ ] Documentation includes a clear table of which tools are available in which mode

### Files to create

- `src/Daqifi.Mcp/Safety/ServerMode.cs`
- `src/Daqifi.Mcp/Safety/Confirmation.cs`
- `src/Daqifi.Mcp.Tests/Safety/*Tests.cs`

### Estimated effort

5 hours

### Dependencies

- #mcp-1 (Project scaffold)

---

## #mcp-10: Starter recipe prompts

### Title
MCP: Implement 5 starter recipe prompts

### Labels
`mcp`, `prompts`, `marketing`, `phase-4`

### Objective

Ship 5 MCP prompts that double as in-product onboarding and marketing artifacts. Each prompt is parameterized and runnable end-to-end against a real Nyquist device.

### Prompts

- `setup_thermocouple_sweep(channels, sample_rate_hz, threshold_c, log_to_sd?)`
- `battery_soak_test(channels, duration_h, summary_interval_min, log_to_sd?)`
- `vibration_capture_fft(channel, duration_s, sample_rate_hz)`
- `multi_channel_pressure_test(channels, sample_rate_hz, threshold_v, log_to_sd?)`
- `wifi_provision_new_device(ssid, password)`

### Tasks

- [ ] Implement `Prompts/RecipePrompts.cs`
- [ ] Each prompt returns a structured message to the agent describing the goal, the suggested tool sequence, and any safety considerations
- [ ] Document each recipe in the README with a screenshot and a 60-second demo video link (videos can land in #mcp-12)
- [ ] Each prompt has at least one integration test that runs it end-to-end against a connected device (or a deterministic mock if hardware unavailable in CI)
- [ ] Coordinate with #mcp-12 author for the README rewrite

### Acceptance criteria

- [ ] All 5 prompts are discoverable via the MCP `list_prompts` call
- [ ] Each prompt can be parameterized and invoked from Claude Desktop end-to-end
- [ ] Each prompt produces a useful working test rig in under 60 seconds of agent activity (excluding the actual test duration)

### Files to create

- `src/Daqifi.Mcp/Prompts/RecipePrompts.cs`
- `src/Daqifi.Mcp.Tests/Prompts/RecipePromptsTests.cs`

### Estimated effort

6 hours

### Dependencies

- #mcp-6 (Streaming tools)
- #mcp-7 (SD card tools)

---

## #mcp-11: Distribution (AOT, dotnet tool, npx shim)

### Title
MCP: Distribution — AOT binary, `dotnet tool`, and `npx` shim

### Labels
`mcp`, `release`, `infrastructure`, `phase-4`

### Objective

Ship three distribution artifacts off the single `Daqifi.Mcp` codebase so users on every platform can install with one command.

### Artifacts

- AOT-published native binaries for `osx-arm64`, `osx-x64`, `linux-x64`, `linux-arm64`, `win-x64`
- `dotnet tool` package on NuGet (`Daqifi.Mcp`)
- npm package `@daqifi/mcp` — a tiny Node shim that downloads the right native binary on first run

### Tasks

- [ ] Configure AOT publish in `Daqifi.Mcp.csproj`
- [ ] GitHub Actions workflow that on tag (`v*.*.*`) publishes binaries to the release as platform-specific zips
- [ ] Configure `dotnet tool` packaging (PackAsTool, ToolCommandName, etc.)
- [ ] CI publishes the .NET tool to NuGet on tagged releases
- [ ] Build the `@daqifi/mcp` npm package in a sibling repo or under `packages/npm/`:
  - `bin/daqifi-mcp` Node script
  - On first run, downloads the matching native binary from GitHub releases into `~/.daqifi-mcp/<version>/<arch>/`
  - Verifies SHA-256
  - Subsequent runs exec the cached binary
- [ ] CI publishes the npm package on tagged releases
- [ ] Document install: `npx @daqifi/mcp` should "just work" in Claude Desktop config
- [ ] Smoke-test the install on macOS, Linux, Windows

### Acceptance criteria

- [ ] On a fresh machine, `npx @daqifi/mcp --help` works without any other install
- [ ] On a fresh machine, `dotnet tool install -g Daqifi.Mcp && daqifi-mcp --help` works
- [ ] Claude Desktop config snippet `{"command": "npx", "args": ["@daqifi/mcp"]}` launches the server cleanly
- [ ] Released binaries are checksummed and the npm shim verifies before exec

### Files to create

- `.github/workflows/release.yml`
- `packages/npm/` (or sibling repo) — Node shim
- Release notes template

### Estimated effort

8 hours

### Dependencies

- All tool issues (#mcp-2 through #mcp-10) — needs a working server to package

---

## #mcp-12: Documentation rewrite

### Title
MCP: Rewrite README and ship agent recipe guide

### Labels
`mcp`, `documentation`, `marketing`, `phase-4`

### Objective

Reposition the repository's README around the agent workflow, and ship a dedicated agent recipe guide that doubles as launch-week content.

### Tasks

- [ ] Rewrite `README.md`:
  - Hero is "AI-native data acquisition for the test bench"
  - Show a 5-line Claude Desktop config snippet up top
  - Embed a 60-second demo GIF (recorded against real hardware) showing one recipe end-to-end
  - Specs move below the fold
  - Old README content (API examples, etc.) moves to `docs/SDK_USAGE.md`
- [ ] Write `docs/AGENT_RECIPES.md`:
  - One section per recipe prompt
  - Each section includes the prompt, an example user instruction, a screenshot of the agent run, and the tool sequence the agent typically uses
- [ ] Update `docs/DEVICE_INTERFACES.md` to link out to the MCP tool reference
- [ ] Draft the Hacker News / launch post in `docs/planning/mcp/LAUNCH_POST.md` (not published; just drafted for review)
- [ ] Update repo description and topics on GitHub

### Acceptance criteria

- [ ] Someone landing on the repo README within 30 seconds understands: (a) this is a DAQ device, (b) you control it with an AI agent, (c) here's how to install
- [ ] All 5 recipes have working documented examples
- [ ] Old SDK-first usage content is preserved (under `docs/SDK_USAGE.md`), not lost
- [ ] Launch post draft exists and is ready for marketing review

### Files to create / modify

- `README.md` (rewrite)
- `docs/AGENT_RECIPES.md` (new)
- `docs/SDK_USAGE.md` (extracted from old README)
- `docs/planning/mcp/LAUNCH_POST.md` (draft)

### Estimated effort

6 hours

### Dependencies

- #mcp-10 (Recipe prompts — need them implemented to document them)
- #mcp-11 (Distribution — need install commands that work)

---

## Dependency graph

```
                           #mcp-1 (scaffold)
                          /        |        \
                #mcp-2 (discovery) #mcp-5 (stream infra)  #mcp-9 (safety)
                  /    |    \              \
        #mcp-3   #mcp-4  #mcp-7             \
        (info) (channels) (SD)               \
                 \              \             \
                  +--- #mcp-6 (streaming tools)
                              \
                               +--- #mcp-8 (resources)
                              \
                               +--- #mcp-10 (prompts)
                                            \
                                             #mcp-11 (distribution)
                                                       \
                                                        #mcp-12 (docs)
```

Issues #mcp-1, #mcp-5, and #mcp-9 can start in parallel.
Issues #mcp-2/3/4/7 can be parallelized after #mcp-1.
Issues #mcp-6/8/10 unblock once their dependencies land.
Issues #mcp-11/12 finalize the release.

---

## Labels to create (if missing)

- `mcp`
- `ai-integration`
- `epic`
- `tools`
- `streaming`
- `sd-card`
- `safety`
- `prompts`
- `release`
- `phase-1`, `phase-2`, `phase-3`, `phase-4`

## Milestone

`DAQiFi MCP v0.1`
