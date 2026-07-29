using Daqifi.Core.Communication.Transport;

namespace Daqifi.Core.Tests.Communication.Transport;

/// <summary>
/// Coverage for issue #382's decision logic: when a run of I/O failures — or a port that stopped
/// being enumerated — means the connection is gone, and, just as importantly, when it does not.
/// </summary>
public class TransportConnectionWatchdogTests
{
    private static TransportConnectionWatchdog CreateWatchdog(List<Exception> losses)
    {
        return new TransportConnectionWatchdog("Test transport (X)", losses.Add);
    }

    [Fact]
    public void RecordFault_BelowThreshold_DoesNotSignalLoss()
    {
        var losses = new List<Exception>();
        using var watchdog = CreateWatchdog(losses);
        watchdog.Arm();

        for (var i = 0; i < TransportConnectionWatchdog.ConsecutiveFaultThreshold - 1; i++)
        {
            watchdog.RecordFault(new IOException("transient"));
        }

        Assert.Empty(losses);
        Assert.True(watchdog.IsArmed);
    }

    [Fact]
    public void RecordFault_AtThreshold_SignalsLossOnceWithTheFailureAsInnerException()
    {
        var losses = new List<Exception>();
        using var watchdog = CreateWatchdog(losses);
        watchdog.Arm();

        var last = new IOException("device gone");
        for (var i = 0; i < TransportConnectionWatchdog.ConsecutiveFaultThreshold; i++)
        {
            watchdog.RecordFault(i == TransportConnectionWatchdog.ConsecutiveFaultThreshold - 1
                ? last
                : new IOException("earlier"));
        }

        var loss = Assert.Single(losses);
        Assert.IsType<TransportNotConnectedException>(loss);
        Assert.Same(last, loss.InnerException);
        Assert.Contains("Test transport (X)", loss.Message);

        // Disarmed by the signal: further failures on the same dead connection must not re-notify.
        Assert.False(watchdog.IsArmed);
        for (var i = 0; i < TransportConnectionWatchdog.ConsecutiveFaultThreshold * 2; i++)
        {
            watchdog.RecordFault(new IOException("still gone"));
        }

        Assert.Single(losses);
    }

    [Fact]
    public void RecordSuccess_BetweenFaults_PreventsEscalation()
    {
        // The "recoverable blip": a stream that fails, recovers, fails again, and so on must never
        // be torn down, no matter how many total failures accumulate.
        var losses = new List<Exception>();
        using var watchdog = CreateWatchdog(losses);
        watchdog.Arm();

        for (var round = 0; round < 20; round++)
        {
            for (var i = 0; i < TransportConnectionWatchdog.ConsecutiveFaultThreshold - 1; i++)
            {
                watchdog.RecordFault(new IOException("blip"));
            }

            watchdog.RecordSuccess();
        }

        Assert.Empty(losses);
        Assert.True(watchdog.IsArmed);
    }

    [Fact]
    public void RecordFault_WhenNotArmed_IsIgnored()
    {
        // Before a connect and after an intentional disconnect there is nothing to lose.
        var losses = new List<Exception>();
        using var watchdog = CreateWatchdog(losses);

        for (var i = 0; i < TransportConnectionWatchdog.ConsecutiveFaultThreshold * 2; i++)
        {
            watchdog.RecordFault(new IOException("noise"));
        }

        Assert.Empty(losses);
    }

    [Fact]
    public void Arm_AfterAPreviousLoss_ClearsTheFailureRun()
    {
        var losses = new List<Exception>();
        using var watchdog = CreateWatchdog(losses);
        watchdog.Arm();

        for (var i = 0; i < TransportConnectionWatchdog.ConsecutiveFaultThreshold; i++)
        {
            watchdog.RecordFault(new IOException("gone"));
        }

        Assert.Single(losses);

        // Reconnect: the next cycle must start from zero, not one failure away from disconnecting.
        watchdog.Arm();
        watchdog.RecordFault(new IOException("first of the new cycle"));

        Assert.Single(losses);
        Assert.True(watchdog.IsArmed);
    }

    [Fact]
    public void Disarm_SuppressesAnInFlightFailureRun()
    {
        // Closing a port during an intentional disconnect makes the in-flight read fail. That must
        // report a disconnect, never a loss.
        var losses = new List<Exception>();
        using var watchdog = CreateWatchdog(losses);
        watchdog.Arm();

        for (var i = 0; i < TransportConnectionWatchdog.ConsecutiveFaultThreshold - 1; i++)
        {
            watchdog.RecordFault(new IOException("closing"));
        }

        watchdog.Disarm();

        for (var i = 0; i < TransportConnectionWatchdog.ConsecutiveFaultThreshold * 2; i++)
        {
            watchdog.RecordFault(new IOException("closing"));
        }

        Assert.Empty(losses);
    }

    [Fact]
    public void PollPresence_BelowMissThreshold_DoesNotSignalLoss()
    {
        var losses = new List<Exception>();
        using var watchdog = CreateWatchdog(losses);
        watchdog.Arm();

        var present = true;
        watchdog.StartPresencePolling(() => present, "port gone", TimeSpan.FromHours(1));

        present = false;
        for (var i = 0; i < TransportConnectionWatchdog.PresenceMissThreshold - 1; i++)
        {
            watchdog.PollPresence();
        }

        Assert.Empty(losses);
    }

