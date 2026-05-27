# RFC: AI-Native DAQiFi via MCP

**Status:** Draft — open for feedback
**Author:** Tyler Kron (drafted with Claude)
**Date:** 2026-05-26
**Target:** v0.1 launch within ~6 weeks of approval

---

## Summary

Ship a first-class Model Context Protocol (MCP) server for DAQiFi hardware, written in .NET on top of the existing `Daqifi.Core` SDK, and reposition the product around the tagline **"the AI-native DAQ."** Lab researchers, test engineers, and educators can drive Nyquist devices end-to-end through Claude, Cursor, Cline, or any other MCP-aware agent — discovery, channel configuration, streaming, SD logging, alerts — without writing application code.

The MCP is not the product. The product is the workflow it unlocks: *go from idea to running test rig in one conversation.* The MCP is the wedge that makes that workflow real, and that everyone else in test & measurement will spend 12+ months catching up on.

---

## Motivation

### Why now

1. **The T&M industry is sleepy.** LabVIEW dates to 1986. The dominant vendors (NI, MCC/Measurement Computing, Dataq) ship Windows-first GUIs and stack-locked SDKs. Every one of them will bolt an LLM chat panel onto an existing GUI within 12 months. None will rewrite their core control surface to be agent-first, because they cannot — too much legacy, too much certification, too much internal politics.
2. **DAQiFi has the asymmetric advantage.** Two SKUs (Nyquist 1, Nyquist 3), a clean cross-platform .NET SDK, no installed-base baggage, and a small team that can ship in weeks rather than quarters. The window to plant a flag in this position is open *now*, and is unlikely to be open in 12 months.
3. **MCP is becoming the default agent integration surface.** Claude Desktop, Cursor, Cline, Zed, and a growing list of agent frameworks consume MCP servers natively. Anthropic and Microsoft now co-maintain an official .NET MCP SDK, so the "ecosystem is Node-only" objection is no longer load-bearing.

### What lab users actually want to do

The user goal is not "call our API." It is, paraphrased from real conversations:

> "Set up a 4-channel pressure test at 1 kHz, log to the SD card, alert me if any channel exceeds 4 V, and tell me when it's done."

Today that's a half-day of LabVIEW or a few hundred lines of custom C#. With a domain-aware MCP, it's one sentence to an agent that already knows the device.

### Competitive framing

| Failure mode | Vendor | DAQiFi answer |
|---|---|---|
| Heavy, expensive, locked to Windows; AI bolted on the side | LabVIEW / NI | Cross-platform, agent-first, $-per-channel competitive |
| DIY, no calibration, no wireless, no SCPI | Arduino + ADC stacks | Real metrology hardware with a 5-minute agent setup |
| Closed ecosystems, slow updates | MCC, Dataq | Open SDK + MCP + recipes shipped with the product |

The position to own: **real benchtop metrology with the workflow ergonomics of a chat interface.**

---

## Goals and non-goals

### Goals (v0.1)

- Ship a working MCP server (`Daqifi.Mcp`) that exposes discovery, connection, channel configuration, streaming, and SD card operations.
- Make streaming **LLM-safe** by default — agents reason on summaries; raw samples are never dumped into context.
- Ship distribution artifacts that work for the three audiences who matter: .NET devs, agent-tool users (`npx`), and Python/lab users (separate client package).
- Ship 3–5 prebuilt "recipe" prompts that are usable demos and marketable artifacts on day one.
- Establish a safety model that prevents an agent from overdriving a sensor, wiping an SD card, or reflashing firmware without explicit confirmation.

### Non-goals (v0.1)

- A REST API. (MCP is the surface. REST can come later if cloud-deployed agents need it.)
- A standalone GUI client. (Agent UIs are the front end.)
- High-fidelity simulator integration. (Use real hardware for v0.1 development; revisit simulator when the smoke test surface is stable.)
- Cloud-managed device fleet. (Local-first; remote can come in v0.3+.)
- Replacing `Daqifi.Desktop`. (Different product, different audience.)

---

## Proposed solution

### Tech stack decision

**Server: .NET, using the official `ModelContextProtocol` NuGet package.**

The case:

