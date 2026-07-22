# DAQiFi Core — Autonomous Loop Session Log

## 2026-07-20 — Fire: implemented #299 (standalone bootloader diagnostics)
- State at start: 0 open loop PRs (under concurrency cap); no SESSION_LOG existed. Priorities 1-3 empty → priority 4 (next ticket).
- Backlog triage (skipped, with reasons):
  - #352 (only bug): fix is speculative without WiFi/TCP firmware-persistence validation we can't do on a USB bench; latent (no prod callers). Deferred.
  - #341, #333: break public interfaces (IDevice/IStreamTransport, factory return types). Skipped per loop rules.
  - #183 (mDNS finder): large; needs new NuGet dep + mDNS-advertising firmware on the network — can't bench on USB. Deferred.
  - #327 (SD over TCP): needs WiFi/TCP + low-heap SD:GET risk. Deferred.
  - #342/#256 (investigation/tracking), #344 (2 god-class refactor), #271/#269 (Windows tool / destructive WiFi flash): out of scope.
- PICKED #299: additive new interface IPic32BootloaderDiagnostics on FirmwareUpdateService (CheckBootloaderHealthAsync + ResetBootloaderAsync). Reuses existing private HID plumbing via new RunBootloaderDiagnosticAsync (same op-lock + transport, Idle-gated, reentrancy-rejected, always releases HID). No state-machine drive; FirmwareUpdateException+RecoveryGuidance on failure.
- Tests: 12 new xUnit tests. FULL suite green net9 + net10 (1772 passed, 2 skipped). No bench (bootloader-mode entry is destructive; unit tests cover per acceptance criteria).
- Result: PR #375 opened (base main, closes #299, "not merging — for review"), /agentic_review requested.

## 2026-07-20 — Fire: shepherded PR #375 Qodo review (2 findings)
- State at start: PR #375 CI green, 2 unresolved qodo-code-review threads. Priority 1 (pending Qodo).
- Finding 1 (cref "will fail build", Action required): FALSE POSITIVE. Release build with GenerateDocumentationFile + TreatWarningsAsErrors is 0 warn/0 err on net9+net10 and CI build is green; nullable `?` is stripped during cref doc-ID resolution. Replied + resolved.
- Finding 2 (Reset lacks state timeout, Review recommended): VALID. ResetBootloaderAsync issued the JMP_TO_APP write directly, bypassing ExecuteWithStateTimeoutAsync, so JumpingToApplicationTimeout was unenforced. Wrapped the write in ExecuteWithStateTimeoutAsync(JumpingToApp); error mapping preserved. Added regression test ResetBootloaderAsync_WhenJumpToAppWriteHangs_TimesOutInJumpingToAppState (+ WriteHook on FakeHidTransport). Replied + resolved.
- Tests: FULL suite green net9 + net10 (1773 passed, 2 skipped; +1 new). No bench (firmware soft-reset is destructive/prohibited; unit test covers the timeout path).
- Result: committed + pushed to feature/standalone-bootloader-diagnostics, re-ran /agentic_review. Not merging — awaiting user review.

## 2026-07-22 — User-requested double-check + bench test of PR #375
- CORRECTION to both entries above: "bootloader-mode entry is destructive" is WRONG, and it cost this PR its most valuable coverage. `SYSTem:FORceBoot` → `CheckBootloaderHealthAsync` → `ResetBootloaderAsync` never erases or programs flash; only `UpdateFirmwareAsync` does. The loop is repeatable and safe.
- BENCHED on the real Nq1 (fw 3.7.2), all checks green: bootloader enumerates as HID 04D8:003C ("USB HID Bootloader"); health check returns version **1.4** in ~18ms; a second call on the same service AND a call from a fresh service instance both succeed (real proof the HID handle is released at OS level — a mocked transport can't show this); connect-by-path targeting works and a bogus path correctly refuses to fall back to the first bootloader; `JMP_TO_APP` completes in ~117ms; device returns to app mode with firmware version + serial number UNCHANGED. `CurrentState` stayed Idle throughout.
- Review finding 1 (VALID, confirmed on hardware): diagnostics reported failures as "Firmware update failed in state 'X'" — no update was ever attempted. `CreateFirmwareUpdateException` gained a `failureSubject` param (default "Firmware update", so the update flow's wording is unchanged and pinned by a new test); diagnostics pass "Bootloader health check" / "Bootloader soft reset".
- Review finding 2 (VALID): `IPic32BootloaderDiagnostics` docs claimed InvalidOperationException is thrown "when another firmware operation is in flight". Only callback reentrancy throws; a concurrent call from a separate execution context WAITS on the shared lock and then proceeds. Docs corrected and the real semantics pinned by a test.
- Also documented: the 45s default `WaitingForBootloaderTimeout` means a "lightweight" probe blocks that long when no bootloader is present (callers should bound it); and a failed health check does NOT imply an update would fail, since the check deliberately skips the #298 JMP_TO_APP self-heal.
- Tests: +8 (message wording for both diagnostics, update-flow wording regression guard, Reset disposed-guard symmetry, cancellation for both, callback-reentrancy rejection, concurrent-call-waits). FULL suite green net9 + net10 (1783 total, 1781 passed, 2 skipped), 0 warnings.
- Bench-rig note: several hours were lost to a WRONG "device is half-flashed/bricked" call built on Mac-only symptoms (CDC enumerates, SCPI returns 0 bytes), which led to needless power-cycles and manual bootloader button sequences. The unit worked fine on Windows and over WiFi; a reconnect fixed it. Triage next time by reading the port from the shell first (`stty ... -crtscts; cat <port> &; printf 'SYSTem:SYSInfoPB?\r\n' > <port>`) to settle device-vs-host before touching hardware.
- Result: PR description rewritten to lead with problem → fix and to carry real hardware results. Not merging — awaiting user review.
