using Daqifi.Core.Firmware;
using Microsoft.Extensions.Logging.Abstractions;

namespace Daqifi.Core.Tests.Firmware;

/// <summary>
/// Direct tests for the firmware-update state machine inside <c>FirmwareUpdateContext</c> —
/// the legal-transition table, the self-transition shortcut, the rejection path and
/// <c>ResetIfTerminalState</c>.
/// </summary>
/// <remarks>
/// The machine is reached today only along the two happy flows (PIC32 and WiFi), which walk it
/// forwards through legal transitions and never ask it to say no. That leaves the whole denial
/// half of the contract unpinned: an extra edge added to the table — letting Programming report
/// Complete without verifying, or letting Failed resume mid-flow instead of restarting — breaks
/// nothing any existing test observes, because no test ever attempts a transition the flows do
/// not already make. <c>ResetIfTerminalState</c> has the same shape: the flows only call it when
/// the machine is already terminal, so its guard could vanish and every suite would stay green.
/// <para>
/// These tests therefore assert properties of the machine (every declared state is reachable;
/// every phase can report failure; the machine can always get back to Idle) — over the declared
/// enum, so a state cannot escape a property by falling out of the discovered graph — and the
/// specific edges that must NOT exist, rather than
/// restating the table row by row. The graph is discovered by probing the machine itself rather
/// than read out of the production dictionary, so a test cannot agree with a mutated table.
/// </para>
/// </remarks>
public class FirmwareUpdateStateMachineTests
{
    private static readonly FirmwareUpdateState[] AllStates =
        Enum.GetValues<FirmwareUpdateState>();

    private static readonly FirmwareUpdateState[] TerminalStates =
    [
        FirmwareUpdateState.Complete,
        FirmwareUpdateState.Failed,
        FirmwareUpdateState.Recovered
    ];

    [Fact]
    public void EveryDeclaredState_IsReachableFromIdle()
    {
        // The two properties below walk every declared state, and both need a route to each one
        // in order to get a context there. A state that lost all its inbound edges is also dead
        // production code — no flow could ever report it — so it has to fail here loudly rather
        // than quietly drop out of the discovered graph and take its own coverage with it.
        var paths = DiscoverShortestPaths();

        Assert.Empty(AllStates.Except(paths.Keys));
    }

    [Fact]
    public void EveryPhaseOfAnUpdate_CanStillReportFailure()
    {
        // A consumer only ever learns an update died through the machine reaching Failed. If any
        // phase lost its Failed edge, a failure there would throw an invalid-transition
        // InvalidOperationException out of the flow instead — surfacing as an unrelated internal
        // error rather than a FirmwareUpdateException carrying recovery guidance.
        var paths = DiscoverShortestPaths();

        var cannotFail = AllStates
            .Where(state => !TerminalStates.Contains(state))
            .Where(state => !CanTransition(PathTo(paths, state), FirmwareUpdateState.Failed))
            .ToList();

        Assert.Empty(cannotFail);
    }

    [Fact]
    public void FromEveryState_TheMachineCanGetBackToIdle()
    {
        // FirmwareUpdateService is a reusable object: a second UpdateFirmwareAsync on the same
        // instance starts from Idle. A state with no route home is a permanently wedged service
        // that only a restart clears, and no flow test would notice because each one runs a
        // single update on a fresh service.
        var paths = DiscoverShortestPaths();
        var edges = DiscoverEdges(paths);

        var stranded = AllStates
            .Where(state => !CanReach(edges, state, FirmwareUpdateState.Idle))
            .ToList();

        Assert.Empty(stranded);
    }

    [Theory]
    // Cannot claim success before the image is written and checked.
    [InlineData(FirmwareUpdateState.Programming, FirmwareUpdateState.Complete)]
    [InlineData(FirmwareUpdateState.ErasingFlash, FirmwareUpdateState.Complete)]
    // Cannot verify an image that was never programmed.
    [InlineData(FirmwareUpdateState.ErasingFlash, FirmwareUpdateState.Verifying)]
    // Cannot write to flash that was never erased, or connect to a bootloader never found.
    [InlineData(FirmwareUpdateState.Connecting, FirmwareUpdateState.Programming)]
    [InlineData(FirmwareUpdateState.WaitingForBootloader, FirmwareUpdateState.ErasingFlash)]
    // The WiFi flow's terminal leg has no half-flashed PIC32 application to re-erase, so the
    // cleanup re-erase must be unreachable from it.
    [InlineData(FirmwareUpdateState.ReconnectingAfterFlash, FirmwareUpdateState.CleaningUp)]
    public void AnEdgeThatWouldSkipAFlashStep_IsRejected(
        FirmwareUpdateState from,
        FirmwareUpdateState to)
    {
        var paths = DiscoverShortestPaths();

        Assert.False(
            CanTransition(PathTo(paths, from), to),
            $"{from} -> {to} must not be a legal transition.");
    }