- **The SDK is the asset.** All the hard, hardware-specific code — protobuf wire format, SCPI command generation, UDP discovery, SD card parser, firmware OTA, async message loop, calibration math — already lives in `Daqifi.Core`. Forking that to Node or Python doubles the maintenance burden forever and creates drift risk on every firmware change.
- **First-class .NET MCP support.** `ModelContextProtocol` is co-maintained by Anthropic and Microsoft. It is not a community port. STDIO and HTTP transports are supported; tool/resource/prompt registration uses attributes and reflection.
- **Distribution is solved.** Three artifacts off one codebase:
  1. `dotnet publish -p:PublishAot=true` produces a ~30 MB self-contained native binary per OS. No .NET runtime required.
  2. `dotnet tool install -g Daqifi.Mcp` for the .NET-friendly audience.
  3. `npx @daqifi/mcp` — a tiny Node shim that downloads the right native binary on first run. Most agent UIs launch MCP servers via `npx` or `uvx`, so this is the entry point most users will use. They never know it is .NET underneath.
- **Python audience is served by a separate client package**, not a rewrite. `pip install daqifi` ships a Python client that either (a) speaks MCP out-of-process to the same server, or (b) wraps it via `pythonnet`. Either way, lab/ML users get idiomatic Python without forking the protocol logic.

The wrong move would be to rewrite the wire protocol in Node or Python "to fit the ecosystem." That trades DAQiFi's strongest asset for an ecosystem alignment that no longer matters.

**Long-term flag (out of scope for v0.1):** if a future device family runs an ESP32-class module with enough resources to host an MCP server natively, Rust becomes interesting. Not a today decision; noted for the firmware roadmap.

### Architecture

```
src/
├── Daqifi.Core/                    # existing SDK — no changes
└── Daqifi.Mcp/                     # new project
    ├── Daqifi.Mcp.csproj           # references Daqifi.Core, ModelContextProtocol
    ├── Program.cs                  # CLI entry; parses --mode, --max-sample-rate, etc.
    ├── Server/
    │   ├── McpServerHost.cs        # MCP server setup, transport selection
    │   ├── DeviceRegistry.cs       # tracks connected devices by device_id
    │   └── StreamRegistry.cs       # tracks active streams + ring buffers
    ├── Tools/
    │   ├── DiscoveryTools.cs       # discover_devices, connect_device, ...
    │   ├── DeviceTools.cs          # get_device_info, get_device_status, ...
    │   ├── ChannelTools.cs         # configure_*_channel, set_sample_rate
    │   ├── StreamingTools.cs       # start_streaming, get_stream_summary, ...
    │   ├── SdCardTools.cs          # list_sd_files, download_sd_file, ...
    │   ├── NetworkTools.cs         # configure_wifi, configure_static_ip
    │   ├── FirmwareTools.cs        # check_firmware_update, update_firmware
    │   └── ScpiTools.cs            # send_scpi (gated)
    ├── Resources/
    │   └── DaqifiResources.cs      # daqifi://devices, daqifi://streams/{id}/summary
    ├── Prompts/
    │   └── RecipePrompts.cs        # thermocouple_sweep, soak_test, vibration_fft, ...
    ├── Streaming/
    │   ├── RingBuffer.cs           # bounded per-stream ring buffer
    │   ├── StreamSummarizer.cs     # min/max/mean/rms/std/dom_freq + sparkline
    │   └── WindowReader.cs         # decimated zoom queries
    └── Safety/
        ├── ServerMode.cs           # ReadOnly | Control | Admin
        └── Confirmation.cs         # destructive-action confirmation pattern
```

Key choices:

- **In-process, not IPC.** `Daqifi.Mcp` references `Daqifi.Core` directly. No serialization boundary, no protocol translation, no drift.
- **STDIO transport is default.** Matches how Claude Desktop, Cursor, Cline launch MCP servers. HTTP transport is a future flag (`--transport=http`) for hosted use cases.
- **Per-stream ring buffers live in the server, not in agent context.** Agents request summaries or decimated windows; raw samples never traverse the LLM context window.

### MCP tool spec (v0.1)

