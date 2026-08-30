using System;
using Daqifi.Core.Firmware;
using Xunit;

namespace Daqifi.Core.Tests.Firmware;

/// <summary>
/// Direct tests for <see cref="FirmwareUpdateServiceOptions.GetStateTimeout"/>, the shipped public
/// method that decides how long each phase of a firmware update is allowed to take.
/// </summary>
/// <remarks>
/// <para>
/// It had no tests of its own. Three production sites read it —
/// <c>FirmwareUpdateContext</c> when entering and while waiting out a state,
/// <c>WifiModuleUpdater</c> for the post-flash serial reconnect, and <c>Pic32FirmwareUpdater</c>
/// for the recovery re-erase — and every existing firmware test reaches it only as a side effect
/// of running an update that is meant to finish well inside whatever budget it returned. So a
/// wrong answer is invisible: the deadline is simply never the thing that fires.
/// </para>
/// <para>
/// What makes it worth pinning is that the switch is the only place a consumer's tuning is
/// connected to a phase. Setting <see cref="FirmwareUpdateServiceOptions.ProgrammingTimeout"/> is
/// the documented way to give a slow programmer more room; if that arm pointed at another
/// property the setter would appear to work and the update would still be cut off at the old
/// budget.
/// </para>
/// <para>
/// The fixture sets a distinct value on every timeout property for exactly that reason. Four of
/// the shipped defaults are 45 seconds and two more are 20, so under default options six of the
/// nine mappings could be swapped with each other and every assertion would still pass. Distinct
/// values are what make a transposition observable.
/// </para>
/// </remarks>
public class FirmwareUpdateStateTimeoutTests
{
    private static readonly TimeSpan Preparing = TimeSpan.FromSeconds(11);
    private static readonly TimeSpan WaitingForBootloader = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan Connecting = TimeSpan.FromSeconds(13);
    private static readonly TimeSpan Erasing = TimeSpan.FromSeconds(14);
    private static readonly TimeSpan Programming = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Verifying = TimeSpan.FromSeconds(16);
    private static readonly TimeSpan JumpingToApp = TimeSpan.FromSeconds(17);

    /// <summary>
    /// The budget a state with no configured timeout of its own falls back to.
    /// </summary>
    private static readonly TimeSpan Fallback = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Options whose every routed timeout differs from every other, so that a mapping pointing at
    /// the wrong property cannot produce the expected number by coincidence.
    /// </summary>
    private static FirmwareUpdateServiceOptions Tuned() => new()
    {
        PreparingDeviceTimeout = Preparing,
        WaitingForBootloaderTimeout = WaitingForBootloader,
        ConnectingTimeout = Connecting,
        ErasingFlashTimeout = Erasing,
        ProgrammingTimeout = Programming,
        VerifyingTimeout = Verifying,
        JumpingToApplicationTimeout = JumpingToApp,

        // Not routed to any state. Present so that a mapping which reached for it instead of the
        // property it names would return a value no case below expects.
        BootloaderResponseTimeout = TimeSpan.FromSeconds(18),
    };

    public static TheoryData<FirmwareUpdateState, TimeSpan> RoutedStates() => new()
    {
        { FirmwareUpdateState.PreparingDevice, Preparing },
        { FirmwareUpdateState.WaitingForBootloader, WaitingForBootloader },
        { FirmwareUpdateState.Connecting, Connecting },
        { FirmwareUpdateState.ErasingFlash, Erasing },
        { FirmwareUpdateState.Programming, Programming },
        { FirmwareUpdateState.Verifying, Verifying },
        { FirmwareUpdateState.JumpingToApp, JumpingToApp },
    };

    /// <summary>
    /// Every phase with a timeout property of its own is answered from that property, and from no
    /// other.
    /// </summary>
    [Theory]
    [MemberData(nameof(RoutedStates))]
    public void GetStateTimeout_AnswersEachPhaseFromTheOptionItNames(
        FirmwareUpdateState state,
        TimeSpan expected)
    {
        Assert.Equal(expected, Tuned().GetStateTimeout(state));
    }

    /// <summary>
    /// The post-WiFi-flash reconnect deliberately shares the verify budget rather than having one
    /// of its own, so that a host which widened <c>VerifyingTimeout</c> for a slow re-enumeration
    /// keeps that tuning after the reconnect became a state in its own right.
    /// </summary>
    [Fact]
    public void GetStateTimeout_ReconnectingAfterFlash_SharesTheVerifyBudget()
    {
        var options = Tuned();

        Assert.Equal(options.VerifyingTimeout, options.GetStateTimeout(FirmwareUpdateState.ReconnectingAfterFlash));
        Assert.Equal(Verifying, options.GetStateTimeout(FirmwareUpdateState.ReconnectingAfterFlash));
    }

    /// <summary>
    /// Recovery re-erases the application flash, so it is bounded by the erase budget rather than
    /// by the fallback — a device left half-flashed has to finish being cleaned up.
    /// </summary>
    [Fact]
    public void GetStateTimeout_CleaningUp_SharesTheEraseBudget()
    {
        var options = Tuned();

        Assert.Equal(options.ErasingFlashTimeout, options.GetStateTimeout(FirmwareUpdateState.CleaningUp));
        Assert.Equal(Erasing, options.GetStateTimeout(FirmwareUpdateState.CleaningUp));
    }

    /// <summary>
    /// The two shared budgets track their source property rather than a value captured when the
    /// options object was built, so tuning the source moves both.
    /// </summary>
    [Fact]
    public void GetStateTimeout_TheSharedBudgets_FollowLaterChangesToTheirSource()
    {
        var options = Tuned();
        options.VerifyingTimeout = TimeSpan.FromSeconds(90);
        options.ErasingFlashTimeout = TimeSpan.FromSeconds(91);

        Assert.Equal(TimeSpan.FromSeconds(90), options.GetStateTimeout(FirmwareUpdateState.ReconnectingAfterFlash));
        Assert.Equal(TimeSpan.FromSeconds(91), options.GetStateTimeout(FirmwareUpdateState.CleaningUp));
    }

    /// <summary>
    /// The lifecycle states that do no waiting get the fallback, and get it even when every
    /// configurable budget has been tuned away from it — none of them may quietly adopt another
    /// phase's budget.
    /// </summary>
    [Theory]
    [InlineData(FirmwareUpdateState.Idle)]
    [InlineData(FirmwareUpdateState.Complete)]
    [InlineData(FirmwareUpdateState.Failed)]
    [InlineData(FirmwareUpdateState.Recovered)]
    public void GetStateTimeout_AStateThatDoesNoWaiting_GetsTheFallback(FirmwareUpdateState state)
    {
        Assert.Equal(Fallback, Tuned().GetStateTimeout(state));
    }

    /// <summary>
    /// A value outside the enum — a state number round-tripped through storage, or one written by
    /// a newer version of the library — is answered with the fallback rather than throwing. The
    /// caller is asking how long to wait, and refusing to say would abort an update over a number
    /// it does not recognise.
    /// </summary>
    [Fact]
    public void GetStateTimeout_AnUndefinedState_GetsTheFallbackRatherThanThrowing()
    {
        Assert.Equal(Fallback, Tuned().GetStateTimeout((FirmwareUpdateState)999));
    }
}
