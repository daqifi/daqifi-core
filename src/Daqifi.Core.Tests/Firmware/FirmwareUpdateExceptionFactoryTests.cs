using Daqifi.Core.Firmware;
using Microsoft.Extensions.Logging.Abstractions;

namespace Daqifi.Core.Tests.Firmware;

/// <summary>
/// Direct tests for the two exception-construction helpers on <see cref="FirmwareUpdateContext"/>:
/// <c>CreateFirmwareUpdateException</c>, which every firmware failure in the library funnels
/// through, and <c>FormatExceptionSummary</c>, which renders a caught exception chain into the
/// bootloader-search diagnostic.
/// </summary>
/// <remarks>
/// The flows only ever hand the factory a plain exception on a phase they were already in, so the
/// parts of its contract that are not on that one path have never been exercised: the pass-through
/// of an exception that is already contextualized, the caller-supplied guidance override, and the
/// failure subject that keeps a read-only bootloader probe from reporting a firmware update it
/// never started. The summary formatter is only reached through
/// <c>Pic32BootloaderSession.DescribeBootloaderSearch</c>, whose tests assert that the messages
/// appear somewhere in the text — which says nothing about the type names, the chain order or the
/// separator between links.
/// <para>
/// Expected guidance strings are never written down here: where a test needs one it asks the
/// factory for it, so a mutated guidance table produces a mutated expectation and the assertions
/// cannot quietly agree with it.
/// </para>
/// </remarks>
public class FirmwareUpdateExceptionFactoryTests
{
    /// <summary>The states <c>BuildRecoveryGuidance</c> handles explicitly — every phase a failure can occur in.</summary>
    private static readonly FirmwareUpdateState[] PhaseStates =
    [
        FirmwareUpdateState.PreparingDevice,
        FirmwareUpdateState.WaitingForBootloader,
        FirmwareUpdateState.Connecting,
        FirmwareUpdateState.ErasingFlash,
        FirmwareUpdateState.Programming,
        FirmwareUpdateState.Verifying,
        FirmwareUpdateState.JumpingToApp,
        FirmwareUpdateState.ReconnectingAfterFlash
    ];

    /// <summary>The states that are outcomes rather than phases, and so fall to the catch-all arm.</summary>
    private static readonly FirmwareUpdateState[] OutcomeStates =
    [
        FirmwareUpdateState.Idle,
        FirmwareUpdateState.Complete,
        FirmwareUpdateState.Failed,
        FirmwareUpdateState.CleaningUp,
        FirmwareUpdateState.Recovered
    ];

    [Fact]
    public void CreateFirmwareUpdateException_WrapsAPlainFailureWithThePhaseThatBroke()
    {
        var context = CreateContext();
        var cause = new IOException("HID write failed.");

        var ex = context.CreateFirmwareUpdateException(
            FirmwareUpdateState.Programming, "program flash block 7", cause);

        Assert.Equal(FirmwareUpdateState.Programming, ex.FailedState);
        Assert.Equal("program flash block 7", ex.Operation);
        // The cause must survive by reference: a consumer catching this is expected to
        // unwrap it to decide whether the failure was transport noise or a bad image.
        Assert.Same(cause, ex.InnerException);
        Assert.Equal(
            "Firmware update failed in state 'Programming' while program flash block 7.",
            ex.Message);
    }