Full reference is in the [appendix](#appendix-complete-tool-reference). Summary:

| Category | Tools | v0.1? |
|---|---|---|
| Discovery & connection | `discover_devices`, `connect_device`, `disconnect_device`, `list_connected_devices` | yes |
| Device introspection | `get_device_info`, `get_device_status` | yes |
| Channel configuration | `list_channels`, `configure_analog_channel`, `configure_digital_channel`, `set_sample_rate` | yes |
| Streaming (LLM-safe) | `start_streaming`, `stop_streaming`, `get_stream_summary`, `read_stream_window` | yes |
| Streaming (advanced) | `wait_for_condition` | v0.2 |
| SD card | `list_sd_files`, `download_sd_file`, `start_sd_logging`, `stop_sd_logging` | yes |
| SD card (destructive) | `delete_sd_file`, `format_sd_card` | v0.2 |
| Network | `configure_wifi`, `configure_static_ip` | v0.2 |
| Firmware | `check_firmware_update`, `update_firmware` | v0.2 |
| Escape hatch | `send_scpi` | v0.2 (behind `--allow-raw-scpi`) |

#### The streaming layer is the differentiator

Most product MCPs shipping today are REST endpoints in a trenchcoat. The DAQiFi MCP wins by being domain-aware about *waveform data*, which agents otherwise cannot reason about cheaply.

- **`get_stream_summary`** returns per-channel min/max/mean/RMS/std/dominant_freq plus a 40-character ASCII sparkline. ~200 bytes per channel. Agents call this freely.
- **`read_stream_window`** returns decimated samples for a time slice, hard-capped at `max_points` (default 500). Server decimates; agent gets a useful zoom view.
- **`wait_for_condition`** (v0.2) lets an agent write "watch for over-voltage" loops without polling.

Build this surface and *copy it from yourself* when competitors catch up.

### Resources (read-only, auto-updating)

- `daqifi://devices` — current device list
- `daqifi://devices/{id}/info` — device metadata
- `daqifi://devices/{id}/channels` — channel configuration
- `daqifi://streams/{id}/summary` — live stream summary, ~1 Hz refresh

### Prompts (the "recipes")

These ship with v0.1 and **are the marketing**. Each one becomes a YouTube demo and a README snippet.

- `setup_thermocouple_sweep` — multi-channel temperature, sample rate, SD logging, threshold alerting
- `battery_soak_test` — long-duration logging with hourly stat summaries
- `vibration_capture_fft` — burst capture with frequency-domain analysis
- `multi_channel_pressure_test` — the medical-R&D pattern called out in the README
- `wifi_provision_new_device` — out-of-box onboarding via agent

### Safety model

Three server-launch modes plus a confirmation pattern:

- `--mode=read-only` (default in CI/automated contexts)
  - Allows: `discover_*`, `get_*`, `list_*`, `read_stream_*`
  - Blocks: configuration changes, streaming start, destructive operations
- `--mode=control` (default for interactive use)
  - Adds: channel configuration, streaming, SD download, SD logging start/stop
  - Blocks: firmware updates, WiFi reconfiguration, SD format, SD delete
- `--mode=admin`
  - Allows everything; destructive tools still require `confirmed: true` and return a human-readable preview before acting.

Plus:

- `--max-sample-rate-hz=…` clamps `set_sample_rate` requests
- `--max-voltage-range=…` clamps `configure_analog_channel` requests
- `--allow-raw-scpi` is off by default

Bake this in for v0.1. It becomes a marketing point ("agent-safe by design") and it is much harder to retrofit after the first incident.

### Distribution

Three artifacts, one codebase, one CI job:

1. **AOT binary** — `dotnet publish -p:PublishAot=true` per OS/arch, attached to GitHub releases. ~30 MB, no runtime required.
2. **`dotnet tool`** — `dotnet tool install -g Daqifi.Mcp` for the .NET audience.
3. **`npx` shim** — `npm i -g @daqifi/mcp` publishes a tiny Node wrapper that downloads the right binary on first run. This is what most agent UIs will use to launch the server.

Python client (`pip install daqifi`) is a v0.3 deliverable, not v0.1.

---

## Phasing

### v0.1 — "It works, end to end" (~4–6 weeks)

Goal: a lab user can install the MCP, point Claude Desktop at it, and run a 4-channel sample-rate-1kHz test that logs to SD and gives them a sparkline. Ship as `0.1.0` and make some noise.

Issues are enumerated in [`GITHUB_ISSUES.md`](GITHUB_ISSUES.md). Summary:

1. Project scaffold + CI ([#mcp-1](GITHUB_ISSUES.md#mcp-1))
2. Discovery & connection tools ([#mcp-2](GITHUB_ISSUES.md#mcp-2))
3. Device introspection tools ([#mcp-3](GITHUB_ISSUES.md#mcp-3))
4. Channel configuration tools ([#mcp-4](GITHUB_ISSUES.md#mcp-4))
5. Streaming infrastructure: ring buffer, summarizer, window reader ([#mcp-5](GITHUB_ISSUES.md#mcp-5))
6. Streaming tools: `start_streaming`, `stop_streaming`, `get_stream_summary`, `read_stream_window` ([#mcp-6](GITHUB_ISSUES.md#mcp-6))
7. SD card tools (non-destructive) ([#mcp-7](GITHUB_ISSUES.md#mcp-7))
8. Resources (`daqifi://`) ([#mcp-8](GITHUB_ISSUES.md#mcp-8))
9. Safety model: modes, clamps, confirmation pattern ([#mcp-9](GITHUB_ISSUES.md#mcp-9))
10. Prompts: 5 starter recipes ([#mcp-10](GITHUB_ISSUES.md#mcp-10))
11. Distribution: AOT binary + `dotnet tool` + `npx` shim ([#mcp-11](GITHUB_ISSUES.md#mcp-11))
12. Documentation: README rewrite + agent recipe guide ([#mcp-12](GITHUB_ISSUES.md#mcp-12))

### v0.2 — Power features and gated ops (~3–4 weeks)

- `wait_for_condition` (event-style waiting for agents)
- Destructive SD ops (`delete_sd_file`, `format_sd_card`) behind admin mode
- Network reconfiguration (`configure_wifi`, `configure_static_ip`)
- Firmware update tools (`check_firmware_update`, `update_firmware`)
- `send_scpi` escape hatch behind `--allow-raw-scpi`
- HTTP transport for hosted use cases

### v0.3+ — Ecosystem and platforms

- Python client package (`pip install daqifi`) with both async API and MCP-over-IPC option
- LangChain / LlamaIndex tool wrappers (auto-generated from the MCP surface)
- Cloud deployment story for fleet monitoring
- Agent-managed recipe library (saved configurations, parameterized)

---

## Marketing & launch plan

The MCP is the wedge; the marketing is what turns the wedge into a position.

### Pre-launch (during v0.1 build)

- **Reposition `README.md`.** Hero is an agent demo, not a spec table. Specs go below the fold. ([#mcp-12](GITHUB_ISSUES.md#mcp-12))
- **Record the 5 recipe demos** as the prompts land. Each demo is a ~60-second screen recording: prompt in, working test rig out.
- **Reserve the position.** Land a placeholder page at the marketing site: "AI-native data acquisition. Coming soon." Capture emails.

### Launch week

- **Headline post**: *"I had Claude run a 3-day soak test on a benchtop DAQ from a single sentence."* Posted to Hacker News, r/labrats, r/embedded, r/electronics, Hackaday tip line. Include the prompt, the install command, the data plot, and a link to the recipe.
- **Submit MCP to Anthropic's directory.**
- **Publish LangChain and LlamaIndex tool wrappers** the same week — free distribution into adjacent ecosystems before competitors notice.

### Post-launch (first 90 days)

- **YouTube creator outreach.** Send units + a 10-minute scripted demo brief to 2–3 niche T&M creators. First-call list: Marco Reps, EEVblog, Applied Science, The Signal Path. Pitch: "10-minute test rig with an AI agent."
- **Educator angle.** Reach out to 2–3 EE/ME department chairs with the LabVIEW-compatibility + SCPI + agent story. "Students can drive the lab bench from Claude." Quotable.
- **Recipe library.** Publish 1 new recipe per week for the first 8 weeks. Each is a blog post, a YouTube short, and a saved prompt the MCP exposes.

### What we do *not* do

- We do not pre-announce. Ship first, talk second.
- We do not chase enterprise. The wedge is greenfield labs, hobbyist-adjacent pros, and educators. Enterprise can come for v1.0.
- We do not market on the SDK. The SDK is plumbing. The story is the workflow.

---

## Open questions

These need a decision before or during v0.1. Each is a candidate for a thread on the RFC PR.

1. **Buyer segmentation.** AI-native marketing pulls greenfield buyers. Migrators (existing LabVIEW shops) won't switch on the AI story alone. Do we accept that, or invest in migration tooling?
2. **Pricing strategy.** Does the AI story support a higher ASP, a SaaS tier (managed recipes, hosted dashboard), or neither? This needs to be decided *before* the launch copy is written.
3. **Agent action boundaries.** Is the default "control" mode the right safety posture, or do we ship "read-only" as default and require an opt-in flag for control? The conservative choice protects us from launch-week horror stories.
4. **Telemetry.** Do we want anonymous usage telemetry from the MCP server to learn what agents actually call? If yes, opt-in only, and stated clearly in the README.
5. **MCP stability contract.** What is our promise to users about tool name/schema stability? Suggest: tool *names* are stable from v0.1; tool *parameters* may add optional fields without notice; breaking changes bump major version.
6. **LabVIEW interop.** Is there a story where the MCP can drive a LabVIEW VI as a tool call? Probably v0.3+, but flagging now because it could be a marketing weapon against NI.
7. **Hosted recipe library vs. in-repo.** Recipes as MCP prompts ship in the binary. Do we also stand up a hosted directory where users contribute and rate recipes? Network effect potential, but support burden.
8. **DAQiFi Desktop alignment.** Does Desktop adopt the MCP internally as its control layer, or stay separate? Adopting it would force a clean API and double as dogfooding, but adds scope.

---

## Appendix: complete tool reference

### Discovery & connection

| Tool | Inputs | Returns | Notes |
|---|---|---|---|
| `discover_devices` | `timeout_ms?: int=2000`, `transports?: ["wifi","serial"]` | `[{device_id, name, transport, address, serial_number, part_number, firmware_version}]` | Wraps `WiFiDeviceFinder` / `SerialDeviceFinder`. |
| `connect_device` | `device_id: string` | `{device_id, status, capabilities}` | Wraps `ConnectFromDeviceInfoAsync()`. |
| `disconnect_device` | `device_id: string` | `{ok: bool}` | |
| `list_connected_devices` | — | `[{device_id, name, state, streaming: bool}]` | Cheap; agents call often. |

### Device introspection

| Tool | Inputs | Returns |
|---|---|---|
| `get_device_info` | `device_id` | `{part_number, firmware_version, channel_counts: {analog, digital}, resolution_bits, voltage_range, capabilities[]}` |
| `get_device_status` | `device_id` | `{connection_state, streaming, sample_rate_hz, sd_logging, sd_free_bytes, channels_enabled[]}` |

### Channel configuration

| Tool | Inputs | Returns | Notes |
|---|---|---|---|
| `list_channels` | `device_id`, `kind?: "analog"\|"digital"` | `[{index, kind, label, enabled, range, direction}]` | |
| `configure_analog_channel` | `device_id`, `channel: int`, `enabled: bool`, `range?: float`, `label?: string` | `{channel, applied}` | Wraps `IAnalogChannel`. |
| `configure_digital_channel` | `device_id`, `channel: int`, `direction: "input"\|"output"`, `label?` | `{channel, applied}` | |
| `set_sample_rate` | `device_id`, `rate_hz: int` | `{rate_hz, applied}` | Clamped to `--max-sample-rate-hz`. |

### Streaming

| Tool | Inputs | Returns | Notes |
|---|---|---|---|
| `start_streaming` | `device_id`, `duration_s?: int`, `ring_buffer_s?: int=60` | `{stream_id, started_at}` | Server owns ring buffer. |
| `stop_streaming` | `stream_id` | `{stream_id, total_samples, duration_s}` | |
| `get_stream_summary` | `stream_id`, `channels?: int[]`, `window_s?: float=5` | `{per_channel: [{channel, n, min, max, mean, rms, std, dominant_freq_hz, sparkline}]}` | The headline tool. |
| `read_stream_window` | `stream_id`, `channels: int[]`, `start_s`, `end_s`, `max_points?: int=500` | `{channel, t[], v[], decimation_factor}` | Hard-capped; decimates server-side. |
| `wait_for_condition` | `stream_id`, `channel`, `predicate: ">4.5"\|"<0"\|"abs>2"\|"rising_edge"`, `timeout_s` | `{matched: bool, t, v}` | **v0.2** |

### SD card operations

| Tool | Inputs | Returns | Destructive | v0.1? |
|---|---|---|---|---|
| `list_sd_files` | `device_id` | `[{name, size_bytes, modified}]` | no | yes |
| `download_sd_file` | `device_id`, `remote_name`, `local_path?` | `{local_path, size_bytes, channels, duration_s, summary}` | no | yes |
| `start_sd_logging` | `device_id`, `filename?` | `{filename}` | no | yes |
| `stop_sd_logging` | `device_id` | `{filename, size_bytes}` | no | yes |
| `delete_sd_file` | `device_id`, `remote_name`, `confirmed: bool` | `{ok}` | **yes** | v0.2 |
| `format_sd_card` | `device_id`, `confirmed: bool` | `{ok}` | **yes** | v0.2 |

### Network & firmware (gated, v0.2)

| Tool | Inputs | Destructive |
|---|---|---|
| `configure_wifi` | `device_id`, `ssid`, `password`, `confirmed` | **yes** |
| `configure_static_ip` | `device_id`, `ip`, `mask`, `gateway`, `confirmed` | **yes** |
| `check_firmware_update` | `device_id` | no |
| `update_firmware` | `device_id`, `target: "pic32"\|"wifi"`, `confirmed` | **yes** |

### Escape hatch (v0.2, off by default)

| Tool | Inputs | Notes |
|---|---|---|
| `send_scpi` | `device_id`, `command`, `expect_response?: bool` | Requires `--allow-raw-scpi`. |
