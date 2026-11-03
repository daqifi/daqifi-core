using System;

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// How one continuous discovery pass ended.
/// </summary>
public enum DiscoveryPassOutcome
{
    /// <summary>
    /// The pass ran to the end inside its time budget. The counts on the report say what, if
    /// anything, changed; all of them zero means the pass found exactly what the live set
    /// already held.
    /// </summary>
    Completed = 0,

    /// <summary>
    /// The pass used up <see cref="ContinuousDiscoveryOptions.PassTimeout"/>. Whatever the pass
    /// returned is incomplete, and a finder that answers cancellation by throwing returns
    /// nothing at all — in which case the live set is left exactly as it was, rather than
    /// counting every device as missing.
    /// </summary>
    TimedOut = 1,

    /// <summary>
    /// The pass, or the work that folded its results into the live set, raised an exception. The
    /// exception itself goes to <see cref="ContinuousDeviceFinder.ScanError"/>; the counts on the
    /// report are zero, because nothing can be said about a pass that did not finish.
    /// </summary>
    Failed = 2,
}

/// <summary>
/// What one continuous discovery pass did, reported to
/// <see cref="ContinuousDiscoveryOptions.Progress"/> so a consumer can show the difference
/// between discovery that is quiet and discovery that is degraded.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ContinuousDeviceFinder"/> raises an event when a device arrives and when a device
/// goes away. It raises nothing for a pass that found the same devices as the pass before it, and
/// nothing for a pass that ran out of time. Those two cases look the same to a consumer, and both
/// look the same as a finder that has stopped running (#725). This report separates them:
/// <see cref="Outcome"/> says whether the pass worked, and the counts say what it changed.
/// </para>
/// <para>
/// One report arrives per pass, in pass order, after that pass's
/// <see cref="ContinuousDeviceFinder.DeviceDiscovered"/> and
/// <see cref="ContinuousDeviceFinder.DeviceLost"/> events. A pass that
/// <see cref="ContinuousDeviceFinder.StopAsync"/> or
/// <see cref="ContinuousDeviceFinder.Dispose"/> cuts short reports nothing: the consumer asked
/// discovery to stop, so it knows why the reports end.
/// </para>
/// </remarks>
/// <param name="Outcome">How the pass ended.</param>
/// <param name="DevicesSeen">
/// How many distinct devices the pass returned, counted by identity, so a device the pass listed
/// twice counts once. Zero for a pass that returned nothing and for a pass that did not finish.
/// </param>
/// <param name="NewDevices">
/// How many of those devices were not in the live set before this pass. Each one also raised
/// <see cref="ContinuousDeviceFinder.DeviceDiscovered"/>.
/// </param>
/// <param name="MetadataUpdates">
/// How many devices the live set already held came back with different metadata — a new IP
/// address, a new port name, a new power state, and so on. This is the change that raises no
/// event of its own, so a consumer that wants to redraw a row has only this count to go by.
/// </param>
/// <param name="LostDevices">
/// How many devices this pass removed from the live set, after
/// <see cref="ContinuousDiscoveryOptions.MissThreshold"/> consecutive misses. Each one also
/// raised <see cref="ContinuousDeviceFinder.DeviceLost"/>.
/// </param>
/// <param name="LiveDeviceCount">
/// How many devices the live set holds now that the pass is folded in: the size of
/// <see cref="ContinuousDeviceFinder.Devices"/> at the moment of the report. A pass that did not
/// finish still reports this, because the devices found earlier are still live — only the counts
/// above are zero.
/// </param>
/// <param name="NextPassDelay">
/// The pause before the next pass starts, which is
/// <see cref="ContinuousDiscoveryOptions.Interval"/>. A bounded retry can say "this was the last
/// attempt" with a zero delay; a continuous loop cannot, because an interval of zero is a
/// legitimate setting and a pass always follows. A consumer learns that discovery stopped by
/// stopping it, and the reports then end.
/// </param>
public readonly record struct DiscoveryPassReport(
    DiscoveryPassOutcome Outcome,
    int DevicesSeen,
    int NewDevices,
    int MetadataUpdates,
    int LostDevices,
    int LiveDeviceCount,
    TimeSpan NextPassDelay);