    [Theory]
    [InlineData(FirmwareUpdateState.Complete)]
    [InlineData(FirmwareUpdateState.Failed)]
    [InlineData(FirmwareUpdateState.Recovered)]
    public void AnOutcomeState_LeadsNowhereButBackToIdle(FirmwareUpdateState terminal)
    {
        // Resuming mid-flow after an outcome has been reported would leave the device in a state
        // nobody re-established: a retry has to start over from Idle, not pick up where it left
        // off. Idle is the only edge each outcome may have.
        var paths = DiscoverShortestPaths();

        var reachable = AllStates
            .Where(target => target != terminal)
            .Where(target => CanTransition(PathTo(paths, terminal), target))
            .ToList();

        Assert.Equal(new[] { FirmwareUpdateState.Idle }, reachable);
    }

    [Fact]
    public void RepeatingTheCurrentState_RenamesTheOperationWithoutAnnouncingAChange()
    {
        // Phases re-announce themselves as their operation text advances (e.g. per retry attempt).
        // That must move the description without telling subscribers the state changed, or a
        // consumer's transition log fills with self-loops.
        var context = ContextAt(FirmwareUpdateState.Programming);
        var events = Subscribe(context);

        context.TransitionToState(FirmwareUpdateState.Programming, "Writing block 7 of 12");

        Assert.Empty(events);
        Assert.Equal(FirmwareUpdateState.Programming, context.CurrentState);
        Assert.Equal("Writing block 7 of 12", context.CurrentOperation);
    }

