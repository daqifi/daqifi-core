# ADR 0003: One package, never strong-named, net10.0 only, AOT-compatible

- **Status:** Accepted (2026-11-24)
- **Issue:** [#1053](https://github.com/daqifi/daqifi-core/issues/1053)
- **Follow-ups:** SBOM (deferred, see below)
- **Supersedes:** —

## Context

2.0 is the last cheap moment for a few packaging choices: each is breaking, or at least awkward,
to change afterwards. None of them had been written down.

## Decision

### One package

`Daqifi.Core` stays one package. Firmware update and HID are not split out.

Moving public types into a second package later is source-breaking for anyone who does not add the
new reference, and binary-breaking unless the old assembly forwards the types, so it waits for
another major. It would be worth that only if the mobile heads of daqifi-avalonia need the firmware
surface gone from their app, and nobody has shown that they do.

The dependencies are not a reason to split. `HidSharp` is used by one file and `System.Management`
by the Windows discovery files, and neither appears in a public signature, so either can be replaced
in a minor release. What staying whole costs is size: every consumer carries both, and they are part
of the roughly 10 MB `Daqifi.Mcp` tool.

### Never strong-named

Neither assembly is strong-named, and neither will be. Strong names matter to .NET Framework, which
this library does not support; on .NET 10 the runtime does not validate the signature. Adding one later
would change the assembly's identity, which .NET's library guidance counts as a breaking change, so
this is "never" rather than "not yet".

### net10.0 only

The library targets `net10.0` (an LTS release) and nothing else, since #988. That rules out netstandard
hosts (.NET Framework, Unity, Windows PowerShell). Adding a target framework is a minor release.
Dropping one is a major release. `net10.0` assets also run on later .NET versions, so a new .NET
release does not by itself require either.

### AOT- and trim-compatible

`Daqifi.Core.csproj` sets `IsAotCompatible`. The trim, single-file and AOT analyzers run in every
build, and their warnings are errors. It also marks the assembly trimmable, so apps that trim only
marked assemblies (the default on iOS and Android) now trim this one.

That covers this project's own code only. Checked on 2026-11-24:

| What | Result |
|---|---|
| Build with the analyzers on | 0 warnings |
| NativeAOT publish for osx-arm64, all of `Daqifi.Core` rooted | No warning in `Daqifi.Core`; a protobuf round trip runs. One warning in HidSharp (IL2075, its Linux `uname` lookup) |
| Trimmed publish for win-x64, all of `Daqifi.Core` rooted | The same HidSharp warning, and two in `System.Management` (IL2067, IL2077). Built, not run |
| `VerifyReferenceTrimCompatibility` / `VerifyReferenceAotCompatibility` | Not marked trimmable: HidSharp, System.CodeDom (via `System.Management`). Not marked AOT-compatible: HidSharp, Google.Protobuf |

So a trimmed or AOT app can stream from a device over WiFi or serial without warnings from this
library, but two paths rest on dependencies that make no such promise:

- **HidSharp** (net35 / netstandard2.0, no trim metadata) backs HID on Windows and Linux, which
  means firmware update there. macOS uses the in-tree IOKit backend.
- **System.Management** queries WMI through built-in COM, which trimmed apps turn off by default and
  NativeAOT does not have. Serial discovery treats a failed query as "unknown port" and probes it,
  so discovery should be slower there rather than broken. Neither path was run on Windows.

### Release artifacts

The release workflow attaches both `.nupkg` files to the GitHub Release with a build-provenance
attestation (`actions/attest`).

An **SBOM is deferred**. It needs a generator tool to choose, pin and keep current, and nobody has
asked for one; the nuspec already lists the dependencies.

## Alternatives considered

| Option | Why not |
|---|---|
| **Split out `Daqifi.Core.Firmware` now** | Two packages to version together and a second reference for every app that updates firmware, to save two dependencies no consumer has complained about. |
| **Strong-name now, in case** | Permanent key handling for a check .NET 10 does not make. |
| **Also target `netstandard2.0`** | Reopens the conditional code #988 removed, for hosts no known consumer uses. |
| **Leave AOT undeclared** | The build is already clean, and without the analyzers it would stop being clean without anyone noticing. |

## Consequences

- A change that needs reflection or runtime code generation must be annotated or written another way,
  or the build fails.
- A consumer that trims and reflects over `Daqifi.Core` types has to root what it reflects on. That
  includes XAML reflection bindings, which daqifi-avalonia has left only in views it has not yet
  moved to compiled bindings.
- Replacing HidSharp or System.Management stays possible in a minor release for as long as they stay
  out of public signatures.
- The attestation covers the file on the GitHub Release. nuget.org adds its own signature to what it
  serves, so the package downloaded from there has a different hash.