    [Fact]
    public void PollPresence_AtMissThreshold_SignalsLoss()
    {
        var losses = new List<Exception>();
        using var watchdog = CreateWatchdog(losses);
        watchdog.Arm();

        var present = true;
        watchdog.StartPresencePolling(() => present, "port gone", TimeSpan.FromHours(1));

        present = false;
        for (var i = 0; i < TransportConnectionWatchdog.PresenceMissThreshold; i++)
        {
            watchdog.PollPresence();
        }

        var loss = Assert.Single(losses);
        Assert.IsType<TransportNotConnectedException>(loss);
        Assert.Equal("port gone", loss.Message);
        Assert.False(watchdog.IsPollingPresence);
    }

    [Fact]
    public void PollPresence_AfterASingleMissThatRecovers_DoesNotSignalLoss()
    {
        // A one-off enumeration hiccup is not an unplug.
        var losses = new List<Exception>();
        using var watchdog = CreateWatchdog(losses);
        watchdog.Arm();

        var present = true;
        watchdog.StartPresencePolling(() => present, "port gone", TimeSpan.FromHours(1));

        for (var round = 0; round < 10; round++)
        {
            present = false;
            watchdog.PollPresence();
            present = true;
            watchdog.PollPresence();
        }

        Assert.Empty(losses);
    }

    [Fact]
    public void PollPresence_WhenProbeThrows_DoesNotSignalLoss()
    {
        // A broken observation is not evidence of a drop.
        var losses = new List<Exception>();
        using var watchdog = CreateWatchdog(losses);
        watchdog.Arm();

        watchdog.StartPresencePolling(() => throw new UnauthorizedAccessException(), "port gone",
            TimeSpan.FromHours(1));

        for (var i = 0; i < TransportConnectionWatchdog.PresenceMissThreshold * 5; i++)
        {
            watchdog.PollPresence();
        }

        Assert.Empty(losses);
        Assert.True(watchdog.IsArmed);
    }

    [Fact]
    public void PollPresence_WhenAProbeExceptionInterruptsAMissRun_DoesNotSignalLoss()
    {
        // The threshold is about CONSECUTIVE observed absences. "absent, could-not-observe,
        // absent" never saw the port absent twice in a row, so it must not satisfy a two-miss
        // threshold — the exception has to break the run, not merely fail to extend it.
        var losses = new List<Exception>();
        using var watchdog = CreateWatchdog(losses);
        watchdog.Arm();

        var step = 0;
        watchdog.StartPresencePolling(
            () => (step++ % 2 == 0)
                ? false
                : throw new UnauthorizedAccessException("the probe could not run"),
            "port gone",
            TimeSpan.FromHours(1));

        for (var i = 0; i < TransportConnectionWatchdog.PresenceMissThreshold * 10; i++)
        {
            watchdog.PollPresence();
        }

        Assert.Empty(losses);
        Assert.True(watchdog.IsArmed);
    }

    [Fact]
    public void StartPresencePolling_WhenNotArmed_DoesNotStart()
    {
        var losses = new List<Exception>();
        using var watchdog = CreateWatchdog(losses);

        watchdog.StartPresencePolling(() => false, "port gone", TimeSpan.FromHours(1));

        Assert.False(watchdog.IsPollingPresence);
    }

    [Fact]
    public void StartPresencePolling_WithNonPositiveInterval_DoesNotStart()
    {
        var losses = new List<Exception>();
        using var watchdog = CreateWatchdog(losses);
        watchdog.Arm();

        watchdog.StartPresencePolling(() => false, "port gone", TimeSpan.Zero);

        Assert.False(watchdog.IsPollingPresence);
    }

    [Fact]
    public void PollPresence_OnTimerCadence_SignalsLossWithinTheDocumentedBound()
    {
        // The bound the docs promise: miss-threshold intervals of absence, plus up to one interval
        // of phase between the drop and the next poll.
        var signalled = new ManualResetEventSlim(false);
        using var watchdog = new TransportConnectionWatchdog("Test transport (X)", _ => signalled.Set());
        watchdog.Arm();

        var interval = TimeSpan.FromMilliseconds(50);
        watchdog.StartPresencePolling(() => false, "port gone", interval);

        var bound = interval * (TransportConnectionWatchdog.PresenceMissThreshold + 1);
        Assert.True(signalled.Wait(bound + TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Disarm_StopsPresencePolling()
    {
        var losses = new List<Exception>();
        using var watchdog = CreateWatchdog(losses);
        watchdog.Arm();
        watchdog.StartPresencePolling(() => false, "port gone", TimeSpan.FromHours(1));

        Assert.True(watchdog.IsPollingPresence);

        watchdog.Disarm();

        Assert.False(watchdog.IsPollingPresence);
        Assert.False(watchdog.IsArmed);
    }

    [Fact]
    public void ConcurrentFaults_SignalLossExactlyOnce()
    {
        // Reader and writer loops report from different threads; the loss must be raised once.
        var losses = new List<Exception>();
        var gate = new object();
        using var watchdog = new TransportConnectionWatchdog("Test transport (X)", ex =>
        {
            lock (gate)
            {
                losses.Add(ex);
            }
        });
        watchdog.Arm();

        Parallel.For(0, 200, _ => watchdog.RecordFault(new IOException("gone")));

        Assert.Single(losses);
    }
}
