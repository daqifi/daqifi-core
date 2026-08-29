# Contributing to DAQiFi Core

Thanks for taking the time to contribute!

## Reporting bugs & requesting features

[Open an issue](https://github.com/daqifi/daqifi-core/issues) with as much detail as you can:
repro steps, expected vs. actual behavior, device model/firmware version, and OS/.NET version.

## Submitting code changes

All code changes go through a pull request:

1. Fork the repo (or branch, if you have write access) — `feature/short-description`,
   `fix/short-description`, or `docs/short-description`.
2. Make your changes and add/update tests.
3. Open a PR against `main` describing the change and linking any related issue.
4. CI must pass and the PR needs review before merge.

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

`Daqifi.Core` ships synchronous facades (`DaqifiDevice.Connect()`,
`DaqifiDeviceFactory.ConnectTcp`/`ConnectSerial`, `IStreamTransport.Connect`/`Disconnect`)
that block on their own async work. An `await` that resumes on the caller's
`SynchronizationContext` therefore deadlocks a WPF/WinForms app calling from the UI
thread. CA2007 is enabled for the library project (see the `[src/Daqifi.Core/**.cs]` section of the root `.editorconfig`)
and warnings are errors, so a naked `await` fails the build. Test projects are exempt.

### Changing the public API means updating `PublicAPI.*.txt`

[ADR 0002](docs/adr/0002-binary-compatibility-policy.md) promises source compatibility for
`Daqifi.Core`'s public API, so the surface is checked in as two files next to
`src/Daqifi.Core/Daqifi.Core.csproj`:

- `PublicAPI.Shipped.txt` - the surface as of the last published release.
- `PublicAPI.Unshipped.txt` - everything added since. It doubles as the release-notes
  checklist ADR 0002 asks for.

Add, remove or change a public member and the build fails (RS0016/RS0017) until the files
agree with the code again. Put the new entries in `PublicAPI.Unshipped.txt`; your IDE offers
"Add to public API" as a code fix on the error, or run:

```
dotnet format analyzers src/Daqifi.Core/Daqifi.Core.csproj --diagnostics RS0016 --severity warn
```

The resulting diff is the point: a reviewer can see exactly what the change does to the API
without reconstructing it from the code.

When a release goes out, the `Unshipped` entries move into `Shipped` and `Unshipped` is emptied.

Two things the tool will not do for you. The protoc-generated `DaqifiOutMessage.cs` is skipped
by the code fix, so entries for it have to be added by hand from the RS0016 message (the symbol
name in the message is already the exact line to add - those types sit in the global namespace).
And a *removal* is never automatic: deleting the entry is the deliberate act of declaring a
breaking change, and ADR 0002 says what that costs.

### The published package is the second opinion

`PublicAPI.*.txt` lives in the repo, so a PR that removes a public member *and* its entry
compiles clean - the deliberate act above looks identical to an accidental one. So
`Daqifi.Core` also sets `EnablePackageValidation` with `PackageValidationBaselineVersion`
pinned to the last version on nuget.org. CI packs the project (`Validate packaged API against
the last published release` in `ci.yml`) and ApiCompat compares the packaged assemblies, for
both target frameworks, against that published package - which no PR can edit. A member the
baseline shipped and this build does not is `CP0002`.

Two things to know:

- Run it locally with `dotnet pack src/Daqifi.Core/Daqifi.Core.csproj`. **Do not** add
  `--no-build`; it skips the validation targets entirely and the pack passes without checking
  anything.
- After a release, bump `PackageValidationBaselineVersion` to the version just published, in
  the same change that moves `Unshipped` entries into `Shipped`. This is required, not
  housekeeping. An out-of-date baseline is a *narrower* check, not a stricter one: ApiCompat
  can only report a member the baseline package actually contains, so everything added since
  the pinned version falls outside the comparison and could be removed with nothing to report.
  CI fails when the baseline drifts behind nuget.org, so forgetting is loud rather than silent.

An intentional break needs the entry removed from `PublicAPI.Shipped.txt` *and* an ApiCompat
suppression (`dotnet pack src/Daqifi.Core/Daqifi.Core.csproj -p:ApiCompatGenerateSuppressionFile=true`),
which checks in a `CompatibilitySuppressions.xml` naming exactly what was broken. That file
appearing in a diff is the signal ADR 0002 wants a reviewer to see.

## Security: how we do and don't accept code

**We only ever accept code changes as pull requests against this repository.** A PR gives
reviewers a real diff, runs CI against the change, and ties it to an accountable GitHub identity.

We do **not** accept patches, "fixes," or libraries attached as `.zip`/binary files in issue or
PR comments — regardless of how convincing or on-topic the surrounding message is. If you see a
comment offering a downloadable file as a fix, please don't run or extract it, and flag it to a
maintainer (or use GitHub's "Report content" option on the comment) so it can be reviewed and
removed.

If you've found a genuine security vulnerability, please report it privately to the maintainers
via [daqifi.com](https://daqifi.com) rather than filing a public issue.
