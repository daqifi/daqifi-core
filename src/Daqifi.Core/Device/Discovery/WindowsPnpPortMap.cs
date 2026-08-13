using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// One row of the <c>Win32_PnPEntity</c> projection the port map is built from.
/// </summary>
/// <param name="DeviceId">The entity's PnP device instance ID, e.g. <c>USB\VID_04D8&amp;PID_F794\...</c>.</param>
/// <param name="Caption">The entity's display name, e.g. <c>USB Serial Device (COM9)</c>.</param>
internal readonly record struct PnpPortEntity(string? DeviceId, string? Caption);

/// <summary>
/// A short-lived, shared map from COM port name to the PnP device instance IDs of the
/// <c>PNPClass = 'Ports'</c> entities that claim it.
/// </summary>
/// <remarks>
/// <para>
/// Both Windows discovery providers used to answer "which PnP entity is COM9?" with their own
/// WMI query, one per port, per discovery pass — a <c>Win32_PnPEntity</c> search with a
/// <c>LIKE</c> predicate, which costs on the order of 100-500 ms warm. A machine with a dozen
/// COM ports (Bluetooth SPP pairs, vendor virtual ports) therefore spent seconds of strictly
/// sequential blocking work before opening a single port, and continuous discovery repeated
/// that every second (issue #487). One query per pass answers all of those questions at once,
/// which is what the macOS provider already does with <c>ioreg</c>.
/// </para>
/// <para>
/// A map may be reused for <see cref="CacheDurationMs"/>, so an entry can be that stale. The
/// exposure is deliberately bounded to less than one discovery pass, and it is the safe
/// direction for both callers: a stale <em>miss</em> makes
/// <see cref="WindowsUsbPortDescriptorProvider"/> return no classification, and an unclassified
/// port is probed rather than skipped. The caller for whom a miss is a real answer — the
/// location provider, which runs only for the handful of ports that probed as DAQiFi devices —
/// asks with <c>refreshOnMiss</c> so a port that appeared since the map was built still
/// resolves.
/// </para>
/// <para>
/// A stale <em>hit</em> is the residual risk: a port re-assigned to different hardware inside
/// the window keeps its old VID/PID for the rest of that window, so a device plugged into a
/// just-vacated COM number can be skipped for one pass and is found on the next. That is the
/// same trade the macOS provider has always made.
/// </para>
/// </remarks>
internal sealed class WindowsPnpPortMap
{
    /// <summary>
    /// Default lifetime of a map. Slightly longer than the continuous-discovery interval
    /// (<see cref="ContinuousDiscoveryOptions"/> defaults to one second) so a single pass is
    /// covered by one query, matching <see cref="MacOsUsbPortDescriptorProvider"/>'s window.
    /// </summary>
    internal const int DefaultCacheDurationMs = 2000;

    /// <summary>
    /// The instance the production providers read. Tests construct their own with a fake query
    /// rather than mutating this one, so nothing here is process-wide mutable state.
    /// </summary>
    // QueryPortEntities is annotated [SupportedOSPlatform("windows")] because it touches
    // System.Management; taking the method group here is not calling it, and it re-checks the
    // platform at runtime before it touches WMI, so the analyzer warning is suppressed.
#pragma warning disable CA1416
    internal static WindowsPnpPortMap Shared { get; } = new(QueryPortEntities);
#pragma warning restore CA1416

    // The COM port number a PnP entity claims appears parenthesized in its caption, e.g.
    // "USB Serial Device (COM9)". Matching the parentheses is what keeps "(COM9)" from also
    // matching the entity captioned "(COM90)" — the same distinction the LIKE '%(COM9)%'
    // predicate this replaces relied on.
    private static readonly Regex CaptionPortRegex = new(
        @"\((COM\d+)\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly IReadOnlyList<string> NoDeviceIds = [];

    private readonly Func<IEnumerable<PnpPortEntity>> _query;
    private readonly object _gate = new();
    private Dictionary<string, List<string>> _map = new(StringComparer.OrdinalIgnoreCase);
    private long _builtAtMs;
    private bool _hasMap;
    private long _queryCount;

    /// <summary>
    /// Initializes a new port map.
    /// </summary>
    /// <param name="query">The underlying entity enumeration to cache.</param>
    /// <param name="cacheDurationMs">
    /// How long a built map may be reused, in milliseconds. Zero or less disables caching.
    /// </param>
    internal WindowsPnpPortMap(Func<IEnumerable<PnpPortEntity>> query, int cacheDurationMs = DefaultCacheDurationMs)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
        CacheDurationMs = cacheDurationMs;
    }

    /// <summary>
    /// How long a built map may be reused, in milliseconds.
    /// </summary>
    internal int CacheDurationMs { get; }

    /// <summary>
    /// How many times the underlying enumeration has run to completion. The point of this class
    /// is that it grows with elapsed time rather than with the number of ports asking.
    /// </summary>
    internal long QueryCount => Interlocked.Read(ref _queryCount);