    [Fact]
    public void ARejectedTransition_NamesBothStatesAndLeavesTheMachineUntouched()
    {
        var context = ContextAt(FirmwareUpdateState.Connecting);
        context.TransitionToState(FirmwareUpdateState.Connecting, "Opening the bootloader HID link");
        var events = Subscribe(context);

        var ex = Assert.Throws<InvalidOperationException>(
            () => context.TransitionToState(FirmwareUpdateState.Complete, "Finishing early"));

        Assert.Contains("Connecting", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Complete", ex.Message, StringComparison.Ordinal);

        // A half-applied rejection is worse than the rejection: the caller catches the exception
        // and the machine is somewhere neither side expects.
        Assert.Empty(events);
        Assert.Equal(FirmwareUpdateState.Connecting, context.CurrentState);
        Assert.Equal("Opening the bootloader HID link", context.CurrentOperation);
    }

    [Fact]
    public void AnAcceptedTransition_TellsSubscribersWhereTheUpdateCameFromAndWentTo()
    {
        // PreviousState is public API and is what a consumer renders a progress trail from;
        // nothing else asserts it, so the two states could be reported the wrong way round.
        var context = ContextAt(FirmwareUpdateState.WaitingForBootloader);
        var events = Subscribe(context);

        context.TransitionToState(FirmwareUpdateState.Connecting, "Connecting to the bootloader");

        var raised = Assert.Single(events);
        Assert.Equal(FirmwareUpdateState.WaitingForBootloader, raised.PreviousState);
        Assert.Equal(FirmwareUpdateState.Connecting, raised.CurrentState);
        Assert.Equal("Connecting to the bootloader", raised.Operation);
    }

    [Theory]
    [InlineData(FirmwareUpdateState.Complete)]
    [InlineData(FirmwareUpdateState.Failed)]
    [InlineData(FirmwareUpdateState.Recovered)]
    public void ResetIfTerminalState_ReturnsAFinishedUpdateToIdle(FirmwareUpdateState terminal)
    {
        var context = ContextAt(terminal);
        var events = Subscribe(context);

        context.ResetIfTerminalState();

        Assert.Equal(FirmwareUpdateState.Idle, context.CurrentState);
        var raised = Assert.Single(events);
        Assert.Equal(terminal, raised.PreviousState);
        Assert.Equal(FirmwareUpdateState.Idle, raised.CurrentState);
    }

    [Theory]
    [InlineData(FirmwareUpdateState.PreparingDevice)]
    [InlineData(FirmwareUpdateState.WaitingForBootloader)]
    [InlineData(FirmwareUpdateState.Connecting)]
    [InlineData(FirmwareUpdateState.ErasingFlash)]
    [InlineData(FirmwareUpdateState.Programming)]
    [InlineData(FirmwareUpdateState.Verifying)]
    [InlineData(FirmwareUpdateState.JumpingToApp)]
    [InlineData(FirmwareUpdateState.ReconnectingAfterFlash)]
    [InlineData(FirmwareUpdateState.CleaningUp)]
    public void ResetIfTerminalState_LeavesAnUpdateThatIsStillRunningAlone(
        FirmwareUpdateState inFlight)
    {
        // The service calls this before it starts work. If the terminal check went away it would
        // silently rewind an in-flight update to Idle — and every flow test would stay green,
        // because they only ever reach the call with the machine already terminal.
        var context = ContextAt(inFlight);
        context.TransitionToState(inFlight, "Mid-flight operation");
        var events = Subscribe(context);

        context.ResetIfTerminalState();

        Assert.Empty(events);
        Assert.Equal(inFlight, context.CurrentState);
        Assert.Equal("Mid-flight operation", context.CurrentOperation);
    }

    // ---- machine probing -----------------------------------------------------
    //
    // The graph below is derived by asking a real context whether it accepts a transition, never
    // by reading the production table. A mutated table therefore produces a mutated graph and the
    // property assertions above have something to catch.

    private static FirmwareUpdateContext CreateContext() => new(
        eventSender: new object(),
        NullLogger.Instance,
        new FirmwareUpdateServiceOptions());

    private static FirmwareUpdateContext ContextAlong(IReadOnlyList<FirmwareUpdateState> path)
    {
        var context = CreateContext();
        foreach (var state in path)
        {
            context.TransitionToState(state, $"Walking to {state}");
        }

        return context;
    }

    /// <summary>
    /// The route the machine accepts to <paramref name="state"/>, failing loudly rather than
    /// throwing a bare KeyNotFoundException if the state turned out to be unreachable.
    /// </summary>
    private static IReadOnlyList<FirmwareUpdateState> PathTo(
        IReadOnlyDictionary<FirmwareUpdateState, IReadOnlyList<FirmwareUpdateState>> paths,
        FirmwareUpdateState state)
    {
        Assert.True(paths.ContainsKey(state), $"{state} is not reachable from Idle.");
        return paths[state];
    }

    private static FirmwareUpdateContext ContextAt(FirmwareUpdateState state)
    {
        return ContextAlong(PathTo(DiscoverShortestPaths(), state));
    }

    private static bool CanTransition(
        IReadOnlyList<FirmwareUpdateState> pathToSource,
        FirmwareUpdateState target)
    {
        var context = ContextAlong(pathToSource);
        return Record.Exception(() => context.TransitionToState(target, "Probe")) is null;
    }

    private static Dictionary<FirmwareUpdateState, IReadOnlyList<FirmwareUpdateState>>
        DiscoverShortestPaths()
    {
        var paths = new Dictionary<FirmwareUpdateState, IReadOnlyList<FirmwareUpdateState>>
        {
            [FirmwareUpdateState.Idle] = []
        };

        var queue = new Queue<FirmwareUpdateState>();
        queue.Enqueue(FirmwareUpdateState.Idle);

        while (queue.Count > 0)
        {
            var from = queue.Dequeue();
            foreach (var to in AllStates)
            {
                if (paths.ContainsKey(to) || !CanTransition(paths[from], to))
                {
                    continue;
                }

                paths[to] = [.. paths[from], to];
                queue.Enqueue(to);
            }
        }

        return paths;
    }

    private static Dictionary<FirmwareUpdateState, List<FirmwareUpdateState>> DiscoverEdges(
        IReadOnlyDictionary<FirmwareUpdateState, IReadOnlyList<FirmwareUpdateState>> paths)
    {
        return paths.Keys.ToDictionary(
            from => from,
            from => AllStates
                .Where(to => to != from && CanTransition(paths[from], to))
                .ToList());
    }

    private static bool CanReach(
        IReadOnlyDictionary<FirmwareUpdateState, List<FirmwareUpdateState>> edges,
        FirmwareUpdateState from,
        FirmwareUpdateState target)
    {
        var seen = new HashSet<FirmwareUpdateState>();
        var queue = new Queue<FirmwareUpdateState>();
        queue.Enqueue(from);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == target)
            {
                return true;
            }

            if (!seen.Add(current) || !edges.TryGetValue(current, out var next))
            {
                continue;
            }

            foreach (var state in next)
            {
                queue.Enqueue(state);
            }
        }

        return false;
    }

    private static List<FirmwareUpdateStateChangedEventArgs> Subscribe(
        FirmwareUpdateContext context)
    {
        var events = new List<FirmwareUpdateStateChangedEventArgs>();
        context.StateChanged += (_, e) => events.Add(e);
        return events;
    }
}
