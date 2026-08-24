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