    /// <summary>
    /// Returns the device instance IDs of the entities whose caption claims
    /// <paramref name="portName"/>, in enumeration order, or an empty list if none does.
    /// </summary>
    /// <param name="portName">A COM port name such as <c>COM9</c>; compared case-insensitively.</param>
    /// <param name="refreshOnMiss">
    /// When true, a port missing from a reusable map forces a rebuild before answering, so a
    /// port that appeared since the map was built is still found. Callers that treat a miss as
    /// "no classification, probe it anyway" should leave this false — refreshing for them would
    /// reinstate a per-port query on any machine with a COM port that has no
    /// <c>PNPClass = 'Ports'</c> entity at all.
    /// </param>
    internal IReadOnlyList<string> GetDeviceIds(string portName, bool refreshOnMiss = false)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            return NoDeviceIds;
        }

        // The query runs under the gate rather than outside it. Concurrent callers are exactly the
        // case this exists for — a discovery pass classifying every port — and letting them all
        // through would have each run its own query, which is the cost being removed. A blocked
        // caller re-checks freshness on the way in, so only the first one queries.
        lock (_gate)
        {
            var live = _hasMap && Environment.TickCount64 - _builtAtMs < CacheDurationMs;
            if (live)
            {
                if (_map.TryGetValue(portName, out var cached))
                {
                    return cached;
                }

                if (!refreshOnMiss)
                {
                    return NoDeviceIds;
                }
            }

            return Rebuild().TryGetValue(portName, out var fresh) ? fresh : NoDeviceIds;
        }
    }

    /// <summary>
    /// Rebuilds and publishes the map. Must be called holding <see cref="_gate"/>.
    /// </summary>
    private Dictionary<string, List<string>> Rebuild()
    {
        // Published only after the query returns: a throwing query must leave the previous map and
        // its timestamp untouched rather than install an empty one, which would read as "every
        // port just disappeared" for the rest of the window.
        var rebuilt = BuildMap(_query());
        Interlocked.Increment(ref _queryCount);
        _map = rebuilt;
        _builtAtMs = Environment.TickCount64;
        _hasMap = true;
        return rebuilt;
    }

    /// <summary>
    /// Groups <paramref name="entities"/> by the COM port names their captions claim. Pure, so
    /// the caption parsing is unit testable on any platform without WMI access.
    /// </summary>
    /// <remarks>
    /// A port maps to a list rather than a single ID because the predicate this replaces matched a
    /// caption substring, not a whole caption: two entities can both mention "(COM9)", and the two
    /// callers pick different entries out of that set — the descriptor provider takes the first one
    /// carrying a VID/PID, the location provider the first one full stop.
    /// </remarks>
    internal static Dictionary<string, List<string>> BuildMap(IEnumerable<PnpPortEntity> entities)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (entities == null)
        {
            return map;
        }

        foreach (var entity in entities)
        {
            if (string.IsNullOrEmpty(entity.DeviceId) || string.IsNullOrEmpty(entity.Caption))
            {
                continue;
            }

            foreach (Match match in CaptionPortRegex.Matches(entity.Caption))
            {
                var portName = match.Groups[1].Value;
                if (map.TryGetValue(portName, out var existing))
                {
                    // Guards a caption that names the same port twice ("… (COM9) bridge (COM9)"),
                    // which would otherwise list one entity under it twice.
                    if (!existing.Contains(entity.DeviceId, StringComparer.OrdinalIgnoreCase))
                    {
                        existing.Add(entity.DeviceId);
                    }
                }
                else
                {
                    map[portName] = [entity.DeviceId];
                }
            }
        }

        return map;
    }

    /// <summary>
    /// Projects every serial-port PnP entity in one WMI query. Errors are reported as "no
    /// entities" rather than thrown, which makes every port unclassified for the current window —
    /// the same fallback the per-port query had, and the direction that costs a probe rather than
    /// a missed device.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static IEnumerable<PnpPortEntity> QueryPortEntities()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return [];
        }

        try
        {
            // Restricting to PNPClass='Ports' skips the rest of the device tree and shrinks WMI's
            // enumeration cost; the same restriction the per-port queries used. No caller value is
            // interpolated into this query at all, which is what retires the COM-name validation
            // the per-port callers needed. The result collection owns native handles and must be
            // disposed, so the rows are materialized here rather than streamed to the caller.
            var entities = new List<PnpPortEntity>();
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT DeviceID, Caption FROM Win32_PnPEntity WHERE PNPClass = 'Ports'");
            using var results = searcher.Get();
            foreach (var entity in results)
            {
                using (entity)
                {
                    entities.Add(new PnpPortEntity(entity["DeviceID"] as string, entity["Caption"] as string));
                }
            }

            return entities;
        }
        catch
        {
            return [];
        }
    }
}
