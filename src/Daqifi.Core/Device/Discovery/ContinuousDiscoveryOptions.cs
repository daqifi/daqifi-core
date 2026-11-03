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
    /// Gets or sets the maximum duration of a single discovery pass. Each pass runs the
    /// wrapped finder under a cancellation token that is cancelled after this duration, so
    /// the pass is bounded by the timeout and is also interrupted promptly when discovery is
    /// stopped or disposed. For listen-based transports (WiFi) this is effectively the
    /// response-collection window; probe-based transports (Serial/HID) typically return
    /// sooner. Default is 3 seconds.
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
    /// Gets or sets an optional sink that receives one <see cref="DiscoveryPassReport"/> for every
    /// discovery pass, so a consumer can tell a pass that found nothing from a pass that ran out
    /// of time. <see langword="null"/> — the default — reports nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ContinuousDeviceFinder"/> raises an event when a device arrives and when a
    /// device goes away. A pass that times out raises nothing, and so does a pass that found the
    /// devices it already knew about. A user looking at an empty device list therefore cannot
    /// tell "nothing is here" from "discovery is not working" (#725). This sink carries that
    /// difference.
    /// </para>
    /// <para>
    /// The sink lives on this options object rather than on a method, which is the shape
    /// <see cref="Daqifi.Core.Device.SdCard.SdCardParseOptions.Progress"/> already uses. Like
    /// every other value here it is read once, at construction, so setting it afterwards has no
    /// effect.
    /// </para>
    /// <para>
    /// Reporting is best effort. An exception from the sink is caught and sent to
    /// <see cref="ContinuousDeviceFinder.ScanError"/>, which is where this class sends every other
    /// faulty handler. It never stops the scan loop and it never discards the devices the pass
    /// found.
    /// </para>
    /// </remarks>
    public IProgress<DiscoveryPassReport>? Progress { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the wrapped finder should be left open
    /// when the <see cref="ContinuousDeviceFinder"/> is disposed. When false (the
    /// default), a wrapped finder implementing <see cref="IDisposable"/> is disposed
    /// along with the continuous finder. Set to true when the wrapped finder is shared
    /// and owned by the caller.
    /// </summary>
    public bool LeaveInnerFinderOpen { get; set; }
}
