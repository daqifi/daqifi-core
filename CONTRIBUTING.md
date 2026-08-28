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
