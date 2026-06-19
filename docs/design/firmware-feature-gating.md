# RFC: Firmware-version-aware feature gating

Status: **Draft / for discussion** · Issue: [#251](https://github.com/daqifi/daqifi-core/issues/251) · Author: investigation pass

## 1. Problem

daqifi-core issues SCPI commands that do not all exist on every firmware a device in
the field might be running. Today there is no single place that answers *"can this
device do X?"*:

- `GetSdCardStorageAsync()` → `SYSTem:STORage:SD:SPACe?` exists only on firmware
  **≥ v3.4.6b1** (firmware [#202](https://github.com/daqifi/daqifi-nyquist-firmware/pull/202)).
  On older firmware the device replies `**ERROR: -113, "Undefined header"` and the SDK
  surfaces a **generic `SdCardOperationException`** — there is no clean "your firmware is
  too old" signal.
- Analog output (`SOURce:VOLTage:LEVel` / `CONFigure:DAC:*`, PR [#250](https://github.com/daqifi/daqifi-core/pull/250))
  is **NQ3-only** — gated by board variant, not version.
- The firmware capability framework ([#327](https://github.com/daqifi/daqifi-nyquist-firmware/issues/327))
  is itself a firmware-gated query (`CONFigure:CAPabilities:JSON?`, fw ≥ v3.5.0).

This is a recurring pattern. Every SCPI command we add has a "first firmware that
supports it" boundary and sometimes a board-variant constraint. daqifi-core is the right
place to own *what a given device can and cannot do*.

## 2. Verified firmware history

Source: `daqifi-nyquist-firmware` git history. **"First released version" = the first
release tag whose history contains the introducing commit** (`git tag --contains`), which
is the client-relevant boundary — not the commit date.

### Release timeline (tag → date)

| Version    | Date (tag)  | Notes |
|------------|-------------|-------|
| v3.0.0b0   | 2025-01-14  | Baseline of recorded history. ADC, DIO, PWM, streaming (legacy `StartStreamData`), LAN, USB transparent. |
| v3.0.0b2   | 2025-08-04  | |
| v3.1.0b2   | 2025-10-09  | Does **not** yet contain NQ3/DAC. |
| v3.2.0     | 2025-11-06  | **First release with NQ3 board + DAC analog output** (#117, commit 2025-10-21). |
| v3.4.3     | 2026-01-30  | SD-card storage base commands present by here. |
| v3.4.4     | 2026-02-06  | |
| v3.4.6b1   | 2026-03-12  | **`SYSTem:STORage:SD:SPACe?`** (#202, commit 2026-03-08); dynamic memory mgmt `SYSTem:MEMory:*` (#227). |
| v3.5.0     | 2026-06-08  | Capability framework, iperf2, WiFi stream finder, BSSID query, channel-change-while-streaming block, Type-2 cap removal. |
| v3.6.0     | 2026-06-12  | ADC read-path re-engineering (#543); streaming fixes. |
| v3.6.1     | 2026-06-18 (HEAD) | CONF:CAP JSON bounds alignment (#548). Not yet a published git tag. |

### Feature → first released firmware (× board variant)

| Feature (daqifi-core surface) | SCPI command | First released | Board | Firmware ref |
|---|---|---|---|---|
| ADC / DIO / PWM / WiFi config / USB transparent | various | v3.0.0b0 (baseline) | all | #19 |
| Streaming start/stop (legacy verbs) | `SYSTem:StartStreamData` / `StopStreamData` | v3.0.0b0 (baseline) | all | #19 |
| **Analog output (DAC)** | `SOURce:VOLTage:LEVel`, `CONFigure:DAC:*` | **v3.2.0** | **NQ3 only** | [#117](https://github.com/daqifi/daqifi-nyquist-firmware/pull/117) |
| SD card storage (list/get/delete/format) | `SYSTem:STORage:SD:*` | ≤ v3.4.3 (predates clean tag history) | needs SD HW | — |
| **SD storage space query** | `SYSTem:STORage:SD:SPACe?` | **v3.4.6b1** | needs SD HW | [#202](https://github.com/daqifi/daqifi-nyquist-firmware/pull/202) |
| SD min-free threshold | `SYSTem:STORage:SD:MINFree` | v3.5.0 | needs SD HW | #502 |
| Dynamic memory management | `SYSTem:MEMory:*` | v3.4.6b1 | all | #227 |
| **Capability document** | `CONFigure:CAPabilities:JSON?` / `:APIVersion?` | **v3.5.0** | all | [#327](https://github.com/daqifi/daqifi-nyquist-firmware/issues/327)/#343 |
| WiFi associated-AP MAC | `SYSTem:COMMunicate:LAN:BSSID?` | v3.5.0 | WiFi | #516 |
| WiFi throughput finder | `SYSTem:STReam:WIFI:FINd?` | v3.5.0 | WiFi | #521 |
| In-firmware iperf2 | `SYSTem:WIFI:IPERF:*` | v3.5.0 | WiFi | #377 |

### Breaking changes (behavior, by released version)

- **v3.2.0** — DAC commands are board-gated: issuing them on NQ1/NQ2 returns an error
  (firmware checks `BoardVariant != 3` in `SCPIDAC.c`).
- **v3.5.0** — `CONFigure:ADC:CHANnel` / `ENAble:VOLTage:DC` are now **rejected while
  streaming** ([#527](https://github.com/daqifi/daqifi-nyquist-firmware/pull/527)). A client
  that toggles channels mid-stream now gets an error instead of silent (corrupting)
  behavior. Stop streaming first.
- **v3.5.0** — the 1 kHz Type-2 muxed scan-rate cap was **removed**; Type-2 now obeys the
  transport cap ([#528](https://github.com/daqifi/daqifi-nyquist-firmware/pull/528)).
  Max-scan-rate semantics changed.
- **v3.6.1** — `CONFigure:CAPabilities:JSON?` bounds were aligned to the actual setter
  bounds ([#548](https://github.com/daqifi/daqifi-nyquist-firmware/pull/548)). Clients that
  parsed the capability JSON to infer setter ranges may see different values.

### Not yet released (do not gate against a version yet)

- **Stream-control namespace rename** `SYSTem:STReam:START/STOP/DATA?`
  ([#311](https://github.com/daqifi/daqifi-nyquist-firmware/pull/311)) lives only on the
  firmware branch `refactor/311-scpi-stream-control` — it is **not merged** and **not in any
  release tag** (≤ v3.6.1). daqifi-core's current producers correctly still send the legacy
  `SYSTem:StartStreamData` verbs, and PR [#168](https://github.com/daqifi/daqifi-core/pull/168)
  ("update producers for SCPI breaking renames") is correctly **held** pending the firmware
  merge. This is the canonical example of *why a version table must track released tags, not
  commit dates.*

## 3. Key enabling fact: undefined-header is distinguishable on the wire

The firmware emits SCPI errors **inline** on both transports —
`UsbCdc.c:1052` and `wifi_tcp_server.c:211` both format
`**ERROR: %d, "%s"\r\n`. An unknown command yields **`-113, "Undefined header"`**
(libscpi `SCPI_ERROR_UNDEFINED_HEADER`). This is a *different code* from a runtime
failure such as `-200, "Execution error"`.

daqifi-core already recognizes `**ERROR` lines (`ScpiResponseClassifier.IsErrorResponseLine`,
`DaqifiStreamingDevice.IsScpiErrorLine`) but does not yet parse the numeric code. Parsing
the code is the concrete hook that makes a **probe + detect** backstop reliable: `-113`
means "this firmware doesn't have the command," which we can map to a typed
`FeatureNotSupportedException` instead of the generic `SdCardOperationException`.

## 4. Strategy: layered, one consumer seam

Three candidate data sources, each with a real weakness:

| Source | Strength | Weakness |
|---|---|---|
| **Version table** (min-version × board) | Cheap, up-front (can gray out UI before any I/O) | Brittle vs. betas/backports; needs per-release maintenance |
| **Probe + detect** (`-113` on the wire) | Always correct against the *actual* device | Needs a round-trip; only knows after you try |
| **Capability doc** (#327 `CAP:JSON?`) | Device self-describes; vendor-neutral | Only fw ≥ v3.5.0; the query is itself gated, so a fallback is still required |

**Recommendation: use all three behind one stable consumer seam.** They are
complementary, not competing.

```
          consumers (desktop, python-core)
                     │
            device.Supports(DeviceFeature.X)   ← STABLE contract; never compares versions
                     │
   ┌─────────────────┼───────────────────────────┐
   │                 │                            │
 version table   capability doc (#327)      probe backstop
 (default /      (when APIVersion? ≥ 1,      (-113 on the command
  bootstrap /     fw ≥ v3.5.0)                path → FeatureNotSupported)
  permanent
  fallback)
```

1. **Consumer seam (stable):** `device.Supports(DeviceFeature feature) → bool`, plus a
   small number of named flags on `DeviceCapabilities` for the common cases
   (`SupportsSdStorageQuery`, `SupportsAnalogOutput`, …). Consumers branch on these and
   **never** parse `FirmwareVersion` themselves. This contract does not change as the data
   source underneath evolves.

2. **Interim data source — static version table** (`DeviceFeatureTable`): a list of
   `(DeviceFeature, MinFirmwareVersion, Board[]?)` requirements, evaluated against the
   existing [`FirmwareVersion`](../../src/Daqifi.Core/Firmware/FirmwareVersion.cs) parsed
   from `DeviceMetadata.FirmwareVersion` and the detected `DeviceType`. This is the
   **UI source-of-truth**: lets desktop disable controls up front with no round-trip.

3. **Command-path backstop — typed `FeatureNotSupportedException`:** on the actual call,
   if the response carries `**ERROR: -113`, throw
   `FeatureNotSupportedException(feature, requiredVersion, actualVersion)` instead of the
   generic error. Always correct even when the table is stale (a backported beta, an
   unexpected build). This is the guard deferred from PR
   [#214](https://github.com/daqifi/daqifi-core/pull/214); land it first on
   `GetSdCardStorageAsync`.

4. **Growth into #327:** when a device answers `CONFigure:CAPabilities:APIVersion?` ≥ 1
   (fw ≥ v3.5.0), populate `DeviceCapabilities` from the live `CONFigure:CAPabilities:JSON?`
   document instead of the table. The table stays as the **permanent bootstrap/fallback** for
   firmware that predates the capability query (< v3.5.0) — which is exactly why #327 can
   never fully replace it. The consumer seam is unchanged.

## 5. Proposed API shape

```csharp
namespace Daqifi.Core.Device;

public enum DeviceFeature
{
    SdStorageQuery,      // SYSTem:STORage:SD:SPACe?        fw >= 3.4.6b1, needs SD
    AnalogOutput,        // SOURce:VOLTage:LEVel / CONF:DAC fw >= 3.2.0,  NQ3 only
    CapabilityDocument,  // CONFigure:CAPabilities:JSON?    fw >= 3.5.0
    SdMinFreeThreshold,  // SYSTem:STORage:SD:MINFree       fw >= 3.5.0, needs SD
    WifiBssidQuery,      // SYSTem:COMMunicate:LAN:BSSID?   fw >= 3.5.0, WiFi
    // … grows as commands are added
}

// New: Firmware/DeviceFeatureTable.cs — the swappable data source.
internal sealed record FeatureRequirement(
    DeviceFeature Feature,
    FirmwareVersion MinVersion,
    DeviceType[]? Boards = null);   // null = all boards

// DeviceCapabilities gains the seam (delegating to the table today):
public bool Supports(DeviceFeature feature);

// New: Device/FeatureNotSupportedException.cs
public sealed class FeatureNotSupportedException : Exception
{
    public DeviceFeature Feature { get; }
    public FirmwareVersion? RequiredVersion { get; }
    public string? ActualVersion { get; }
    public DeviceType? Board { get; }
}
```

Call-site pattern (e.g. `GetSdCardStorageAsync`):

```csharp
// up-front gate (no round-trip) — also lets desktop disable the button
if (!Capabilities.Supports(DeviceFeature.SdStorageQuery))
    throw new FeatureNotSupportedException(DeviceFeature.SdStorageQuery, required, FirmwareVersion);

// … issue command … then the wire backstop catches stale-table cases:
if (ResponseHasUndefinedHeader(lines))   // **ERROR: -113
    throw new FeatureNotSupportedException(DeviceFeature.SdStorageQuery, required, FirmwareVersion);
```

`DeviceCapabilities.FromDeviceType(...)` is extended to also take the firmware version (or
gets a back-reference so `Supports` can read both board and version). The existing
`FirmwareVersion` semver/pre-release comparison already handles `3.4.6b1 < 3.4.6 < 3.5.0`
correctly, so the table is a thin layer on top.

## 6. Deliverables / follow-ups

- [ ] `DeviceFeature` enum + `DeviceFeatureTable` + `DeviceCapabilities.Supports(...)`
  (feeds UI gating).
- [ ] `FeatureNotSupportedException` and the command-path backstop that parses the SCPI
  error code and special-cases `-113`; land first on `GetSdCardStorageAsync` (the guard
  deferred from #214).
- [ ] (Later, non-blocking) `CONFigure:CAPabilities:JSON?` reader that overrides the table
  when fw ≥ v3.5.0 — the #327 growth path.

## 7. Out of scope

Implementing the full #327 capability document reader. This RFC covers the investigation,
the strategy decision, and the minimal version-gating layer + typed exception.
