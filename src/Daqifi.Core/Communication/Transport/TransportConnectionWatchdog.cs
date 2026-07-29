namespace Daqifi.Core.Communication.Transport;

/// <summary>
/// Shared drop-detection logic for stream transports. Decides when an established connection
/// should be declared lost and raises that decision exactly once per armed cycle.
/// </summary>
/// <remarks>
/// <para>
/// Two independent signals feed the decision, mirroring the two ways a transport learns its device
/// is gone (issue #382):
/// </para>
/// <list type="number">
/// <item><description>
/// <b>I/O fault escalation.</b> The reader/writer loop reports failures through
/// <see cref="ITransportHealthSink"/>; <see cref="ConsecutiveFaultThreshold"/> consecutive
/// failures with no successful transfer in between mean the link is gone rather than glitching.
/// A single successful read resets the run, so a recoverable blip never disconnects.
/// </description></item>
/// <item><description>
/// <b>Presence polling.</b> An optional periodic probe (for serial: "is this port still
/// enumerated?") bounds detection latency for an idle connection that is not producing read
/// failures because nothing is being read. <see cref="PresenceMissThreshold"/> consecutive misses
/// are required so a single hiccup in the enumeration cannot tear down a live connection.
/// </description></item>
/// </list>
/// <para>
/// Both live here rather than in each transport so the idempotency — a connection is declared lost
/// once, whichever signal wins the race — is implemented in one place.
/// </para>
/// </remarks>
internal sealed class TransportConnectionWatchdog : IDisposable
{
    /// <summary>
    /// Number of consecutive I/O failures, with no successful transfer in between, that mean the
    /// connection is gone rather than glitching.
    /// </summary>
    /// <remarks>
    /// The reader loop backs off 100 ms after a failed read, so five failures is roughly half a
    /// second of an unbrokenly failing stream — long enough that a one-off fault cannot trip it,
    /// short enough that a real drop is reported effectively immediately while traffic is flowing.
    /// </remarks>
    internal const int ConsecutiveFaultThreshold = 5;

    /// <summary>
    /// Number of consecutive presence-probe misses required before the connection is declared lost.
    /// </summary>
    internal const int PresenceMissThreshold = 2;

    /// <summary>
    /// Default cadence for presence polling. With <see cref="PresenceMissThreshold"/> this bounds
    /// detection of a silent drop at roughly three seconds (two intervals plus up to one interval
    /// of phase between the drop and the next poll).
    /// </summary>
    internal static readonly TimeSpan DefaultPresencePollInterval = TimeSpan.FromSeconds(1);

    private readonly Action<Exception> _onConnectionLost;
    private readonly string _description;
    private readonly object _timerLock = new();

    /// <summary>
    /// 1 while a connection is established and a loss may still be signalled, 0 otherwise. The
    /// armed-to-disarmed transition is the one-shot gate: whichever signal wins the race performs
    /// it, and every later signal sees 0 and does nothing.
    /// </summary>
    private int _armed;

    private int _consecutiveFaults;
    private int _presenceMisses;
    private int _pollInFlight;
    private Timer? _presenceTimer;
    private Func<bool>? _presenceProbe;
    private string _presenceLostMessage = string.Empty;
    private bool _disposed;

    /// <summary>
    /// Initializes a new watchdog.
    /// </summary>
    /// <param name="description">
    /// How the transport describes itself in the exception attached to a loss, e.g.
    /// <c>"Serial transport (/dev/cu.usbmodem1101)"</c>.
    /// </param>
    /// <param name="onConnectionLost">
    /// Invoked at most once per armed cycle when the connection is declared lost. Invoked on the
    /// reporting thread (a reader/writer loop) or on a timer thread, never with an internal lock
    /// held.
    /// </param>
    public TransportConnectionWatchdog(string description, Action<Exception> onConnectionLost)
    {
        _description = description ?? throw new ArgumentNullException(nameof(description));
        _onConnectionLost = onConnectionLost ?? throw new ArgumentNullException(nameof(onConnectionLost));
    }

    /// <summary>
    /// Gets a value indicating whether a connection loss can still be signalled.
    /// </summary>
    public bool IsArmed => Volatile.Read(ref _armed) == 1;

    /// <summary>
    /// Gets a value indicating whether presence polling is currently running.
    /// </summary>
    public bool IsPollingPresence
    {
        get
        {
            lock (_timerLock)
            {
                return _presenceTimer != null;
            }
        }
    }

    /// <summary>
    /// Arms the watchdog after a successful connect, clearing any state left by a previous cycle.
    /// </summary>
    public void Arm()
    {
        Volatile.Write(ref _consecutiveFaults, 0);
        Volatile.Write(ref _presenceMisses, 0);
        Volatile.Write(ref _armed, 1);
    }

