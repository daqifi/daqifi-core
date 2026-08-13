using System.IO.Ports;

namespace Daqifi.Core.Communication.Transport;

/// <summary>
/// A short-lived, shared cache of <see cref="SerialPort.GetPortNames"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every connected <see cref="SerialStreamTransport"/> re-checks that its port is still enumerated
/// once a second. The enumeration is a whole-system scan that allocates a <see cref="string"/> per
/// port plus the array, and on Windows it is a registry read — so a ten-device fleet ran ten full
/// scans a second to answer ten questions that one scan already answers (issue #491). Sharing one
/// snapshot makes that cost a function of elapsed time instead of of how many devices are connected.
/// </para>
/// <para>
/// <b>Staleness runs one way only: a "yes" may be up to <see cref="CacheDurationMs"/> old, a "no"
/// is always taken now.</b> Reusing a positive is what makes the cache worth having, and the cost
/// is bounded — a port removed just after a snapshot keeps reading as present until that snapshot
/// expires, moving worst-case unplug detection from roughly three seconds to roughly four. Reusing
/// a <em>negative</em> would be the dangerous direction and is therefore never done: it would report
/// a port plugged in since the snapshot as absent, which is the difference between a transport
/// arming its presence check at connect time and silently not arming it, and between a healthy
/// connection surviving and being torn down. A cached snapshot that does not list the port is
/// treated as no answer at all, and a fresh enumeration decides.
/// </para>
/// <para>
/// A failed enumeration is never cached and never turned into an answer — it propagates, so the
/// caller can still tell "not observed" from "observed absent".
/// </para>
/// </remarks>
internal sealed class SerialPortNameSnapshot
{
    /// <summary>
    /// Default lifetime of a snapshot. Matches the presence-poll cadence
    /// (<see cref="TransportConnectionWatchdog.DefaultPresencePollInterval"/>), so a process makes
    /// at most about one enumeration per second no matter how many transports are polling.
    /// </summary>
    internal const int DefaultCacheDurationMs = 1000;

    /// <summary>
    /// The instance the production presence probe reads. Tests construct their own rather than
    /// mutating this one, so nothing here is process-wide mutable state.
    /// </summary>
    internal static SerialPortNameSnapshot Shared { get; } = new(SerialPort.GetPortNames);

    private readonly Func<string[]> _enumerate;
    private readonly object _gate = new();
    private string[] _names = [];
    private long _takenAtMs;
    private bool _hasSnapshot;
    private long _enumerationCount;

    /// <summary>
    /// Initializes a new snapshot cache.
    /// </summary>
    /// <param name="enumerate">The underlying enumeration to cache.</param>
    /// <param name="cacheDurationMs">
    /// How long a snapshot may be reused, in milliseconds. Zero or less disables caching, which is
    /// what a caller wanting the uncached behaviour should pass.
    /// </param>
    internal SerialPortNameSnapshot(Func<string[]> enumerate, int cacheDurationMs = DefaultCacheDurationMs)
    {
        _enumerate = enumerate ?? throw new ArgumentNullException(nameof(enumerate));
        CacheDurationMs = cacheDurationMs;
    }

    /// <summary>
    /// How long a snapshot may be reused, in milliseconds.
    /// </summary>
    internal int CacheDurationMs { get; }

    /// <summary>
    /// How many times the underlying enumeration has actually run. The point of this class is that
    /// it grows with elapsed time rather than with the number of transports asking.
    /// </summary>
    internal long EnumerationCount => Interlocked.Read(ref _enumerationCount);

    /// <summary>
    /// Reports whether <paramref name="portName"/> appears in the current snapshot, taking a fresh
    /// one first if none is live.
    /// </summary>
    /// <param name="portName">The port name to look for; compared case-insensitively.</param>
    /// <exception cref="Exception">
    /// Propagates whatever the underlying enumeration raised when a fresh snapshot was needed and
    /// could not be taken.
    /// </exception>
    internal bool Contains(string portName)
    {
        // The enumeration runs under the gate rather than outside it. Concurrent callers are exactly
        // the case this exists for — several watchdog timers firing on the same tick — and letting
        // them all through would have each take its own snapshot, which is the cost being removed.
        // A blocked caller re-checks freshness on the way in, so only the first one enumerates.
        lock (_gate)
        {
            var live = _hasSnapshot && Environment.TickCount64 - _takenAtMs < CacheDurationMs;
            if (live)
            {
                if (ContainsName(_names, portName))
                {
                    return true;
                }

                // A cached snapshot that does not list the port is no answer: it predates anything
                // plugged in since. Fall through and let a fresh enumeration decide.
            }

            return ContainsName(Enumerate(), portName);
        }
    }

    /// <summary>
    /// Takes and publishes a fresh snapshot. Must be called holding <see cref="_gate"/>.
    /// </summary>
    private string[] Enumerate()
    {
        // Published only after the call returns: a throwing enumeration must leave the previous
        // snapshot and its timestamp untouched rather than install an empty one, which would read
        // as "every port just disappeared".
        var names = _enumerate() ?? [];
        _names = names;
        _takenAtMs = Environment.TickCount64;
        _hasSnapshot = true;
        Interlocked.Increment(ref _enumerationCount);
        return names;
    }

    private static bool ContainsName(string[] names, string portName)
    {
        foreach (var name in names)
        {
            if (string.Equals(name, portName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
