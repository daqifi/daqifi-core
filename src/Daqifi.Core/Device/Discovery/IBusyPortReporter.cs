using System.Collections.Generic;

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Implemented by a finder that can tell the difference between "no device here" and
/// "there is a port here, but something already has it open".
/// </summary>
/// <remarks>
/// <para>
/// A serial probe identifies a device by opening its port and asking it questions. When the
/// caller's own application already holds that port — the normal shape for an app with a device
/// list on screen next to a connected device — the open fails and the probe answers exactly as it
/// does for a port with nothing on it. <see cref="ContinuousDeviceFinder"/> then counts that as a
/// miss and, after <see cref="ContinuousDiscoveryOptions.MissThreshold"/> passes, reports the
/// device the caller is actively using as lost (issue #532).
/// </para>
/// <para>
/// This is deliberately a separate, internal seam rather than a change to
/// <see cref="IDeviceFinder.DiscoverAsync(System.Threading.CancellationToken)"/>. A one-shot discovery has no prior identity to
/// reuse, so all it could say about a locked port is "something DAQiFi-shaped is here but busy" —
/// which may or may not be worth promising in the public API, and is a decision this fix does not
/// need to make. Continuous discovery does have the prior identity, which is what lets it resolve
/// the ambiguity without guessing.
/// </para>
/// </remarks>
internal interface IBusyPortReporter
{
    /// <summary>
    /// Port names that were present but could not be opened during the most recent discovery
    /// pass, because something else held them.
    /// </summary>
    /// <remarks>
    /// Reset at the start of every pass, so it describes that pass alone and never accumulates.
    /// </remarks>
    IReadOnlyCollection<BusyPort> TakeBusyPortsFromLastPass();
}
