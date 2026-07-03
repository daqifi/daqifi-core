# Daqifi.Mcp

A [Model Context Protocol](https://modelcontextprotocol.io) (MCP) server that lets an AI agent
(Claude Desktop, Claude Code, Cursor, Codex, …) drive a DAQiFi Nyquist data-acquisition device:
discover it, connect, configure analog channels and sample rate, and run on-device SD-card logging.

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
| `set_sample_rate` | Set sample rate in Hz (Nyquist hardware supports up to 1000 Hz). |
| `start_sd_logging` | Start on-device SD logging (**requires a USB/serial connection**). |
| `stop_sd_logging` | Stop SD logging. |

> SD logging is on-device: the device writes to its own SD card. Data does not stream back to the
> agent in this version (see the streaming-evolution plan).

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
--max-sample-rate-hz <n>  Clamp set_sample_rate to at most <n> Hz.
-h, --help                Show help.
```

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

## Notes

- **stdout is reserved** for the MCP JSON-RPC stream; all logging goes to **stderr**.
- The server runs **locally** and talks to the device exactly like `Daqifi.Core` does — nothing
  is sent to the cloud.