    [Fact]
    public void CreateFirmwareUpdateException_WhenTheCauseIsAlreadyContextualized_ReturnsItUntouched()
    {
        // No flash-path operation throws a FirmwareUpdateException today, so this branch is
        // never walked by the flows — yet it is the branch that decides whether a failure
        // raised deeper down keeps its own phase or gets relabelled with the outer catch's.
        var context = CreateContext();
        var original = new FirmwareUpdateException(
            FirmwareUpdateState.Verifying,
            "compare flash CRC",
            "Flash CRC mismatch.",
            "Re-select the firmware package.",
            new InvalidOperationException("CRC 0x1234 != 0x5678."));

        var ex = context.CreateFirmwareUpdateException(
            FirmwareUpdateState.CleaningUp,
            "re-erase flash after failure",
            original,
            recoveryGuidance: "Ignored guidance.",
            failureSubject: "Ignored subject");

        // Same instance, not a copy and not a second wrapper: re-wrapping would bury the
        // original one level deeper on every catch block it passes through.
        Assert.Same(original, ex);
        Assert.Equal(FirmwareUpdateState.Verifying, ex.FailedState);
        Assert.Equal("compare flash CRC", ex.Operation);
        Assert.Equal("Flash CRC mismatch.", ex.Message);
        Assert.Equal("Re-select the firmware package.", ex.RecoveryGuidance);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public void CreateFirmwareUpdateException_UsesTheCallersFailureSubjectWithoutDisturbingAnythingElse()
    {
        // A recovery dialog that probed the bootloader deliberately, instead of starting an
        // update, must not be told "Firmware update failed".
        var context = CreateContext();
        var cause = new TimeoutException("No response.");

        var defaulted = context.CreateFirmwareUpdateException(
            FirmwareUpdateState.Connecting, "connect HID transport", cause);
        var subjected = context.CreateFirmwareUpdateException(
            FirmwareUpdateState.Connecting, "connect HID transport", cause,
            failureSubject: "Bootloader health check");

        Assert.StartsWith("Firmware update failed in state", defaulted.Message, StringComparison.Ordinal);
        Assert.StartsWith("Bootloader health check failed in state", subjected.Message, StringComparison.Ordinal);
        // The subject names the caller's operation, so it may not leak into anything the
        // operator is told to do about the failure, nor into the reported phase.
        Assert.Equal(defaulted.RecoveryGuidance, subjected.RecoveryGuidance);
        Assert.Equal(defaulted.FailedState, subjected.FailedState);
        Assert.Equal(defaulted.Operation, subjected.Operation);
        Assert.DoesNotContain("Bootloader health check", subjected.RecoveryGuidance!, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateFirmwareUpdateException_PrefersTheCallersGuidanceOverTheStateDefault()
    {
        // The PIC32 flow computes guidance from how far cleanup got, which is strictly more
        // specific than what the phase alone can say. That override must win.
        var context = CreateContext();
        var cause = new IOException("Erase rejected.");

        var stateDefault = context.CreateFirmwareUpdateException(
            FirmwareUpdateState.ErasingFlash, "erase flash", cause).RecoveryGuidance;

        var overridden = context.CreateFirmwareUpdateException(
            FirmwareUpdateState.ErasingFlash, "erase flash", cause,
            recoveryGuidance: "Flash was fully re-erased; the device is safe to retry.");

        Assert.Equal(
            "Flash was fully re-erased; the device is safe to retry.",
            overridden.RecoveryGuidance);
        Assert.NotEqual(stateDefault, overridden.RecoveryGuidance);
    }

    [Fact]
    public void CreateFirmwareUpdateException_DerivesDefaultGuidanceFromThePhaseAlone()
    {
        var context = CreateContext();

        string GuidanceFor(FirmwareUpdateState state) =>
            context.CreateFirmwareUpdateException(state, "some operation", new IOException("boom"))
                .RecoveryGuidance!;

        // Every outcome state shares the catch-all arm, so it is the baseline a dropped
        // phase arm would fall back to.
        var fallback = GuidanceFor(FirmwareUpdateState.Idle);
        foreach (var state in OutcomeStates)
        {
            Assert.Equal(fallback, GuidanceFor(state));
        }

        // Each phase must say something the fallback does not, and something no other phase
        // says: guidance shared between two phases would send the operator down the wrong
        // recovery for one of them.
        var seen = new Dictionary<string, FirmwareUpdateState>(StringComparer.Ordinal);
        foreach (var state in PhaseStates)
        {
            var guidance = GuidanceFor(state);
            Assert.NotEqual(fallback, guidance);
            Assert.False(string.IsNullOrWhiteSpace(guidance));
            Assert.False(
                seen.TryGetValue(guidance, out var other),
                $"States '{state}' and '{other}' offer identical recovery guidance.");
            seen[guidance] = state;
        }
    }

    [Fact]
    public void CreateFirmwareUpdateException_KeepsGuidanceIndependentOfTheOperationAndTheCause()
    {
        // Guidance is the phase's advice, not the failure's: two different faults caught in
        // the same phase must tell the operator to do the same thing.
        var context = CreateContext();

        var first = context.CreateFirmwareUpdateException(
            FirmwareUpdateState.WaitingForBootloader,
            "wait for HID bootloader enumeration",
            new TimeoutException("Timed out."));
        var second = context.CreateFirmwareUpdateException(
            FirmwareUpdateState.WaitingForBootloader,
            "poll for the bootloader a second time",
            new UnauthorizedAccessException("Access denied."));

        Assert.Equal(first.RecoveryGuidance, second.RecoveryGuidance);
        // ...while the message still distinguishes them.
        Assert.NotEqual(first.Message, second.Message);
    }

    [Fact]
    public void FormatExceptionSummary_ForASingleFailure_NamesTheTypeAndAddsNoSeparator()
    {
        var summary = FirmwareUpdateContext.FormatExceptionSummary(
            new UnauthorizedAccessException("HID enumeration denied."));

        Assert.Equal("UnauthorizedAccessException: HID enumeration denied.", summary);
    }

    [Fact]
    public void FormatExceptionSummary_ForAChain_ListsOutermostFirstAndPrefixesEveryInnerLink()
    {
        // The two inner links carry the same message on purpose: only the type names
        // distinguish them, so a summary that dropped the types would read as a repeat and
        // the operator would have no way to tell the platform refusal from the driver's.
        var chain = new IOException(
            "Enumeration failed.",
            new UnauthorizedAccessException(
                "Access is denied.",
                new InvalidOperationException("Access is denied.")));

        var summary = FirmwareUpdateContext.FormatExceptionSummary(chain);

        Assert.Equal(
            "IOException: Enumeration failed. | Inner UnauthorizedAccessException: Access is denied. " +
            "| Inner InvalidOperationException: Access is denied.",
            summary);
    }

    [Fact]
    public void FormatExceptionSummary_WhenALinkHasNoMessage_StillNamesItsType()
    {
        // A driver-supplied exception with an empty message is the case where the type name
        // is the only diagnostic left, so it may not be dropped along with the text.
        var summary = FirmwareUpdateContext.FormatExceptionSummary(
            new IOException(string.Empty, new TimeoutException("Timed out.")));

        Assert.Equal("IOException:  | Inner TimeoutException: Timed out.", summary);
    }

    private static FirmwareUpdateContext CreateContext() =>
        new(eventSender: new object(), NullLogger.Instance, new FirmwareUpdateServiceOptions());
}
