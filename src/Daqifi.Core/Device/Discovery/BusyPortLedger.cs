using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Records which serial ports were present but already open during a discovery pass.
/// </summary>
/// <remarks>
/// This exists as its own type because the bookkeeping is concurrent and the failure it guards
/// against is a race. Probes run in parallel and one abandoned on the hard per-port timeout keeps
/// running, so a probe belonging to pass N can reach its catch block during pass N+1 -- after
/// <see cref="BeginPass"/> has already cleared the set. Every write therefore carries the pass its
/// probe STARTED in, and reads ignore anything that is not the current pass.
/// </remarks>
internal sealed class BusyPortLedger
{
    // Case-insensitive because an OS port name is not case-significant, and two spellings of one
    // port must not become two entries -- the reader would then see a phantom second busy port.
    private readonly ConcurrentDictionary<string, (int Pass, BusyPort Port)> _entries =
        new(System.StringComparer.OrdinalIgnoreCase);

    private int _pass;

    /// <summary>The pass currently collecting.</summary>
    internal int CurrentPass => Volatile.Read(ref _pass);

    /// <summary>Clears the set and opens the next pass, returning its number.</summary>
    internal int BeginPass()
    {
        // Clear BEFORE the increment. A probe that enters after the increment gets the new pass
        // and is kept; one that entered before it carries the old pass and is filtered on read.
        // Clearing alone could never be enough -- a late write lands after the clear.
        _entries.Clear();
        Volatile.Write(ref _pass, unchecked(_pass + 1));
        return Volatile.Read(ref _pass);
    }

    /// <summary>Records a busy port against the pass its probe started in.</summary>
    internal void Record(int pass, BusyPort port)
    {
        // Newest pass wins, in BOTH directions, and that is the whole point.
        //
        // The first version used TryAdd, which silently does nothing when the key is present. A
        // late probe from the previous pass re-populating its entry after BeginPass cleared the
        // set would then make the CURRENT pass's write for that same port fail -- so the port
        // would be missing from PortsFromLastPass and the device holding it would be reported
        // lost. That is precisely the bug this whole mechanism exists to prevent, reintroduced
        // through a race.
        //
        // The reverse must hold too: a stale write arriving after the current pass has already
        // recorded the port must not clobber it, which a plain indexer assignment would.
        _entries.AddOrUpdate(
            port.PortName,
            _ => (pass, port),
            (_, existing) => IsNewerPass(pass, existing.Pass) ? (pass, port) : existing);
    }

    /// <summary>Ports recorded as busy by the pass that is currently collecting.</summary>
    internal IReadOnlyCollection<BusyPort> PortsFromLastPass()
    {
        var pass = Volatile.Read(ref _pass);
        return _entries.Values.Where(e => e.Pass == pass).Select(e => e.Port).ToList();
    }

    /// <summary>
    /// Wrap-safe "<paramref name="candidate"/> is newer than <paramref name="existing"/>".
    /// </summary>
    /// <remarks>
    /// Compares the DIFFERENCE rather than the values, so it stays correct across the int wrap a
    /// long-lived process eventually reaches. `a > b` would invert for one pass at the wrap and
    /// drop a legitimate rescue; the subtraction form is the same idiom the firmware uses for
    /// tick deadlines, and costs nothing.
    /// </remarks>
    internal static bool IsNewerPass(int candidate, int existing) => unchecked(candidate - existing) > 0;
}
