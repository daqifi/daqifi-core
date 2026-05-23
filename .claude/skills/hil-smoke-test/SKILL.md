---
name: hil-smoke-test
description: Run the hardware-in-the-loop smoke test against a USB-connected DAQiFi device — discover, connect, read metadata, stream briefly, verify samples flow, disconnect cleanly. Use after touching the SDK to confirm real-device integration still works. Triggers on "smoke test the device", "run hil test", "test with hardware", "verify device integration", "/hil", "/smoke-test".
user-invocable: true
allowed-tools:
  - Bash(dotnet build *)
  - Bash(dotnet run *)
  - Bash(ls *)
  - Read
---

# /hil-smoke-test — Hardware-in-the-Loop Smoke Test

A quick sanity check that the SDK still talks to a real DAQiFi device over
USB after you've made changes. Read-only with respect to persistent device
state (no SD card writes, no network reconfig, no firmware updates).

Your job here is **orchestration** — build, run the test, interpret the
exit code, help diagnose failures. The actual test logic lives in
[src/Daqifi.Core.SmokeTest/Program.cs](src/Daqifi.Core.SmokeTest/Program.cs)
and is deterministic — do **not** reimplement it inline or instruct the
user to copy-paste C# into a REPL. Always run the binary.

## How to run it

Build then run, in two separate commands so the user sees compile errors
distinct from runtime failures:

```bash
dotnet build src/Daqifi.Core.SmokeTest/Daqifi.Core.SmokeTest.csproj -c Release
dotnet run --project src/Daqifi.Core.SmokeTest -c Release --no-build -- [args]
```

Default invocation (auto-discover the USB device, stream 2s at 100 Hz on
channels 0–1) takes no args. If the user mentions a specific port, baud,
duration, or rate, pass them through:

| Flag | Default | When to pass it |
|---|---|---|
| `--port=<name>` | `auto` | User names a port, or auto-discovery picks the wrong one |
| `--baud=<int>` | `9600` | User has a non-default baud |
| `--rate=<hz>` | `100` | User wants higher throughput pressure (try `1000`) |
| `--duration=<seconds>` | `2` | Longer to surface slow leaks; shorter for tight iteration |
| `--channels=<bitmask>` | `3` | Decimal bitmask, e.g. `255` for 8 channels |

The smoke-test project targets `net9.0` (the SDK's minimum). No `-f`
flag needed.

## Interpreting exit codes

The test exits with a stable code. **Use the code, not the message text** —
the message wording may evolve.

| Exit | Meaning | Most likely cause | What to suggest |
|---|---|---|---|
| `0` | PASS | — | Report pass cleanly. |
| `2` | Bad arguments | Typo in flag | Show usage and re-run. |
| `10` | No device found | USB not connected / device off / driver missing / charge-only cable | Check power LED, swap cable, confirm `ls /dev/cu.*` shows a DAQiFi entry on macOS, or `ls /dev/ttyACM*` on Linux. |
| `11` | Connect / init failed | Device busy (another process holds the port), firmware mid-boot, bad serial state | Close DAQiFi Desktop / any other app holding the port. Power-cycle the device and retry. |
| `12` | Metadata missing | Device responded but reported empty part number — possibly bootloader / partial init | Power-cycle; if it persists, this is a regression worth investigating in the init path. |
| `13` | Degraded samples | Stream produced fewer samples than the threshold (~50% of `rate × duration`) | Re-run; if it repeats, the streaming or parsing path has regressed. Try a higher `--duration` to rule out cold-start latency. |
| `20` | No samples received | Stream command sent but zero analog data came back | High-signal regression — likely in `EnableAdcChannels`, `StartStreaming`, the protobuf consumer, or `DaqifiOutMessage.AnalogInData` mapping. Check recent diffs in `src/Daqifi.Core/Communication/Consumers/` and `Device/DaqifiDevice.cs`. |
| `99` | Unexpected exception | Bug in the SDK itself escaped to the smoke harness | Show the stack trace from stderr. |

## When you should re-run before declaring a failure

HIL tests are inherently a little flaky. **Re-run once** on exit codes `10`,
`11`, or `13` before reporting failure — USB enumeration timing, DTR
toggle races, and brief device-side latency can all produce one-shot
hiccups. Do **not** retry exit code `0`, `2`, `12`, `20`, or `99` — those
are deterministic states.

## What to report back to the user

After a successful run, summarize in one or two lines: device that was
tested (part number + serial), and sample throughput. Don't paste the full
stdout — the user can scroll if they want detail.

After a failure, lead with the exit code's meaning, then the most likely
cause from the table above, then ask what they want to do next. Don't
auto-spawn investigation tasks unless the user asks — failures here can
often be physical (cable, power) and a side-task wastes effort.

## Out of scope for this skill

If the user asks the smoke test to also exercise SD card operations,
firmware updates, or network reconfiguration: **decline and explain why**.
Each of those is destructive or hardware-modifying and belongs in its own
opt-in skill, not a smoke test. Offer to design one separately.
