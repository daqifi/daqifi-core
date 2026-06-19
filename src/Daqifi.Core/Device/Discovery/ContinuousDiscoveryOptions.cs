using System;

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Configuration for <see cref="ContinuousDeviceFinder"/>: how often to scan,
/// how long each scan pass may run, and how many consecutive missed passes a
/// device must be absent before it is reported lost.
/// </summary>
public class ContinuousDiscoveryOptions
{
    /// <summary>
    /// Gets or sets the delay between the end of one discovery pass and the start
    /// of the next. Default is 1 second. Use <see cref="TimeSpan.Zero"/> to start
    /// the next pass immediately.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets the maximum duration of a single discovery pass, passed to the
    /// wrapped finder's <see cref="IDeviceFinder.DiscoverAsync(TimeSpan)"/>. For
    /// listen-based transports (WiFi) this is effectively the response-collection
    /// window; probe-based transports (Serial/HID) typically return sooner. Default
    /// is 3 seconds.
    /// </summary>
    public TimeSpan PassTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Gets or sets the number of consecutive passes a device must be absent before
    /// <see cref="ContinuousDeviceFinder.DeviceLost"/> is raised and it is removed
    /// from the live set. A value of 1 reports a device lost as soon as it is missing
    /// from a single pass; the default of 2 tolerates one dropped response (useful for
    /// lossy UDP discovery). Must be at least 1.
    /// </summary>
    public int MissThreshold { get; set; } = 2;

    /// <summary>
    /// Gets or sets an optional function that computes a stable identity key for a
    /// discovered device. Devices sharing the same key are treated as the same device
    /// across passes. When null, <see cref="ContinuousDeviceFinder"/> uses a default
    /// per-transport identity (connection type combined with MAC address, device path,
    /// port name, or serial number, whichever is available).
    /// </summary>
    public Func<IDeviceInfo, string>? IdentitySelector { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the wrapped finder should be left open
    /// when the <see cref="ContinuousDeviceFinder"/> is disposed. When false (the
    /// default), a wrapped finder implementing <see cref="IDisposable"/> is disposed
    /// along with the continuous finder. Set to true when the wrapped finder is shared
    /// and owned by the caller.
    /// </summary>
    public bool LeaveInnerFinderOpen { get; set; }
}
