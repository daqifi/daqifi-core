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
| Escape hatch | `send_scpi` | v0.2 (design specced in [§Safety: send_scpi v0.2 design](#safety-send_scpi-v02-design)) |

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

**What a "recipe" is, mechanically:** an MCP server exposes three surfaces — tools (callable functions), resources (read-only URIs), and **prompts** (parameterized templates the server tells the client about). The MCP-aware client (Claude Desktop, Cursor, Cline) surfaces those prompts as a slash-command menu / suggestion list / parameter form. When the user picks one and fills in the parameters, the client expands the template into the LLM's context as a structured user-or-system message — the LLM then has both a known-good starting prompt AND the agent's normal tool access to act on it.

So a "recipe" in this RFC = an MCP prompt that bundles a domain-specific task description with sensible defaults the user can override. The LLM doesn't have to *know* how to set up a thermocouple sweep — the recipe tells it the right tool sequence and the user only fills in *"how many channels, what threshold, where to log."*

These ship with v0.1 and **are the marketing** — they're the difference between "another MCP server" and "the agent already knows how to run my lab." Each one becomes a YouTube demo and a README snippet:

- `setup_thermocouple_sweep` — multi-channel temperature, sample rate, SD logging, threshold alerting
- `battery_soak_test` — long-duration logging with hourly stat summaries
- `vibration_capture_fft` — burst capture with frequency-domain analysis
- `multi_channel_pressure_test` — the medical-R&D pattern called out in the README
- `wifi_provision_new_device` — out-of-box onboarding via agent

For the deferred "hosted recipe library" question (user-contributed recipes shared back to a public registry), see [Open Questions Q7](#q7-hosted-recipe-library).

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

#### Safety: `send_scpi` v0.2 design

`send_scpi` ships in v0.2 (see tool table above) and stays behind the `--allow-raw-scpi` startup flag. This subsection specs the design now so the v0.1 work on the structured-error contract (next subsection) lands a foundation the v0.2 implementation can plug straight into, and so reviewers can debate the safety model before we commit code.

The risk being managed is twofold: (1) destructive operations the agent shouldn't issue without explicit user intent; (2) plausibly-correct-looking SCPI that the LLM hallucinated and that *appears* to succeed at the device because the firmware just returns an error string the agent ignores. Two-tier safety net + the structured-error contract handle both:

**Tier 1: destructive deny-list (hard block)** — patterns refused unless the caller passes `confirmed: true` on that specific call. Initial list (case-insensitive on alpha; SCPI capital-letter abbreviation rules respected):

- `SD:FORmat` / `SYST:STOR:SD:FORmat`
- `SYST:FORceBoot` / `SYST:FORC*`
- `*RST` (full system reset)
- `SYST:REBoot` (deny unless `confirmed=true` — many legitimate uses)
- `CONF:DAC:SAVEcal` / `CONF:ADC:SAVEcal` (calibration NVM writes)
- `LAN:FACReset`, `LAN:FWUpdate`

Extensible via `--deny-scpi-pattern <regex>` startup flag. The list is intentionally small — it covers "rm -rf" class operations, not every command that mutates state.

**Tier 2: wiki-pattern warn (soft signal — hallucination check)** — at startup, parse a bundled snapshot of `01-SCPI-Interface.md` from the [`daqifi-nyquist-firmware.wiki`](https://github.com/daqifi/daqifi-nyquist-firmware/wiki/01-SCPI-Interface) repo, extract every published pattern, and pre-compute a canonical-form lookup table that respects the SCPI capital-letter abbreviation rules (`SYSTem:STReam:START` matches `SYST:STR:START`, `SYST:STR:STA`, `SYSTEM:STREAM:START`, etc.).

At dispatch time, if a raw command does NOT canonicalize to any published pattern, the MCP returns a structured warning whose copy explicitly invites the LLM to self-check for hallucination:

```jsonc
{
  "success": true,
  "response": "<scpi response>",
  "errors": [],
  "warnings": [{
    "code": "UNDOCUMENTED_SCPI",
    "message": "'SYST:FOO:BAR' isn't in the SCPI wiki (snapshot 2026-05-27).
      LLMs sometimes invent plausible-looking SCPI — verify the syntax before
      trusting the device response. If you believe the command is real
      (undocumented or post-snapshot) proceed; otherwise try a suggestion below.",
    "suggestions": ["SYST:FOO:BAZ", "SYST:FOO:BARQ?"]   // Damerau-Levenshtein ≤ 3
  }]
}
```

**Why this exact phrasing matters:** "you may have hallucinated" is the key prompt. Without it, the LLM treats the warning as bureaucratic noise; with it, the model is invited to introspect on its own reliability for this specific command. The warning is non-blocking — the firmware's own response is the ground truth. The wiki may lag firmware (per the daqifi-nyquist-firmware CLAUDE.md: "When you add or modify SCPI commands, update the wiki same-day"), so an unmatched command isn't automatically wrong; it's a request for the LLM to double-check itself before reading too much into the response.

**Wiki snapshot freshness:** the snapshot is bundled at build time via `scripts/refresh-scpi-wiki.sh` and the CI freshness check (snapshot must be ≤30 days old) both land in v0.1 under [#mcp-13](GITHUB_ISSUES.md#mcp-13) so the foundation is in place before v0.2 consumes it. Snapshot date is exposed in every warning so users can spot staleness even if CI is lenient.

**Mode interaction** (per the safety modes above):
- Server must be launched with `--allow-raw-scpi` regardless of mode
- `read-only` → `send_scpi` blocked unconditionally even with `--allow-raw-scpi`
- `control` → deny-list active; `confirmed=true` overrides on a per-call basis; wiki-warn always on
- `admin` → same as control (deny-list does NOT auto-relax — `confirmed=true` per call is still required for destructive patterns; that's the explicit confirmation the RFC requires for admin-mode destructive tools)

#### Structured error response contract (all SCPI tools — v0.1 work)

The error contract below applies to every typed SCPI tool in v0.1 (`configure_analog_channel`, `start_streaming`, `start_sd_logging`, etc.) as well as the deferred `send_scpi`. It's listed here because it's the foundation the `send_scpi` safety design plugs into, and because shipping typed-tool errors in this structured shape from v0.1 means we don't have a contract break when v0.2 lands.

Every SCPI tool — typed and raw — returns errors in a uniform shape so the model never has to grep free text to know whether something failed. Three error surfaces are scraped per call:

1. **Synchronous SCPI error queue (`SYST:ERR?`)** — drained BEFORE the command (clear baseline), then drained AFTER (return the delta). Catches `-101 Invalid character`, `-200 Execution error`, etc.

2. **Asynchronous LOG_E buffer (`SYST:LOG?`)** — opt-in via `scrapeLogAfter` (default `true` for `send_scpi`, default `false` for typed tools to avoid log-buffer drain on every call). After the command, drain the log buffer and return any `[ERROR]` / `[WARN]` lines that appeared during the call window. Note `SYST:LOG?` is destructive (clears the buffer).

3. **Readback failures** — for tools with a `ReadbackAsync` validator (e.g. `configure_wifi` → APPLY → poll `LAN:ADDR?` until non-zero or timeout), failure to validate within the timeout returns a `ReadbackFailedError`.

Response shape:

```jsonc
{
  "success": true,                         // false iff any class-1 error
  "response": "<scpi response text>",      // raw text or parsed value
  "errors": [
    { "source": "SCPI",     "code": -113, "message": "Undefined header" },
    { "source": "LOG_E",    "code": null, "message": "[WIFI] Connection lost" },
    { "source": "READBACK", "code": null, "message": "LAN:ADDR? returned 0.0.0.0 after 20s" }
  ],
  "warnings": [ /* UNDOCUMENTED_SCPI etc. */ ]
}
```

Rationale: a single SCPI command can affect multiple subsystems and generate errors that arrive asynchronously after the immediate response returns "OK". An MCP that returns only the immediate response would miss the `LOG_E` that fires 200 ms later when the WINC driver rejects the new SSID. The model can't act on errors it doesn't see.

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

### v0.2 — Power features and gated ops (target: fast-follow, ~3–4 weeks after v0.1)

**Priority:** ship v0.2 quickly so the MCP itself becomes the primary harness for exercising new firmware features. The DAQiFi firmware team currently types every new SCPI command into a Python test harness within hours of landing it; with `send_scpi` available behind the v0.2 safety net, that workflow moves entirely to Claude + the MCP and stops requiring a custom Python script per feature.

- `wait_for_condition` (event-style waiting for agents)
- Destructive SD ops (`delete_sd_file`, `format_sd_card`) behind admin mode
- Network reconfiguration (`configure_wifi`, `configure_static_ip`)
- Firmware update tools (`check_firmware_update`, `update_firmware`)
- `send_scpi` escape hatch behind `--allow-raw-scpi` (design specced — see [§Safety: send_scpi v0.2 design](#safety-send_scpi-v02-design))
- HTTP transport for hosted use cases

### v0.3+ — Ecosystem and platforms

- Python client package (`pip install daqifi`) with both async API and MCP-over-IPC option
- LangChain / LlamaIndex tool wrappers (auto-generated from the MCP surface)
- Cloud deployment story for fleet monitoring
- **LabVIEW-as-client example library** — a small set of LabVIEW VIs that show how to invoke the DAQiFi MCP from inside a LabVIEW dataflow graph (see [§Q6](#q6-labview-interop) for the framing).  Narrow scope, no per-VI support burden.
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

## Firmware-aware constraints

Items that aren't in the firmware README but ARE in `daqifi-nyquist-firmware`'s root `CLAUDE.md` / project memory. Tyler's RFC didn't cover these because they're firmware-team knowledge; the MCP needs to enforce them so the LLM doesn't trip them silently.

### Quiescence rule — no SCPI during a benchmarked stream

The firmware has a documented constraint (`CLAUDE.md` → "Quiescence Rule" + memory `feedback_no_scpi_during_benchmark`): **any SCPI query issued while a streaming session is in progress can throttle the encoder enough to corrupt the very measurement being collected.** Polling `STAT:QUES:COND?` at 1 Hz during a 3 kHz WiFi run silently turns `Wst=6500` (real drops) into `Wst=0` (the encoder yielded so often it didn't generate the drops). Out-of-band visibility (Saleae, Wireshark, PC iperf2 log) is the only safe inspection during a stream.

This is the #1 footgun for an LLM driving the device. Without enforcement, an agent reasoning "I'll just check on the stream every few seconds" will trash benchmark integrity *and not know it*.

**MCP enforcement:**

- `DeviceRegistry` tracks `isStreaming` per handle (set by `start_streaming`, cleared by `stop_streaming` / disconnect / auto-stop).
- A `RequiresQuiescence` attribute on tool methods causes the dispatcher to return a structured `quiescence_violation` error when called against a streaming handle. Default-on for every tool that issues SCPI; `stop_streaming` / `disconnect_device` / `get_stream_summary` / `read_stream_window` are the allowlisted exceptions (they read in-process ring-buffer state, not the device).
- The error message tells the model exactly what to do: *"Cannot run `get_device_status` while `<handle>` is streaming — it would invalidate the measurement (see firmware quiescence rule). Use `get_stream_summary` for in-process state, or call `stop_streaming` first."*
- Opt-out via `respectQuiescence: false` for tools that need to poll the device mid-stream for a legitimate reason (rare; mostly debugging). The opt-out is logged in the audit trail.

This is one of those rules that's much cheaper to enforce mechanically in v0.1 than to retrofit after the first "but my numbers were clean!" support ticket.

### Variant-aware defaults and capability-filtered tool surface

NQ1, NQ2, NQ3 differ in ADC resolution, channel counts, available peripherals (DAC on NQ3 only), and sensible default `voltagePrecision`. Tyler's tool table is variant-neutral; without per-variant smarts, the LLM has to know the differences and the user has to spell them out.

- `connect_device` caches a `BoardCapabilities` blob per handle (read from the firmware at connect time via `*IDN?` + `SYST:SYSInfoPB?`).
- `start_streaming` etc. consult that blob and apply sane defaults:
  - NQ1: `format=csv`, `voltagePrecision=4` (12-bit MC12bADC)
  - NQ3: `format=csv`, `voltagePrecision=6` (18-bit AD7609)
  - NQ2: `format=csv`, `voltagePrecision=7` (24-bit AD7173)
- DAC tools (v0.2) are filtered out of `list_tools` when the connected handle is an NQ1, or return a structured `not_supported_on_this_variant` error rather than producing wedged state. Same pattern for any future variant-specific tools.
- The capabilities are exposed as a resource (`daqifi://devices/{id}/capabilities`) — the authoritative answer to "what can this device do".

The LLM gets to write *"start streaming"* and have it Just Work on whichever board is connected, instead of *"start streaming with the right format for whichever ADC you have"*.

### ADC channel-enable: bitmask vs per-channel calls

Subtle firmware-side perf trap: enabling channels one at a time via `ENABle:VOLTage:DC <ch>,1` triggers the firmware's frequency-cap recompute once per call. Enabling 16 channels individually causes 16 recomputes (each one walking the channel-config table). The firmware also exposes a bitmask SCPI form that enables N channels with one recompute.

`configure_analog_channel` for a single channel is fine. But `set_active_channels(channels: int[])` should issue the bitmask form (one SCPI call) rather than looping over `configure_analog_channel`. The model thinks "set channels 0,3,4,7,12" once; the device sees one mask write, not five.

Worth specifying in the tool contract so the v0.1 implementation doesn't accidentally do the looped form.

### Credentials handling — no echo, no audit

WiFi passwords passed to `configure_wifi` are accepted as inputs but **never** appear in tool responses, audit logs, or error messages. This is a direct response to a 2026-05-07 incident recorded in project memory: the project leaked a real Tesla AP password from `batch.sh` that echoed substituted env vars during a debug session.

`ScpiAuditLog` (see below) masks any argument whose key name contains `pass`/`secret`/`key`/`token`, replacing the value with `***REDACTED***`. The masking is applied at log-emission time, not at input time — the password is still usable for the SCPI call.

---

## Cross-cutting infrastructure (v0.1 internal)

### ScpiAuditLog

Every SCPI command sent (with arguments masked for sensitive keys) and every response received is appended to a session-scoped audit log, retrievable via the resource `daqifi://session/audit`. This is what the model and the human read together when *"the WiFi configure didn't work"* and we need to retro-debug. Bounded (last 500 entries) to keep memory in check. The audit log also captures `quiescence_violation` opt-outs so we can see when the user explicitly bypassed the safety rail.

### RateLimiter

Per-handle rate limit (e.g. no more than 30 tool calls per second) prevents a runaway LLM loop from hammering the device. The firmware can handle bursts but we don't need the model to discover that the hard way — and rate-limit errors are a useful signal that the agent is in a degenerate loop.

### Logging conventions

Server writes structured logs (`Microsoft.Extensions.Logging`) to stderr — stdout is reserved for JSON-RPC and must stay clean. The MCP client (Claude Desktop, Cursor, Cline) typically tails stderr.

### Telemetry (opt-in)

Per [Q4 resolution](#q4-telemetry), the server can collect anonymous usage telemetry to help us learn what agents actually call and where pain points cluster. **Off by default.** Enable via `--telemetry` flag or `DAQIFI_MCP_TELEMETRY=1` env var.

**What's collected per tool call:**

| Field | Example | Why |
|---|---|---|
| `tool` | `"start_streaming"` | Which surfaces get used |
| `mode` | `"control"` | Which safety posture users actually pick |
| `outcome` | `"ok"` / `"scpi_error"` / `"quiescence_violation"` / `"timeout"` | Where failures cluster |
| `error_code` | `-200` (SCPI) or `null` | Group similar failures |
| `latency_ms` | `342` | Tail-latency outliers reveal device-side bottlenecks |
| `server_version` | `"0.1.0"` | Cross-version regression detection |
| `client_id` | `"claude-desktop/1.x"` (from MCP InitializeResult) | Which client ecosystems we serve |
| `os` | `"linux"` / `"win"` / `"mac"` | Platform skew |
| `device_variant` | `"NQ1"` / `"NQ2"` / `"NQ3"` | Variant-specific issue detection |
| `session_id` | random UUID, server-process-scoped | Sequence reconstruction within a session |
| `event_ts` | epoch ms | Time-bucketing for retention |

**What's EXPLICITLY NOT collected:**

- Tool *arguments* (channel numbers, frequencies, file paths, SCPI strings — too easy to leak research-sensitive setups)
- Tool *responses* / SCPI response text
- Device serials, MAC addresses, IP addresses, hostnames
- WiFi SSIDs, passwords, any credential
- File contents or filenames (the SD card might hold a customer's pre-clinical data)
- The user's `client_id` beyond the tool name (no per-user identifier; `session_id` is server-process-scoped and resets each launch)

**Wire shape:** batched HTTP POST to a daqifi.com endpoint (TBD with Tyler — likely a CloudFlare worker + bucket setup, cheap and easy). Batches of up to 100 events flushed every 60 s or on shutdown. Failed flushes drop silently (we don't degrade the user's session for telemetry).

**Disclosure & inspection:**

- README has a dedicated **Telemetry** section listing every field collected, where it goes, and how to disable
- `--telemetry-dry-run` mode prints every event that *would* be sent to stderr instead of sending — lets a paranoid user (or a procurement reviewer) verify exactly what we collect before opting in
- Server logs an `INFO` line on startup when telemetry is enabled: `Telemetry enabled — see README §Telemetry for details. Disable with --no-telemetry.`

**Retention:** 90 days raw event-level; indefinite as anonymized aggregates (counts, percentiles). Public dashboard at some later date if/when the data is interesting enough to be community-useful.

**Compliance:** the data shape is engineered specifically to fall below common compliance triggers — no identifiers, no contents, no metadata that could re-identify a specific user / lab / experiment. We don't sell, share with third parties, or use it for marketing personalization. Reviewers can verify all of this by reading the open-source server code and running `--telemetry-dry-run`.

---

## Cross-cutting concern for daqifi-core

`daqifi-core` already does most of what the MCP needs, but two small API additions would clean up the MCP layer and also benefit `daqifi-desktop`. Neither blocks v0.1; both are mentioned so they can be batched into a single follow-up PR if Tyler agrees.

### 1. `Task<DeviceMetadata> GetMetadataAsync()` on `DaqifiDevice`

The serial number, IP, part number, and firmware version arrive in the first protobuf info message after connect, but they aren't surfaced as typed properties until the consumer subscribes to events and processes the message. Today the MCP would have to:

```csharp
var tcs = new TaskCompletionSource<DeviceMetadata>();
device.ChannelsPopulated += (s, e) => tcs.TrySetResult(BuildMetadata(device));
device.Connect();
var metadata = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
```

A typed `GetMetadataAsync(CancellationToken)` that returns once the first protobuf info has been parsed (or throws on timeout) would replace that boilerplate with one line. Internal implementation can keep the event subscription pattern; the public surface is just cleaner.

### 2. Public `ExecuteConfirmedAsync(command, drainErrorQueue = true)`

`ExecuteTextCommandAsync` and `DrainErrorQueueAsync` are both `protected` instance methods on `DaqifiDevice`. The MCP's "send a SCPI command and tell me what errors it produced" wrapper needs both. Today we'd either (a) subclass `DaqifiDevice` just to expose them or (b) re-implement the SCPI text channel from scratch. Both are ugly.

Proposed signature:

```csharp
public Task<ConfirmedScpiResult> ExecuteConfirmedAsync(
    string command,
    bool drainErrorQueue = true,
    bool scrapeLogAfter = false,
    CancellationToken ct = default);

public record ConfirmedScpiResult(
    string ResponseText,
    IReadOnlyList<ScpiError> SyncErrors,
    IReadOnlyList<string> AsyncLogLines);
```

This is the building block for the [structured error response contract](#structured-error-response-contract-all-scpi-tools--v01-work) above. `daqifi-desktop` would also benefit — it currently reproduces parts of this pattern for its own SCPI-console feature.

---

## Open questions

Resolved on 2026-05-27 in discussion between Chris (hardware/firmware partner) and Claude. Tyler should override any of these in PR review if his view differs.

### Q1. Buyer segmentation

> AI-native marketing pulls greenfield buyers. Migrators (existing LabVIEW shops) won't switch on the AI story alone. Do we accept that, or invest in migration tooling?

**Answered (Chris, 2026-05-27):** Accept it; keep it light. NO native LabVIEW migration tool — the support burden of every customer's bespoke VI is permanent. Paid migration *assists* are fine on a per-engagement basis. For self-serve migration, LabVIEW users can lean on the DAQiFi MCP + Claude Code / their CLI of choice to walk them through translating VIs into MCP-driven workflows. We may publish examples eventually; not for v0.1.

(Related: Q6 — different direction. We *will* think about letting LabVIEW be a *client* of the MCP. See below.)

### Q2. Pricing strategy

> Does the AI story support a higher ASP, a SaaS tier (managed recipes, hosted dashboard), or neither? This needs to be decided *before* the launch copy is written.

**Answered (Chris, 2026-05-27):** MCP alone does not move pricing. A future SaaS offer (managed recipes, hosted dashboard, fleet management) likely would — defer that pricing decision until the SaaS scope is real.

### Q3. Agent action boundaries

> Is the default "control" mode the right safety posture, or do we ship "read-only" as default and require an opt-in flag for control?

**Answered (Chris, 2026-05-27):** `--mode=control` stays the default. Control is the most powerful feature and the whole pitch — making users opt in via a flag every time would kill the conversational magic in the launch demos. The destructive deny-list + per-call `confirm: true` + `--max-sample-rate-hz` / `--max-voltage-range` clamps are the mitigations; nothing in v0.1's control surface can permanently damage anything. Destructive ops (firmware update, SD format, factory reset) are all v0.2 and require admin mode + explicit per-call confirmation.

`--mode=read-only` remains explicit for CI / automated contexts where absolute certainty of no state change matters.

### Q4. Telemetry

> Do we want anonymous usage telemetry from the MCP server to learn what agents actually call? If yes, opt-in only, and stated clearly in the README.

**Answered (Chris, 2026-05-27):** Yes — telemetry is desired for product development and pain-point detection. **Strict opt-in** (off by default; user passes `--telemetry` or sets `DAQIFI_MCP_TELEMETRY=1`). README discloses exactly what's collected. See [§Telemetry (opt-in)](#telemetry-opt-in) below for the data shape and design.

### Q5. MCP stability contract

> What is our promise to users about tool name/schema stability?

**Answered (Chris, 2026-05-27):** Tyler's framing is right, tightened as follows:

- **Tool names** are stable from v0.1.0 onwards. Renaming a tool is a breaking change requiring a major-version bump.
- **Parameters**: *optional* fields may be added freely at any version. *Required* fields may not be added later; if a new required field is needed, ship it as optional in vN and required in v(N+1) with a deprecation window.
- **Tool removal** is the only "remove things" breaking change. Requires (a) one minor release with a `deprecated: true` schema flag + runtime warning, (b) next major bumps. Minimum **6 months** between deprecation announcement and removal — slow enough for a slow-moving lab user, fast enough that we don't carry dead code forever.
- **`experimental: true`** schemas (e.g. `send_scpi` in v0.2) carry NO stability promise. Their behavior, parameters, and existence may change in any minor release. Clearly flagged in the tool description.
- **Resource URIs** (`daqifi://...`) follow the same rules as tool names.

This mirrors the gRPC / Anthropic API / MCP SDK stability conventions — proven, low-surprise.

### Q6. LabVIEW interop

> Is there a story where the MCP can drive a LabVIEW VI as a tool call? Probably v0.3+, but flagging now because it could be a marketing weapon against NI.

**Answered (Chris, 2026-05-27):** **Inverted from the original question.** Instead of MCP-calls-out-to-LabVIEW (which carries permanent per-VI support burden), the more interesting direction is MCP-as-server-with-LabVIEW-as-client:

- A LabVIEW user drops a `DAQiFi MCP Tool Call.vi` (or similar) into their dataflow graph
- The VI internally opens a JSON-RPC connection to the DAQiFi MCP server (via stdio launch or local HTTP transport)
- LabVIEW dataflow inputs/outputs marshal to/from MCP tool call params/results
- LabVIEW users get programmatic device access without LabVIEW-side custom code, AND can keep using Claude / Cursor / Cline alongside

This is much narrower: a small example library (a few VIs + a brief README) rather than a featureful product. No support burden because the LabVIEW side is just a JSON-RPC client — we don't own any customer VIs.

Defer to v0.3+ at the earliest. For v0.1/v0.2, the marketing weapon is the published recipe + blog post showing *"I had Claude drive both DAQiFi and a legacy LabVIEW rig in the same conversation."*

### Q7. Hosted recipe library

> Recipes as MCP prompts ship in the binary. Do we also stand up a hosted directory where users contribute and rate recipes? Network effect potential, but support burden.

**Answered (Chris, 2026-05-27):** Three-stage rollout based on actual demand:

- **v0.1**: 5 in-binary recipes (Tyler's list). Discovery is "look in the MCP prompts list" + "read the README."
- **v0.2** (only if contribution requests appear): add a `recipes/` directory in this repo; point users at GitHub Discussions for sharing. Free, no support burden, GitHub-native contribution flow. Track: how often does it get used?
- **v0.3+** (only if `recipes/` proves itself): formalize a hosted directory (web app, search, ratings, parameterized prompts). Real eng cost + ongoing moderation burden — build it only when the flywheel has proven itself.

Building a hosted library in v0.1 would be infrastructure for a community that doesn't exist yet.

### Q8. DAQiFi Desktop alignment

> Does Desktop adopt the MCP internally as its control layer, or stay separate?

**Answered (Chris, 2026-05-27):** Stay separate for v0.1. Desktop and MCP both depend on `Daqifi.Core` directly; no coupling between them.

In v0.2, consider piloting **one Desktop feature** on top of the MCP — the SCPI Console is the natural candidate, since it benefits from the same wiki-warn + structured-error contract + audit log we're building. Low risk (developer-tool surface, not core acquisition path), high signal (we learn whether the contracts hold up under a non-LLM client). Evaluate broader migration in v0.3+ based on that pilot.

The "force a clean API" benefit Tyler cited is real, but we can get most of it from a single-feature pilot without committing the whole Desktop product to a contract we're still learning.

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
