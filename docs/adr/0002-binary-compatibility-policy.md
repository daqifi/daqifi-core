# ADR 0002: Semver here means source compatibility, not binary compatibility

- **Status:** Accepted (2026-08-22)
- **Issue:** [#557](https://github.com/daqifi/daqifi-core/issues/557)
- **Follow-ups:** —
- **Supersedes:** —

## Context

Raised and confirmed by the adversarial audit on [#554](https://github.com/daqifi/daqifi-core/pull/554),
dispositioned `defer_ticket` as a release-process decision rather than a code fix.

`ChannelAcquisitionStatistics` is a positional `record`. #554 appends two parameters —
`string? Unit = null` and `long ValueSampleCount = 0` — taking it from 14 to 16.

Optional defaults preserve **source** compatibility: anyone who rebuilds against the new
package is fine. They do not preserve **binary** compatibility. C# emits exactly one
constructor and one `Deconstruct`, at the new arity, and the defaults are resolved at the
*caller's* compile time. A consumer assembly compiled against the 14-parameter shape — the
one that shipped in v1.6.0 — and then run against the new `Daqifi.Core.dll` without
recompiling throws:

```
System.MissingMethodException: Method not found: 'Void ChannelAcquisitionStatistics..ctor(...)'
```

The audit reproduced this empirically with a minimal record, not just from reasoning.

**Scope of the break — narrower than it sounds.** Only positional construction and
full-arity `Deconstruct` break. Property reads, `with` expressions, `Equals`, `GetHashCode`,
and `ToString` are all unaffected. `ChannelAcquisitionStatistics` is a read-only snapshot
produced by `AcquisitionStatistics.Snapshot()`, and consumers overwhelmingly read it rather
than construct it. It also fails loud — a hard exception at the call site, immediately —
rather than silently computing a wrong-but-plausible number. And it only manifests under
drop-in-DLL or diamond-dependency deployment; the normal NuGet-restore flow recompiles the
consumer, where the optional defaults do their job.

The same shape applies to any future positional-record field addition in the public API, not
just this one — diagnostic-snapshot types are the ones most likely to keep gaining fields as
firmware reports more (e.g. #536, adding eight accessors to a sibling type). That recurrence
is why this needed a standing policy rather than a per-addition judgment call.

## Decision

Appending a parameter (with a default) to a public positional record is **not** treated as a
breaking change requiring a major version bump. It is called out in the release notes for
the version that ships it, so a drop-in-DLL consumer has a documented reason to recompile.

The library does not add a compatibility shim (e.g. an `[EditorBrowsable(Never)]` overload at
the old arity forwarding to the new constructor) to preserve the old binary signature. A shim
per addition does not scale against types designed to keep growing fields, and would become
permanent surface area on types that exist specifically to absorb firmware growth.

Semver as practiced by this library (`README.md`, "For maintainers") therefore tracks source
compatibility for its public API, not binary compatibility. Consumers who need binary
compatibility across versions should recompile against each release rather than swap the DLL
in place.

## Alternatives considered

| Option | Why not |
|---|---|
| **Add a compatibility shim per breaking append** (old-arity `[EditorBrowsable(Never)]` overload forwarding to the new ctor) | Preserves the old binary signature, but costs a permanent shim on a type that will keep growing fields — the maintenance burden compounds with every future addition instead of being paid once. |
| **Bump major version on every appended record field** | Honest, but forces consumers through a major-version migration for a change that is source-compatible for the overwhelming majority of call sites (property reads, `with`, snapshot consumption) and only breaks the narrow drop-in-DLL deployment path. |
| **Say nothing and let it surface as a support question each time** | The cheapest in the moment, but re-litigates the same reasoning on every future record-field addition instead of settling it once. |

## Consequences

**Positive**
- No permanent compatibility shims accumulate on types designed to grow (diagnostic
  snapshots, capability documents).
- The next contributor who appends a record field does not have to re-derive this reasoning —
  they call it out in release notes and move on.
- Documented in `README.md` ("For maintainers") so the policy is visible to anyone integrating
  against this library, not just to whoever reads this ADR.

**Negative / costs**
- A consumer running a drop-in-DLL or diamond-dependency deployment can still hit a
  `MissingMethodException` after a release that appends a record field, if they don't read
  release notes. Mitigated, not eliminated, by calling it out explicitly per release.
- This is an explicit statement that binary compatibility is out of scope for this library's
  guarantee — acceptable today because DAQiFi's own consumers are the only known integrators,
  but worth revisiting if that changes.

## Out of scope

This ADR does not cover breaking changes to non-record public types, method signature changes,
or removals — those remain ordinary breaking changes requiring a major version bump under
normal semver. It covers only the append-a-parameter-to-a-positional-record shape.
