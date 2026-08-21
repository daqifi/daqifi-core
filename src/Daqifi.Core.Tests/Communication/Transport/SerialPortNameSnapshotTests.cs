using Daqifi.Core.Communication.Transport;

namespace Daqifi.Core.Tests.Communication.Transport;

/// <summary>
/// Issue #491, item 2: the presence probe ran a full <c>SerialPort.GetPortNames()</c> per connected
/// device per second. These cover the shared snapshot that makes the enumeration a function of
/// elapsed time instead of of device count, and the ordering change in
/// <see cref="SerialStreamTransport.IsPortEnumerated(string, SerialPortNameSnapshot)"/> that keeps
/// it from running at all on Unix while the device node is there.
/// </summary>
public class SerialPortNameSnapshotTests
{
    [Fact]
    public void RepeatedLookupsWithinTheCacheWindow_EnumerateOnce()
    {
        var calls = 0;
        var snapshot = new SerialPortNameSnapshot(() =>
        {
            Interlocked.Increment(ref calls);
            return ["COM3", "COM7"];
        });

        for (var i = 0; i < 25; i++)
        {
            Assert.True(snapshot.Contains("COM3"));
        }

        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Equal(1L, snapshot.EnumerationCount);
    }

    [Fact]
    public void ALookupAfterTheCacheWindow_EnumeratesAgain()
    {
        var calls = 0;
        var snapshot = new SerialPortNameSnapshot(
            () =>
            {
                Interlocked.Increment(ref calls);
                return ["COM3"];
            },
            cacheDurationMs: 1);

        Assert.True(snapshot.Contains("COM3"));
        Thread.Sleep(20);
        Assert.True(snapshot.Contains("COM3"));

        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public void WithCachingDisabled_EveryLookupEnumerates()
    {
        var calls = 0;
        var snapshot = new SerialPortNameSnapshot(
            () =>
            {
                Interlocked.Increment(ref calls);
                return ["COM3"];
            },
            cacheDurationMs: 0);

        snapshot.Contains("COM3");
        snapshot.Contains("COM3");
        snapshot.Contains("COM3");

        Assert.Equal(3, Volatile.Read(ref calls));
    }

    /// <summary>
    /// The point of the change: N transports polling on the same tick cost one enumeration, not N.
    /// </summary>
    [Fact]
    public void ConcurrentLookupsOnTheSameTick_EnumerateOnce()
    {
        var calls = 0;
        var snapshot = new SerialPortNameSnapshot(() =>
        {
            Interlocked.Increment(ref calls);

            // Wide enough that every other caller is definitely inside Contains before this returns.
            Thread.Sleep(30);
            return ["COM3"];
        });

        const int callers = 8;
        using var everyoneReady = new Barrier(callers);
        var results = new bool[callers];

        // Dedicated threads, not the thread pool: the barrier requires all of them to be running at
        // once, which a pool that is ramping up cannot promise.
        var threads = new Thread[callers];
        for (var i = 0; i < callers; i++)
        {
            var index = i;
            threads[i] = new Thread(() =>
            {
                everyoneReady.SignalAndWait();
                results[index] = snapshot.Contains("COM3");
            })
            {
                IsBackground = true
            };
            threads[i].Start();
        }

        foreach (var thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "A concurrent lookup never returned.");
        }

        Assert.All(results, Assert.True);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    /// <summary>
    /// The hazard a plain time-based cache would introduce: a port that appeared after the last
    /// snapshot must not read as absent. A transport arms its presence check by asking this the
    /// moment it opens the port, so a stale "no" there would silently disable drop detection for
    /// the life of that connection — and a stale "no" during polling would tear down a healthy one.
    /// </summary>
    [Fact]
    public void APortThatAppearedAfterTheCachedSnapshot_IsStillFound()
    {
        var ports = new List<string> { "COM3" };
        var snapshot = new SerialPortNameSnapshot(() => ports.ToArray(), cacheDurationMs: 60_000);

        Assert.True(snapshot.Contains("COM3"));

        ports.Add("COM9");

        // Well inside the cache window, and the cached snapshot does not list COM9.
        Assert.True(snapshot.Contains("COM9"));
        Assert.Equal(2L, snapshot.EnumerationCount);
    }

    /// <summary>
    /// Only a miss forces the extra enumeration; once the fresh snapshot has the port, the window
    /// starts again from there.
    /// </summary>
    [Fact]
    public void AMissThatRefreshes_LeavesTheNewSnapshotCached()
    {
        var ports = new List<string> { "COM3" };
        var snapshot = new SerialPortNameSnapshot(() => ports.ToArray(), cacheDurationMs: 60_000);

        Assert.True(snapshot.Contains("COM3"));
        ports.Add("COM9");
        Assert.True(snapshot.Contains("COM9"));

        Assert.True(snapshot.Contains("COM9"));
        Assert.True(snapshot.Contains("COM3"));
        Assert.Equal(2L, snapshot.EnumerationCount);
    }

    /// <summary>
    /// A refresh forced by a cached miss can itself fail. That is "not observed", not "absent", so
    /// it must reach the caller rather than being answered from the stale snapshot.
    /// </summary>
    [Fact]
    public void AFailedRefreshAfterACachedMiss_Propagates()
    {
        var shouldThrow = false;
        var snapshot = new SerialPortNameSnapshot(
            () => shouldThrow ? throw new IOException("enumeration failed") : ["COM3"],
            cacheDurationMs: 60_000);

        Assert.True(snapshot.Contains("COM3"));

        shouldThrow = true;
        Assert.Throws<IOException>(() => snapshot.Contains("COM9"));
    }

    [Fact]
    public void PortNameComparison_IsCaseInsensitive()
    {
        var snapshot = new SerialPortNameSnapshot(() => ["COM3"]);

        Assert.True(snapshot.Contains("com3"));
        Assert.False(snapshot.Contains("COM4"));
    }

    [Fact]
    public void AnEnumerationThatReturnsNull_IsTreatedAsNoPorts()
    {
        var snapshot = new SerialPortNameSnapshot(() => null!);

        Assert.False(snapshot.Contains("COM3"));
    }

    /// <summary>
    /// A failed enumeration must stay a failure. Reporting "not found" for it would let two
    /// transient failures look like two consecutive presence misses and close a healthy connection.
    /// </summary>
    [Fact]
    public void AnEnumerationThatThrows_Propagates()
    {
        var snapshot = new SerialPortNameSnapshot(() => throw new UnauthorizedAccessException("nope"));

        Assert.Throws<UnauthorizedAccessException>(() => snapshot.Contains("COM3"));
    }

    /// <summary>
    /// A failure must not be cached — neither as an empty snapshot (which would read as every port
    /// disappearing at once) nor as a fresh timestamp over the last good one. The next lookup, even
    /// well inside the cache window, has to try the enumeration again.
    /// </summary>
    [Fact]
    public void AFailedEnumeration_IsNotCached()
    {
        var shouldThrow = true;
        var snapshot = new SerialPortNameSnapshot(
            () => shouldThrow ? throw new IOException("enumeration failed") : ["COM3"],
            cacheDurationMs: 60_000);

        Assert.Throws<IOException>(() => snapshot.Contains("COM3"));

        shouldThrow = false;
        Assert.True(snapshot.Contains("COM3"));
        Assert.Equal(1L, snapshot.EnumerationCount);
    }

    [Fact]
    public void Constructor_WithNullEnumerator_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SerialPortNameSnapshot(null!));
    }

    [Fact]
    public void IsPortEnumerated_ForAPresentDeviceNode_DoesNotEnumerateAtAll()
    {
        // A Windows port name is not a path, so the cheap check does not apply there.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var calls = 0;
        var snapshot = new SerialPortNameSnapshot(() =>
        {
            Interlocked.Increment(ref calls);
            return [];
        });

        var path = Path.GetTempFileName();
        try
        {
            Assert.StartsWith("/", path);
            Assert.True(SerialStreamTransport.IsPortEnumerated(path, snapshot));
            Assert.Equal(0, Volatile.Read(ref calls));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IsPortEnumerated_ForAnAbsentDeviceNode_FallsBackToTheEnumeration()
    {
        var snapshot = new SerialPortNameSnapshot(() => ["/dev/daqifi-491-listed-but-absent"]);

        Assert.True(SerialStreamTransport.IsPortEnumerated("/dev/daqifi-491-listed-but-absent", snapshot));
        Assert.False(SerialStreamTransport.IsPortEnumerated("/dev/daqifi-491-not-listed", snapshot));
    }

    /// <summary>
    /// The device node already answered "absent" before the enumeration was reached, so a failure
    /// there adds nothing and the probe reports absence rather than propagating — the behaviour the
    /// old ordering reached through its <c>catch</c>.
    /// </summary>
    [Fact]
    public void IsPortEnumerated_ForAnAbsentDeviceNode_SwallowsAFailedEnumeration()
    {
        var snapshot = new SerialPortNameSnapshot(() => throw new UnauthorizedAccessException("nope"));

        Assert.False(SerialStreamTransport.IsPortEnumerated("/dev/daqifi-491-does-not-exist", snapshot));
    }

    /// <summary>
    /// A Windows-style name has no second source, so a failed enumeration is "not observed" and
    /// must reach the caller instead of being reported as a missing port.
    /// </summary>
    [Fact]
    public void IsPortEnumerated_ForAWindowsStyleName_PropagatesAFailedEnumeration()
    {
        var snapshot = new SerialPortNameSnapshot(() => throw new UnauthorizedAccessException("nope"));

        Assert.Throws<UnauthorizedAccessException>(
            () => SerialStreamTransport.IsPortEnumerated("COM9", snapshot));
    }

    [Fact]
    public void IsPortEnumerated_ForManyPortsOnTheSameSnapshot_EnumeratesOnce()
    {
        var calls = 0;
        var snapshot = new SerialPortNameSnapshot(() =>
        {
            Interlocked.Increment(ref calls);
            return ["COM3", "COM4", "COM5", "COM6"];
        });

        foreach (var port in new[] { "COM3", "COM4", "COM5", "COM6" })
        {
            Assert.True(SerialStreamTransport.IsPortEnumerated(port, snapshot));
        }

        Assert.Equal(1, Volatile.Read(ref calls));
    }
}
