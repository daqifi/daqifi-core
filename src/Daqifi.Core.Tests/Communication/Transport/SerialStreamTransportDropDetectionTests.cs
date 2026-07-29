using Daqifi.Core.Communication.Transport;

namespace Daqifi.Core.Tests.Communication.Transport;

/// <summary>
/// Issue #382 for the serial transport: a physically unplugged device must be reported as a drop
/// within a bounded time, an intentional disconnect must not be, and the port-presence check must
/// never fire on a platform where it cannot see the port it is watching.
/// </summary>
/// <remarks>
/// The port-presence probe is substituted so "the cable was pulled" is reproducible without
/// hardware; the real probe is covered separately by <see cref="IsPortEnumerated_ForAPortThatDoesNotExist_ReportsAbsent"/>
/// and the bench validation on the PR.
/// </remarks>
public class SerialStreamTransportDropDetectionTests
{
    private static readonly TimeSpan FastInterval = TimeSpan.FromMilliseconds(50);

    [Fact]
    public void WhenThePortStopsBeingPresent_TransportReportsTheDrop()
    {
        var present = true;
        using var transport = new SerialStreamTransport("/dev/ttyTest382", livenessCheckInterval: FastInterval)
        {
            PortPresenceProbe = _ => Volatile.Read(ref present)
        };

        var dropped = new ManualResetEventSlim(false);
        TransportStatusEventArgs? captured = null;
        transport.StatusChanged += (_, e) =>
        {
            if (e.IsConnected)
            {
                return;
            }

            captured = e;
            dropped.Set();
        };

        transport.StartDropDetection();
        Assert.True(transport.IsLivenessMonitorActive);

        // The cable is pulled: the port stops being enumerated.
        Volatile.Write(ref present, false);

        Assert.True(dropped.Wait(TimeSpan.FromSeconds(10)), "the transport never reported the unplug");
        Assert.False(transport.IsConnected);
        Assert.IsType<TransportNotConnectedException>(captured!.Error);
        Assert.Contains("/dev/ttyTest382", captured.Error!.Message);
        Assert.False(transport.IsLivenessMonitorActive);
    }

    [Fact]
    public void WhenThePortMomentarilyDisappearsAndComesBack_NoDropIsReported()
    {
        // A single hiccup in the enumeration is not an unplug.
        var present = 1;
        using var transport = new SerialStreamTransport("/dev/ttyTest382", livenessCheckInterval: TimeSpan.FromHours(1))
        {
            // Alternates absent/present on every poll, so the miss run never reaches the threshold.
            PortPresenceProbe = _ => Interlocked.Increment(ref present) % 2 == 0
        };

        var drops = 0;
        transport.StatusChanged += (_, e) =>
        {
            if (!e.IsConnected)
            {
                Interlocked.Increment(ref drops);
            }
        };

        transport.StartDropDetection();

        for (var i = 0; i < 50; i++)
        {
            transport.PollLivenessForTesting();
        }

        Assert.Equal(0, Volatile.Read(ref drops));
        Assert.True(transport.IsLivenessMonitorActive);
    }

    [Fact]
    public void WhenTheProbeCannotSeeThePortAtConnectTime_TheCheckIsNotArmed()
    {
        // A platform (or port-name spelling) the probe cannot see must disable the check rather
        // than report a drop on every poll. I/O fault escalation still covers such a port.
        using var transport = new SerialStreamTransport("COM-not-enumerated", livenessCheckInterval: FastInterval)
        {
            PortPresenceProbe = _ => false
        };

        var drops = 0;
        transport.StatusChanged += (_, e) =>
        {
            if (!e.IsConnected)
            {
                Interlocked.Increment(ref drops);
            }
        };

        transport.StartDropDetection();

        Assert.False(transport.IsLivenessMonitorActive);
        Thread.Sleep(400);
        Assert.Equal(0, Volatile.Read(ref drops));
    }

    [Fact]
    public void WhenThePresenceProbeStartsThrowing_NoDropIsReported()
    {
        // A probe that cannot answer is a failure to observe, not evidence the port went away.
        // Before this was fixed the default probe swallowed its own exceptions and returned
        // "absent", so two transient enumeration failures looked like two consecutive misses and
        // closed a healthy connection.
        var firstCall = true;
        using var transport = new SerialStreamTransport("/dev/ttyTest382", livenessCheckInterval: TimeSpan.FromHours(1))
        {
            PortPresenceProbe = _ =>
            {
                if (firstCall)
                {
                    firstCall = false;
                    return true;
                }

                throw new UnauthorizedAccessException("the probe could not run");
            }
        };

        var drops = 0;
        transport.StatusChanged += (_, e) =>
        {
            if (!e.IsConnected)
            {
                Interlocked.Increment(ref drops);
            }
        };

        transport.StartDropDetection();
        Assert.True(transport.IsLivenessMonitorActive);

        for (var i = 0; i < TransportConnectionWatchdog.PresenceMissThreshold * 5; i++)
        {
            transport.PollLivenessForTesting();
        }

        Assert.Equal(0, Volatile.Read(ref drops));
        Assert.True(transport.IsLivenessMonitorActive);
    }

