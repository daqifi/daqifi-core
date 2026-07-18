# ADR 0001: Firmware-version-aware feature gating

- **Status:** Accepted (2026-06-19)
- **Issue:** [#251](https://github.com/daqifi/daqifi-core/issues/251)
- **Follow-ups:** [#254](https://github.com/daqifi/daqifi-core/issues/254) (floor + `-113` backstop), [#255](https://github.com/daqifi/daqifi-core/issues/255) (dead-code removal), [#256](https://github.com/daqifi/daqifi-core/issues/256) (deferred table + #327 reader)
- **Supersedes:** —

> **Note on the evidence section.** §"Context — firmware audit" below is a *living*
> reference (it changes every firmware release); the rest of this ADR is the immutable
> decision. If the audit starts drifting, lift it into a standalone living doc and link it
> from here rather than editing the decision.

## Context

daqifi-core issues SCPI commands that do not all exist on every firmware a fielded device
might run. There is no single place that answers *"can this device do X?"*, so we keep
adding ad-hoc, per-feature checks:

- `GetSdCardStorageAsync()` → `SYSTem:STORage:SD:SPACe?` exists only on firmware
  **≥ v3.4.6b1** (firmware [#202](https://github.com/daqifi/daqifi-nyquist-firmware/pull/202)).
  On older firmware the device replies `**ERROR: -113, "Undefined header"` and the SDK
  surfaces a **generic `SdCardOperationException`** — no clean "your firmware is too old"
  signal.
- Analog output (`SOURce:VOLTage:LEVel` / `CONFigure:DAC:*`, PR [#250](https://github.com/daqifi/daqifi-core/pull/250))
  is **NQ3-only** — gated by board variant, not version.
- The firmware capability framework ([#327](https://github.com/daqifi/daqifi-nyquist-firmware/issues/327))
  is itself a firmware-gated query (`CONFigure:CAPabilities:JSON?`, fw ≥ v3.5.0).

daqifi-core is the right place to own *what a given device can and cannot do*.

### Context — firmware audit (living evidence)

Source: `daqifi-nyquist-firmware` git history. **"First released version" = the first
release tag whose history contains the introducing commit** (`git tag --contains`) — the
client-relevant boundary, not the commit date.

**Release timeline (tag → date):** v3.0.0b0 (2025-01-14) → v3.0.0b2 (2025-08-04) →
v3.1.0b2 (2025-10-09) → v3.2.0 (2025-11-06) → v3.4.3 (2026-01-30) → v3.4.4 (2026-02-06) →
v3.4.6b1 (2026-03-12) → v3.5.0 (2026-06-08) → v3.6.0 (2026-06-12) → v3.6.1 → v3.6.3 →
**v3.7.0** → v3.7.1 → **v3.7.2** (current firmware HEAD). SD file transfer over WiFi
(firmware #598/#599, commit `bf105585` = `git describe` v3.6.3-6) first shipped in the
**v3.7.0** release tag (`git tag --contains bf105585`).

**Feature → first released firmware (× board variant):**

| Feature (daqifi-core surface) | SCPI command | First released | Board / HW | Firmware ref |
|---|---|---|---|---|
| ADC / DIO / PWM / WiFi config / USB transparent | various | v3.0.0b0 (baseline) | all | #19 |
| Streaming start/stop (legacy verbs) | `SYSTem:StartStreamData` / `StopStreamData` | v3.0.0b0 (baseline) | all | #19 |
| **Analog output (DAC)** | `SOURce:VOLTage:LEVel`, `CONFigure:DAC:*` | **v3.2.0** | **NQ3 only** | [#117](https://github.com/daqifi/daqifi-nyquist-firmware/pull/117) |
| SD card storage (list/get/delete/format) | `SYSTem:STORage:SD:*` | ≤ v3.4.3 (predates clean tag history) | needs SD HW | — |
| SD storage space query | `SYSTem:STORage:SD:SPACe?` | v3.4.6b1 | needs SD HW | [#202](https://github.com/daqifi/daqifi-nyquist-firmware/pull/202) |
| SD min-free threshold | `SYSTem:STORage:SD:MINFree` | v3.5.0 | needs SD HW | #502 |
| SD logging filename | `SYSTem:STORage:SD:FILE` (hard rename of `SD:LOGging`, no alias) | v3.5.0 | needs SD HW | [#323](https://github.com/daqifi/daqifi-nyquist-firmware/pull/323) |
| Dynamic memory management | `SYSTem:MEMory:*` | v3.4.6b1 | all | #227 |
| Capability document | `CONFigure:CAPabilities:JSON?` / `:APIVersion?` | v3.5.0 | all | [#327](https://github.com/daqifi/daqifi-nyquist-firmware/issues/327)/#343 |
| WiFi associated-AP MAC | `SYSTem:COMMunicate:LAN:BSSID?` | v3.5.0 | WiFi | #516 |
| WiFi throughput finder | `SYSTem:STReam:WIFI:FINd?` | v3.5.0 | WiFi | #521 |
| In-firmware iperf2 | `SYSTem:WIFI:IPERF:*` | v3.5.0 | WiFi | #377 |
| **SD file transfer over WiFi** (list/get/delete routed to the requesting interface) | `SYSTem:STORage:SD:LIST?` / `:GET` / `:DELete` over TCP | **v3.7.0** | **WiFi + SD HW** | [#598/#599](https://github.com/daqifi/daqifi-nyquist-firmware/pull/598) |

> **Read this table through the v3.5.0 floor (Decision 1).** Every command daqifi-core issues
> over USB first shipped **at or before v3.5.0**, so over USB on *supported* firmware the only
> differences left are *hardware* capability (NQ3 for DAC, SD-card presence, WiFi presence),
> board-derived and already handled by `DeviceCapabilities.FromDeviceType`.
>
> **The first live, above-floor firmware-version gate is now active:** SD file transfer over
> **WiFi/TCP** requires firmware **≥ v3.7.0** (#598/#599). This is not a hardware gate — the same
> SD-capable WiFi device supports it on v3.7.0+ and rejects it below — so it is enforced at the
> transport in `EnsureSdFileTransferSupportedOnTransport()` (LIST/GET/DELETE), which throws
> `FeatureNotSupportedException(SdFileTransferOverWifi, v3.7.0, …)` when the active transport is
> not USB and the reported firmware is older/unparseable. Over USB these operations are
> unchanged (available on all SD-capable firmware). This is exactly the forward-looking case the
> version-gating layer was built for — a command consumed *after* the floor.

**Breaking changes (behavior), by released version:**

- **v3.2.0** — DAC commands are board-gated: issuing them on NQ1/NQ2 returns an error
  (firmware checks `BoardVariant != 3` in `SCPIDAC.c`).
- **v3.5.0** — **SD logging filename hard-renamed** `SYSTem:STORage:SD:LOGging "f"` →
  `SYSTem:STORage:SD:FILE "f"` with **no alias**
  ([#323](https://github.com/daqifi/daqifi-nyquist-firmware/pull/323)). daqifi-core's
  `SetSdLoggingFileName` sent the old name, so on v3.5.0+ the device rejected it with
  `-113 "Undefined header"` — silently, because logging is fire-and-forget. Fixed by core
  [#253](https://github.com/daqifi/daqifi-core/pull/253) (now sends `SD:FILE`
  unconditionally — correct under the floor).
- **v3.5.0** — `CONFigure:ADC:CHANnel` / `ENAble:VOLTage:DC` are now **rejected while
  streaming** ([#527](https://github.com/daqifi/daqifi-nyquist-firmware/pull/527)). Stop
  streaming before toggling channels.
- **v3.5.0** — the 1 kHz Type-2 muxed scan-rate cap was **removed**; Type-2 now obeys the
  transport cap ([#528](https://github.com/daqifi/daqifi-nyquist-firmware/pull/528)).
- **v3.6.1 (pending)** — `CONFigure:CAPabilities:JSON?` bounds were aligned to the actual
  setter bounds ([#548](https://github.com/daqifi/daqifi-nyquist-firmware/pull/548)). Clients
  that parse the capability JSON to infer setter ranges may see different values.

**SCPI renames in v3.5.0 — compatibility is per-command, not per-release.** The #311
stream-control consolidation shipped in **v3.5.0** through follow-up PRs #322/#323/#324; only
the umbrella commit #311 itself is unmerged. Three renames landed in the *same* release with
*three different* compatibility outcomes — which is why a "breaking renames" batch keyed on a
release (the now-closed core [#168](https://github.com/daqifi/daqifi-core/pull/168)) doesn't
model reality (seed data contributed on [#251](https://github.com/daqifi/daqifi-core/issues/251)):

| daqifi-core method | Old → New | Firmware handling | Breaking for core? |
|---|---|---|---|
| `StartStreaming` / `StopStreaming` | `SYSTem:StartStreamData` / `StopStreamData` → `STReam:START` / `STOP` | both kept as **aliases** ([#324](https://github.com/daqifi/daqifi-nyquist-firmware/pull/324)) | no — old name works on all fw |
| `SetUsbTransparencyMode` | `USB:SetTransparentMode` → `USB:TRANSparent:MODE` | both kept as **aliases** | no |
| `SetSdLoggingFileName` | `STORage:SD:LOGging` → `STORage:SD:FILE` | **hard rename, no alias** ([#323](https://github.com/daqifi/daqifi-nyquist-firmware/pull/323)) | **yes** — see above |
| `GetSdLoggingState` | `STORage:SD:LOGging?` | **never existed in firmware** | dead code — remove |

Verified at the v3.5.0 tree: `STReam:START`, `StartStreamData`, `USB:TRANSparent:MODE`,
`SetTransparentMode`, and `STORage:SD:FILE` are all present, while `STORage:SD:LOGging` is
**gone**. Core #253 corroborated the `-113` behavior on hardware (old `SD:LOGging` →
`-113 "Undefined header"`, new `SD:FILE` → OK) — direct empirical support for the probe
backstop below.

> **We depend on firmware keeping the aliases.** Streaming/USB old names work everywhere
> *because firmware chose to alias them*. #311 is marked a breaking refactor (`!`); if a
> future cleanup ever drops the legacy aliases, our un-gated old names break. Track #311's
> end-state. (When that day comes, the new names are safe to send unconditionally anyway,
> since they exist on all supported — i.e. ≥ v3.5.0 — firmware.)

### Enabling fact: undefined-header is distinguishable on the wire

The firmware emits SCPI errors **inline** on both transports — `UsbCdc.c:1052` and
`wifi_tcp_server.c:211` both format `**ERROR: %d, "%s"\r\n`. An unknown command yields
**`-113, "Undefined header"`** (libscpi `SCPI_ERROR_UNDEFINED_HEADER`) — a *different code*
from a runtime `-200, "Execution error"`. daqifi-core already recognizes `**ERROR` lines
(`ScpiResponseClassifier.IsErrorResponseLine`, `DaqifiStreamingDevice.IsScpiErrorLine`) but
does not yet parse the numeric code. Parsing it is the concrete hook that makes a
probe-and-detect backstop reliable.

## Decision

### Decision 1 — Minimum supported firmware is **v3.5.0**

"Supported" = the baseline we build and test against, and the version at/above which all
documented behavior holds. This is not a hard refusal to connect to older devices; it is the
line below which we don't promise correctness. Rationale: v3.5.0 is the bench/reference
firmware, it's where the SD-logging hard rename, the capability document, and the current
streaming/SD command surface all settled, and core #253 already hard-requires it for SD
logging.

**Consequence — this collapses most of the gating problem:**

- Every command daqifi-core issues today exists on all supported firmware → **no version
  table entries are needed yet**. Don't build the table until we consume a post-v3.5.0
  command.
- The **version-selected-command** mode (send the old name on old firmware) is **unnecessary
  and out of scope** — there is no supported firmware that needs the pre-rename names. Core
  #253's unconditional `SD:FILE` is correct as-is.
- The only *live* capability axis today is **board / hardware** (NQ3 → DAC; SD-card present;
  WiFi present), which `DeviceCapabilities.FromDeviceType` already derives.

### Decision 2 — One stable consumer seam; gate on board + a single firmware floor

Expose `device.Supports(DeviceFeature feature)` as the stable contract. Consumers branch on
it and **never** compare firmware strings. Behind it, two live sources today and a
forward-looking third:

1. **Board / hardware capability (live)** — `FromDeviceType` already answers "is this NQ3?",
   "does it have an SD card / WiFi?". DAC gating is *this*, not version gating.
2. **Firmware floor + typed backstop (live, primary near-term work)** — a device below
   v3.5.0, or any post-floor feature a device lacks, is surfaced as a typed
   `FeatureNotSupportedException` instead of a generic error. The authoritative signal is the
   firmware's `**ERROR: -113` on the command path; an optional up-front version check (using
   the existing [`FirmwareVersion`](../../src/Daqifi.Core/Firmware/FirmwareVersion.cs)) can
   pre-empt the round-trip for UI.
3. **`DeviceFeature` version table (deferred)** — introduce only when we start consuming a
   command newer than the floor. Same shape as below; empty today, so unbuilt today.
4. **#327 capability document (later, non-blocking)** — when a device answers
   `CONFigure:CAPabilities:APIVersion?` ≥ 1, populate `DeviceCapabilities` from the live
   `CONFigure:CAPabilities:JSON?` document. The floor/board logic remains the bootstrap and
   the fallback for anything that predates or omits the query. Note: #327 the *issue* is
   closed, but the capability *commands* shipped in v3.5.0 and are maintained (#548), so this
   is a real growth path — we just don't lean on "#327 the framework."

### Evaluate `Supports` lazily — don't cache version-derived flags

`DeviceMetadata.UpdateFromProtobuf` rebuilds `Capabilities` from `FromDeviceType(DeviceType)`
when the **part number** arrives, but assigns `FirmwareVersion` in a *separate* branch (and
`FromDeviceType` doesn't even see the version). A precomputed `SupportsX` bool would
therefore be snapshotted before/without the firmware version and silently go stale. So:

- `Supports(DeviceFeature)` **evaluates against the current metadata at call time** (board +
  firmware version), rather than reading cached booleans.
- Any convenience properties (`SupportsAnalogOutput`, …) **delegate** to `Supports(...)`;
  nothing version-derived is stored. This keeps the two from ever diverging.

### Proposed API shape

```csharp
namespace Daqifi.Core.Device;

public enum DeviceFeature
{
    AnalogOutput,        // SOURce:VOLTage:LEVel / CONF:DAC — board gate: NQ3 only
    // Below the v3.5.0 floor, so universally present on supported firmware — listed for the
    // typed-exception backstop against *below-floor* devices, not because they vary on
    // supported firmware:
    SdStorageQuery,      // SYSTem:STORage:SD:SPACe?   (fw v3.4.6b1)
    CapabilityDocument,  // CONFigure:CAPabilities:JSON? (fw v3.5.0)
    // … add post-v3.5.0 commands here as we consume them; that is when the table is built.
}

// Device/FeatureNotSupportedException.cs — the typed backstop.
public sealed class FeatureNotSupportedException : Exception
{
    public DeviceFeature Feature { get; }
    public FirmwareVersion? RequiredVersion { get; }  // e.g. the v3.5.0 floor, or a feature min
    public string? ActualVersion { get; }
    public DeviceType? Board { get; }
}

// DeviceCapabilities seam — evaluated lazily against current board + firmware version.
public bool Supports(DeviceFeature feature);
```

Call-site pattern (e.g. `GetSdCardStorageAsync`):

```csharp
// optional up-front gate (no round-trip) — lets desktop disable the control:
if (!Capabilities.Supports(DeviceFeature.SdStorageQuery))
    throw new FeatureNotSupportedException(DeviceFeature.SdStorageQuery, MinSupportedFirmware, FirmwareVersion);

// … issue command … the wire backstop is the authoritative signal:
if (ResponseHasUndefinedHeader(lines))   // **ERROR: -113
    throw new FeatureNotSupportedException(DeviceFeature.SdStorageQuery, MinSupportedFirmware, FirmwareVersion);
```

(`CommandRename`/version-selected emit from earlier drafts is **dropped** — Decision 1 makes
it unnecessary. Reintroduce only if the supported floor is ever lowered below a hard rename.)

## Alternatives considered

| Option | Why not (alone) |
|---|---|
| **No floor; full version table from day one** | Builds machinery to support arbitrarily-old firmware we don't test; the table is *empty* once the floor is at v3.5.0. Pure speculation cost. |
| **Version table only** | Brittle against betas/backports; a stale entry silently mis-gates; can't prove the real device. |
| **Probe + detect only** | Needs a round-trip and can't gate UI up front; only knows after you try. (But it's always correct — so it's the backstop, not the whole answer.) |
| **#327 capability doc only** | Only fw ≥ v3.5.0; the query is itself firmware-gated, so a fallback is required regardless — it can't be the sole source. |

The decision is: **a v3.5.0 floor + board-derived capability (both live) + the `-113` typed
backstop (always correct), with the version table and #327 reader deferred until they earn
their place.**

## Consequences

**Positive**
- The immediate surface shrinks to almost nothing: board gating already exists; the only new
  code is the floor constant + the `-113` → `FeatureNotSupportedException` backstop.
- Today's generic `SdCardOperationException` for old firmware becomes a clear, typed
  `FeatureNotSupportedException` carrying required-vs-actual version.
- The seam is stable, so the deferred table and #327 reader slot in later without touching
  consumers.

**Negative / costs**
- A floor is a support commitment: pre-v3.5.0 devices get best-effort behavior and typed
  "too old" errors, not guarantees. Acceptable, and explicit.
- When we *do* consume a post-floor command, someone must add its `DeviceFeature` + min
  version. The backstop keeps that honest (it's correct even if the entry is missing).

**Follow-up implementation issues**
1. [#254](https://github.com/daqifi/daqifi-core/issues/254) — **`MinSupportedFirmware = v3.5.0`
   + `FeatureNotSupportedException` + `-113` backstop** on `GetSdCardStorageAsync` (the guard
   deferred from [#214](https://github.com/daqifi/daqifi-core/pull/214)). *Primary near-term
   deliverable.*
2. [#255](https://github.com/daqifi/daqifi-core/issues/255) — **Remove dead code**: the
   `GetSdLoggingState` producer + `SYSTem:STORage:SD:LOGging?` query never existed in the
   firmware SCPI table. Public-API removal, separate from the #253 fix.
3. [#256](https://github.com/daqifi/daqifi-core/issues/256) *(deferred)* — `DeviceFeature`
   version table + lazy `Supports(...)` (only when we consume a post-v3.5.0 command), and the
   `CONFigure:CAPabilities:JSON?` reader (#327 growth path).

## Out of scope

The full #327 capability-document reader; the version table (until a post-floor command needs
it); version-selected command emit (obviated by the floor). This ADR covers the
investigation, the strategy decision, the v3.5.0 floor, and the minimal board-gate + typed
`-113` backstop.