    /// <summary>
    /// Disarms the watchdog and stops presence polling. Used for an intentional disconnect, where
    /// the transport reports <c>Disconnected</c> itself and a concurrent in-flight poll or read
    /// failure must not also report a loss.
    /// </summary>
    public void Disarm()
    {
        Volatile.Write(ref _armed, 0);
        StopPresencePolling();
        Volatile.Write(ref _consecutiveFaults, 0);
        Volatile.Write(ref _presenceMisses, 0);
    }

    /// <summary>
    /// Starts polling <paramref name="probe"/> for the continued presence of the underlying device.
    /// No-ops when the watchdog is not armed or <paramref name="interval"/> is not positive.
    /// </summary>
    /// <param name="probe">Returns <c>true</c> while the device is still present.</param>
    /// <param name="lostMessage">Message for the exception attached to a presence-driven loss.</param>
    /// <param name="interval">Polling cadence.</param>
    public void StartPresencePolling(Func<bool> probe, string lostMessage, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(probe);

        if (!IsArmed || interval <= TimeSpan.Zero)
        {
            return;
        }

        lock (_timerLock)
        {
            if (_disposed)
            {
                return;
            }

            _presenceProbe = probe;
            _presenceLostMessage = lostMessage;
            Volatile.Write(ref _presenceMisses, 0);

            _presenceTimer?.Dispose();
            _presenceTimer = new Timer(_ => PollPresence(), null, interval, interval);
        }
    }

    /// <summary>
    /// Stops presence polling, leaving the armed state untouched.
    /// </summary>
    public void StopPresencePolling()
    {
        lock (_timerLock)
        {
            _presenceTimer?.Dispose();
            _presenceTimer = null;
        }
    }

    /// <summary>
    /// Records a failed read or write. Declares the connection lost once
    /// <see cref="ConsecutiveFaultThreshold"/> failures have occurred with no successful transfer
    /// in between.
    /// </summary>
    /// <param name="error">The exception the failed operation raised.</param>
    public void RecordFault(Exception error)
    {
        if (!IsArmed)
        {
            return;
        }

        var faults = Interlocked.Increment(ref _consecutiveFaults);
        if (faults < ConsecutiveFaultThreshold)
        {
            return;
        }

        SignalConnectionLost(new TransportNotConnectedException(
            $"{_description} saw {ConsecutiveFaultThreshold} consecutive I/O failures with no " +
            "successful transfer in between; the connection is treated as lost.",
            error));
    }

    /// <summary>
    /// Records a successful read or write, clearing the current failure run.
    /// </summary>
    public void RecordSuccess()
    {
        // Read first: the overwhelmingly common case is a healthy stream with the counter already
        // at zero, and this runs once per successful read.
        if (Volatile.Read(ref _consecutiveFaults) != 0)
        {
            Interlocked.Exchange(ref _consecutiveFaults, 0);
        }
    }

    /// <summary>
    /// Runs one presence probe. Exposed for tests so the polling decision can be exercised without
    /// waiting on a timer.
    /// </summary>
    internal void PollPresence()
    {
        // A probe that runs long (an enumeration that blocks) must not stack callbacks.
        if (Interlocked.Exchange(ref _pollInFlight, 1) != 0)
        {
            return;
        }

        try
        {
            if (!IsArmed)
            {
                return;
            }

            var probe = _presenceProbe;
            if (probe == null)
            {
                return;
            }

            if (probe())
            {
                Volatile.Write(ref _presenceMisses, 0);
                return;
            }

            if (Interlocked.Increment(ref _presenceMisses) < PresenceMissThreshold)
            {
                return;
            }

            SignalConnectionLost(new TransportNotConnectedException(_presenceLostMessage));
        }
        catch
        {
            // A probe that throws is a failure to observe, not evidence of a drop: never let it
            // disconnect a healthy transport, and never let it kill the timer thread. Probes are
            // required to surface "could not answer" this way rather than returning false, which
            // would be indistinguishable from the device having actually gone.
            //
            // Clearing the run matters as much as not incrementing it. The threshold is about
            // CONSECUTIVE observed absences; letting a miss survive an exception would make
            // "absent, could-not-observe, absent" satisfy a two-miss threshold that never actually
            // saw the port absent twice in a row.
            Volatile.Write(ref _presenceMisses, 0);
        }
        finally
        {
            Volatile.Write(ref _pollInFlight, 0);
        }
    }

    /// <summary>
    /// Declares the connection lost, at most once per armed cycle.
    /// </summary>
    private void SignalConnectionLost(Exception error)
    {
        // Disarm and signal in one step. Whichever of the two detectors gets here first owns the
        // notification; the other sees 0 and returns.
        if (Interlocked.Exchange(ref _armed, 0) != 1)
        {
            return;
        }

        StopPresencePolling();
        _onConnectionLost(error);
    }

    /// <summary>
    /// Disposes the watchdog, stopping presence polling.
    /// </summary>
    public void Dispose()
    {
        lock (_timerLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _presenceTimer?.Dispose();
            _presenceTimer = null;
        }

        Volatile.Write(ref _armed, 0);
    }
}
