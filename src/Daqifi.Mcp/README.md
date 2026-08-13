# Daqifi.Mcp

A [Model Context Protocol](https://modelcontextprotocol.io) (MCP) server that lets an AI agent
(Claude Desktop, Claude Code, Cursor, Codex, …) drive a DAQiFi Nyquist data-acquisition device:
discover it, connect, configure analog channels and sample rate, read live measurements, and run
on-device SD-card logging.

It is a thin layer over [`Daqifi.Core`](../Daqifi.Core) — all device/protocol logic lives there.
The server speaks MCP over **stdio**, so the client launches it as a subprocess.

## Tools

| Tool | Purpose |
|---|---|
| `discover_devices` | Find devices on USB/serial and WiFi. Call first; returns `device_id`s. |
| `connect_device` | Connect to a discovered `device_id`. |
| `list_connected_devices` | List currently-connected devices. |
| `disconnect_device` | Disconnect and release a device. |
| `get_device_status` | Connection state, streaming/logging flags, sample rate, enabled channels. |
| `list_channels` | All channels with type/enabled/direction. |
| `configure_analog_channels` | Enable exactly the given analog channels; disable the rest. |
| `configure_digital_channels` | Enable exactly the given digital channels; disable the rest. |
| `set_digital_direction` | Set a digital channel to `input` or `output`. |
| `set_digital_output` | Drive a digital channel high or low (switches it to output if needed). |
| `set_pwm_output` | Start PWM on a capable channel: duty 1-100%, shared frequency 6-50000 Hz. |
| `disable_pwm` | Stop PWM on a channel (pin is left high-impedance). |
| `set_sample_rate` | Set sample rate in Hz (ceiling depends on the enabled channel count; over-cap requests are rejected). |
| `read_channel_values` | Latest value on every enabled channel, with the timestamp it was sampled at. |
| `capture_samples` | A block of live data as rows: one row per sample tick, one column per channel. |
| `start_sd_logging` | Start on-device SD logging (**requires a USB/serial connection**). |
| `stop_sd_logging` | Stop SD logging. |
| `list_sd_files` | List the log files on the SD card, with size and creation date. |
| `get_sd_storage` | Free/used/total space on the SD card. |
| `download_sd_file` | Fetch a log file to this machine and (by default) parse it into a CSV. |
| `delete_sd_file` | Delete a file from the SD card. Destructive; blocked by `--read-only`. |

> **Two ways to get data, for two different jobs.** `read_channel_values` and `capture_samples`
> stream live to the agent — good for spot checks and short captures, bounded by a duration and a
> row budget so the answer stays a tool result rather than a file. SD logging is on-device: the
> device writes to its own card at full rate for as long as you like, and you retrieve it afterwards
> with `download_sd_file`, which writes the raw file and a CSV into this machine's temp directory.
>
> Both live tools start the device's stream only if nothing is streaming yet, and stop it again
> afterwards; a session that was already running is read and left alone. Neither works while the
> device is logging to its SD card — the data goes to the card instead of to this machine, so those
> calls are refused with that explanation rather than returning nothing.
>
> **Retrieve before you stream.** A live streaming session collapses the device's SD buffer
> (firmware #703), after which downloads come back empty until the device is reconnected or another
> SD recording re-arms it. Do the SD work first on a fresh connection.

## Run it

### Option A — install as a .NET global tool (recommended)

```bash
dotnet tool install -g Daqifi.Mcp     # provides the `daqifi-mcp` command (requires the .NET runtime)
```

### Option B — from source (development)

```bash
dotnet run --project src/Daqifi.Mcp
```

### Flags

```
--read-only               Expose discovery/introspection only; block configuration and logging.
--max-sample-rate-hz <n>  Reject set_sample_rate requests above <n> Hz.
-h, --help                Show help.
```

`--read-only` blocks anything that changes the device or the card: channel/rate configuration,
DIO/PWM output, start/stop logging, and `delete_sd_file`. Reading data back is still allowed —
`list_sd_files`, `get_sd_storage` and `download_sd_file` all work, since they change nothing on the
device (the download does write its two files into this machine's temp directory, which is the only
way the data can reach the agent at all).

The live tools sit on the line: reading a stream that is **already** running changes nothing and is
allowed, but starting one is a change, so `read_channel_values` and `capture_samples` are refused
under `--read-only` when the device is idle — and say so.

## Point your agent at it

An stdio MCP server is just a command the client launches. Every client config reduces to
**command + args**.

**Claude Desktop** — `claude_desktop_config.json`:
```json
{
  "mcpServers": {
    "daqifi": { "command": "daqifi-mcp", "args": [] }
  }
}
```

**Claude Code**:
```bash
claude mcp add daqifi -- daqifi-mcp
```

**Cursor** — `~/.cursor/mcp.json` (same shape as Claude Desktop).

**Codex CLI** — `~/.codex/config.toml`:
```toml
[mcp_servers.daqifi]
command = "daqifi-mcp"
args = []
```

During development, point the client at the source build instead:
`{ "command": "dotnet", "args": ["run", "--project", "/abs/path/to/src/Daqifi.Mcp"] }`.

Then plug in a DAQiFi over USB (or join its WiFi) and ask, e.g.:
*"Discover my DAQiFi, connect, enable analog channels 0–3 at 1 kHz, and start logging to the SD card."*
*"Stop the log, then download it and tell me the average on AI0."*
*"What voltage is on AI1 right now?"*
*"Capture two seconds off channels 0–3 and tell me if anything looks noisy."*

## Notes

- **stdout is reserved** for the MCP JSON-RPC stream; all logging goes to **stderr**.
- The server runs **locally** and talks to the device exactly like `Daqifi.Core` does — nothing
  is sent to the cloud.
