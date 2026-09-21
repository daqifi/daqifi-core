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
4. CI must pass and every review conversation must be resolved before the PR can be queued —
   see below for what `main` actually enforces.

Agent rule files (`.cursor/rules`, `.claude/rules`) should point here rather than
restating this process.

### How `main` is gated

`main` is merge-queue gated. Do not push to it. Every change lands through a pull request.

- **Merge queue** — GitHub's merge queue is required. A queued PR is retested on a
  `merge_group` (CI's required `build` check) before it lands.
- **Approvals: zero required; resolved threads: all of them.** This is a solo-maintained
  repo, so the ruleset asks for no approving review — but it does require every review
  conversation to be resolved, so an open comment from a human or a review bot blocks the
  merge until someone answers or resolves it. Pushing to a reviewed branch dismisses stale
  approvals.
- **Squash only** — merge commits and rebase merges are disabled. The squash commit title
  is the PR title; the squash body is left blank.
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
  The baseline is always the newest *stable* release: a prerelease can be published, but it is
  not what a consumer restores by default, so CI skips prereleases when deciding what is newest.

An intentional break needs the entry removed from `PublicAPI.Shipped.txt` *and* an ApiCompat
suppression (`dotnet pack src/Daqifi.Core/Daqifi.Core.csproj -p:ApiCompatGenerateSuppressionFile=true`),
which checks in a `CompatibilitySuppressions.xml` naming exactly what was broken. That file
appearing in a diff is the signal ADR 0002 wants a reviewer to see.

## Security

See [SECURITY.md](SECURITY.md) for how we accept code and how to report a vulnerability.