    [Fact]
    public void WhenThePresenceProbeThrowsAtConnectTime_TheCheckIsNotArmedAndConnectStillSucceeds()
    {
        // No baseline observation means nothing to compare later polls against, so the check stays
        // off — and the failure must not escape into the caller's successful connect.
        using var transport = new SerialStreamTransport("/dev/ttyTest382", livenessCheckInterval: FastInterval)
        {
            PortPresenceProbe = _ => throw new UnauthorizedAccessException("the probe could not run")
        };

        var drops = 0;
        transport.StatusChanged += (_, e) =>
        {
            if (!e.IsConnected)
            {
                Interlocked.Increment(ref drops);
            }
        };

        transport.StartDropDetection();

        Assert.False(transport.IsLivenessMonitorActive);
        Thread.Sleep(400);
        Assert.Equal(0, Volatile.Read(ref drops));

        // Fault escalation still covers the port.
        for (var i = 0; i < TransportConnectionWatchdog.ConsecutiveFaultThreshold; i++)
        {
            transport.ReportIoFault(new IOException("device gone"));
        }

        Assert.Equal(1, Volatile.Read(ref drops));
    }

    [Fact]
    public void WhenTheLivenessIntervalIsZero_TheCheckIsDisabled()
    {
        using var transport = new SerialStreamTransport("/dev/ttyTest382", livenessCheckInterval: TimeSpan.Zero)
        {
            PortPresenceProbe = _ => true
        };

        transport.StartDropDetection();

        Assert.False(transport.IsLivenessMonitorActive);
    }

    [Fact]
    public void AfterAnIntentionalDisconnect_ThePortDisappearingReportsNothing()
    {
        // Releasing the port makes it stop being "ours"; that must never be re-reported as a loss
        // on top of the disconnect the caller asked for.
        var present = true;
        using var transport = new SerialStreamTransport("/dev/ttyTest382", livenessCheckInterval: TimeSpan.FromHours(1))
        {
            PortPresenceProbe = _ => Volatile.Read(ref present)
        };

        transport.StartDropDetection();
        Assert.True(transport.IsLivenessMonitorActive);

        transport.Disconnect();

        var drops = 0;
        transport.StatusChanged += (_, e) =>
        {
            if (!e.IsConnected)
            {
                Interlocked.Increment(ref drops);
            }
        };

        Volatile.Write(ref present, false);
        for (var i = 0; i < 10; i++)
        {
            transport.PollLivenessForTesting();
        }

        Assert.False(transport.IsLivenessMonitorActive);
        Assert.Equal(0, Volatile.Read(ref drops));
    }

    [Fact]
    public void PersistentIoFaults_ReportTheDrop_ButABlipDoesNot()
    {
        using var transport = new SerialStreamTransport("/dev/ttyTest382", livenessCheckInterval: TimeSpan.Zero);

        var drops = 0;
        TransportStatusEventArgs? captured = null;
        transport.StatusChanged += (_, e) =>
        {
            if (e.IsConnected)
            {
                return;
            }

            captured = e;
            Interlocked.Increment(ref drops);
        };

        transport.StartDropDetection();

        // The blip: repeated failures that keep recovering must never disconnect.
        for (var round = 0; round < 10; round++)
        {
            for (var i = 0; i < TransportConnectionWatchdog.ConsecutiveFaultThreshold - 1; i++)
            {
                transport.ReportIoFault(new IOException("blip"));
            }

            transport.ReportIoSuccess();
        }

        Assert.Equal(0, Volatile.Read(ref drops));

        // The real thing: an unbroken run of failures.
        for (var i = 0; i < TransportConnectionWatchdog.ConsecutiveFaultThreshold; i++)
        {
            transport.ReportIoFault(new IOException("device gone"));
        }

        Assert.Equal(1, Volatile.Read(ref drops));
        Assert.IsType<TransportNotConnectedException>(captured!.Error);
    }

    [Fact]
    public void ReportIoFault_BeforeAnyConnection_IsIgnored()
    {
        using var transport = new SerialStreamTransport("/dev/ttyTest382");

        var drops = 0;
        transport.StatusChanged += (_, e) =>
        {
            if (!e.IsConnected)
            {
                Interlocked.Increment(ref drops);
            }
        };

        for (var i = 0; i < TransportConnectionWatchdog.ConsecutiveFaultThreshold * 3; i++)
        {
            transport.ReportIoFault(new IOException("no connection to lose"));
        }

        Assert.Equal(0, Volatile.Read(ref drops));
    }

    [Fact]
    public void ReportIoFault_WithNullError_Throws()
    {
        using var transport = new SerialStreamTransport("/dev/ttyTest382");

        Assert.Throws<ArgumentNullException>(() => transport.ReportIoFault(null!));
    }

    [Fact]
    public void Constructor_WithNegativeLivenessInterval_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SerialStreamTransport("/dev/ttyTest382", livenessCheckInterval: TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void IsPortEnumerated_ForAPortThatDoesNotExist_ReportsAbsent()
    {
        // The real probe, no substitution: neither the framework enumeration nor the filesystem
        // knows this port on any platform.
        Assert.False(SerialStreamTransport.IsPortEnumerated("COM-daqifi-382-does-not-exist"));
        Assert.False(SerialStreamTransport.IsPortEnumerated("/dev/daqifi-382-does-not-exist"));
    }

    [Fact]
    public void IsPortEnumerated_ForAnExistingDeviceNodePath_ReportsPresent()
    {
        // The Unix fallback: a port the framework enumeration happens not to return is still
        // present if its device node exists. Windows port names are not paths, so it does not
        // apply there.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.GetTempFileName();
        try
        {
            Assert.StartsWith("/", path);
            Assert.True(SerialStreamTransport.IsPortEnumerated(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
