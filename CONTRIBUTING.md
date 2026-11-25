# Contributing to DAQiFi Core

Thanks for taking the time to contribute!

## Reporting bugs & requesting features

[Open an issue](https://github.com/daqifi/daqifi-core/issues) with as much detail as you can:
repro steps, expected vs. actual behavior, device model/firmware version, and OS/.NET version.

## Submitting code changes

All code changes go through a pull request:

1. Fork the repo (or branch, if you have write access) — `feature/short-description`,
   `fix/short-description`, `docs/short-description`, or `chore/short-description`.
2. Make your changes and add/update tests.
3. Open a PR against `main` describing the change and linking any related issue.
4. CI must pass and every review conversation must be resolved before the PR can be queued —
   see below for what `main` actually enforces.

The agent rule files (`.cursor/rules/`, `.claude/rules/`) defer to this file for the
contributor process. If one of them disagrees with it, this file is right and the rule
file needs fixing.

### How `main` is gated

`main` is merge-queue gated. Do not push to it. Every change lands through a pull request.

- **Merge queue** — GitHub's merge queue is required. A queued PR is retested on a
  `merge_group` (CI's required `build` check) before it lands.
- **Approvals: zero required; resolved threads: all of them.** This is a solo-maintained
  repo, so the ruleset asks for no approving review — but it does require every review
  conversation to be resolved, so an open comment from a human or a review bot blocks the
  merge until it is marked resolved — replying alone doesn't clear it. Pushing to a
  reviewed branch dismisses stale approvals.
- **Squash only** — merge commits and rebase merges are disabled. The squash commit title
  is the PR title (GitHub appends ` (#N)`); the body is left blank apart from GitHub's
  `Co-authored-by:` trailers.
- **PR titles** — conventional-commit `type(scope): summary`, matching `git log`:
  `fix(sdcard): ...`, `feat(mcp): ...`, `chore(api): ...`. Reserve `type(scope)!: summary`
  for a breaking change. Because the squash uses the PR title, that title is the commit
  message on `main` — write it for someone reading `git log`, not for the queue.

### Code style is enforced by the build, not by review

Two rules are wired in the root `.editorconfig` and made effective by
`EnforceCodeStyleInBuild` in `Directory.Build.props`. Because warnings are errors, a violation
fails the build rather than waiting for someone to notice it in review:

- **IDE0161** — namespaces are file-scoped, everywhere in the repo.
- **CA1707** — no underscores in the names of `Daqifi.Core`'s public members. Test method names
  are unaffected: the rule is scoped to the library project, and only to its public surface.

Both have an IDE code fix on the error. To convert a whole project at once:

```
dotnet format style <project-or-solution> --diagnostics IDE0161
```

Renaming a public member is an API change, so it also needs the `PublicAPI.*.txt` update below —
and, if the old name shipped, an `[Obsolete]` forwarder rather than a removal. `IntelHexParser`'s
two protected-address constants are the worked example.

### Awaits in `Daqifi.Core` must be `ConfigureAwait(false)`

`Daqifi.Core` keeps one pair of synchronous facades that block on its own async work:
`DaqifiDevice.Connect()` and `DaqifiDevice.Disconnect()` (and `Dispose()` through the latter).
Transports, HID and `DaqifiDeviceFactory` are async-only; don't add a sync twin to them. An
`await` that resumes on the caller's `SynchronizationContext` would deadlock a WPF/WinForms app
that blocks on the library from its UI thread, whether through those two or through its own
`.GetAwaiter().GetResult()`. CA2007 is enabled for the library project (see the `[src/Daqifi.Core/**.cs]` section of the root `.editorconfig`)
and warnings are errors, so a naked `await` fails the build. Test projects are exempt.

### Changing the public API means updating `PublicAPI.*.txt`

[ADR 0002](docs/adr/0002-binary-compatibility-policy.md) promises source compatibility for
`Daqifi.Core`'s public API, so the surface is checked in as two files next to
`src/Daqifi.Core/Daqifi.Core.csproj`:

- `PublicAPI.Shipped.txt` - the surface as of the last published release.
- `PublicAPI.Unshipped.txt` - everything added since, and everything removed since as
  `*REMOVED*` lines. It doubles as the release-notes checklist ADR 0002 asks for.

Add, remove or change a public member and the build fails (RS0016/RS0017) until the files
agree with the code again. Put the new entries in `PublicAPI.Unshipped.txt`; your IDE offers
"Add to public API" as a code fix on the error, or run:

```
dotnet format analyzers src/Daqifi.Core/Daqifi.Core.csproj --diagnostics RS0016 --severity warn
```

The resulting diff is the point: a reviewer can see exactly what the change does to the API
without reconstructing it from the code.

To remove a shipped member, leave its line in `PublicAPI.Shipped.txt` alone and add the same line
to `PublicAPI.Unshipped.txt` prefixed with `*REMOVED*`; to change a signature, do that for the old
line and add the new one. A member that never shipped just loses its `Unshipped` line.
`PublicAPI.Shipped.txt` changes only [after a release](#after-a-release).

Two things the tool will not do for you. The protoc-generated `DaqifiOutMessage.cs` is skipped
by the code fix, so entries for it have to be added by hand from the RS0016 message (the symbol
name in the message is already the exact line to add).
And a *removal* is never automatic: the `*REMOVED*` line is the deliberate act of declaring a
breaking change, and ADR 0002 says what that costs.

Until 2.0 ships, a break also needs a row in [docs/migration/2.0.md](docs/migration/2.0.md).
`python3 scripts/check-migration-guide.py` lists what the guide misses and any name in it that no longer
exists; `--compile` also builds its code samples. CI does not run it.

### Regenerating `DaqifiOutMessage`

`src/Daqifi.Core/Communication/Messages/DaqifiOutMessage.cs` is protoc output; never change it by
hand. Its source is `src/Daqifi.Core/Protos/DaqifiOutMessage.proto`, a copy of the firmware
repository's file, whose header names the firmware commit it came from. The firmware owns the wire
format: change it there first, then copy it here, update that commit, and keep the `option` lines.

Use the protoc from the [Google.Protobuf.Tools](https://www.nuget.org/packages/Google.Protobuf.Tools)
NuGet package at the same version as the `Google.Protobuf` reference in `Daqifi.Core.csproj` -
generated code must not be newer than the runtime it links against. From the repository root:

```
protoc -I src/Daqifi.Core --csharp_out=src/Daqifi.Core/Communication/Messages Protos/DaqifiOutMessage.proto
```

The `-I` path matters: the file name protoc is given is embedded in the descriptor.
`DaqifiOutMessageWireFormatTests` checks that the `.proto` and the generated code agree and that no
existing field changed number or type; add a row there for each new field.

### The published package is the second opinion

`PublicAPI.*.txt` lives in the repo, so a PR that removes a public member *and* edits the files
to match compiles clean, whether or not anyone meant to break the API. So
`Daqifi.Core` also sets `EnablePackageValidation` with `PackageValidationBaselineVersion`
pinned to the last version on nuget.org. CI packs the project (`Validate packaged API against
the last published release` in `ci.yml`) and ApiCompat compares the packaged assemblies, for
every target framework, against that published package - which no PR can edit. A member the
baseline shipped and this build does not is `CP0002`.

Two things to know:

- Run it locally with `dotnet pack src/Daqifi.Core/Daqifi.Core.csproj`. **Do not** add
  `--no-build`; it skips the validation targets entirely and the pack passes without checking
  anything.
- The baseline has to move with every stable release (see [below](#after-a-release)). An
  out-of-date baseline is a *narrower* check, not a stricter one: ApiCompat
  can only report a member the baseline package actually contains, so everything added since
  the pinned version falls outside the comparison and could be removed with nothing to report.
  CI fails when the baseline drifts behind nuget.org, so forgetting is loud rather than silent.
  The baseline is always the newest *stable* release: a prerelease can be published, but it is
  not what a consumer restores by default, so CI skips prereleases when deciding what is newest.

An intentional break needs the `*REMOVED*` line in `PublicAPI.Unshipped.txt` *and* an ApiCompat
suppression (`dotnet pack src/Daqifi.Core/Daqifi.Core.csproj -p:ApiCompatGenerateSuppressionFile=true`),
which checks in a `CompatibilitySuppressions.xml` naming exactly what was broken. That file
appearing in a diff is the signal ADR 0002 wants a reviewer to see.

### Releases

A GitHub Release tagged `vX.Y.Z` publishes both packages to NuGet and lists the MCP server in the
MCP Registry. A prerelease tag (`vX.Y.Z-rc.1`) publishes to NuGet only. Either way the packages are
attached to the GitHub Release with a build-provenance attestation. `release.yml` refuses a
release whose prerelease box disagrees with its tag, or whose tag is not on `main`.

If a release run fails part-way, use **Re-run failed jobs**, not **Re-run all jobs**. The packages
are packed once and every later job uses those archives, so a retry pushes and attaches the same
ones. Re-running everything deletes them and packs again. That still pushes a package NuGet does not
have yet, but `release-assets` refuses to attest or attach anything unless it matches what NuGet
published, and leaves the files already on the GitHub Release alone.

The tag alone decides the released version. `VersionPrefix` in `Directory.Build.props` only names
the builds in between: a local or CI build is the next version with `-dev` on the end.

#### After a release

Once a stable version is on nuget.org, CI fails every PR until the API files catch up with it. On
a fresh branch from `main`:

```
git fetch --tags
python3 scripts/post-release.py X.Y.Z
```

It bumps `PackageValidationBaselineVersion`, moves `VersionPrefix` past the release, folds
`PublicAPI.Unshipped.txt` as of the tag into `PublicAPI.Shipped.txt` (applying the `*REMOVED*`
lines; anything merged since the tag stays in `Unshipped`), regenerates or deletes
`CompatibilitySuppressions.xml`, and packs to check the result. Commit what it changed and open a
PR. There is nothing to do after a prerelease, which is never the baseline.

## Security

See [SECURITY.md](SECURITY.md) for how we accept code and how to report a vulnerability.
