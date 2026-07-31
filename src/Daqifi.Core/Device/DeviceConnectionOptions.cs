using Daqifi.Core.Communication.Transport;
using Microsoft.Extensions.Logging;

#nullable enable

namespace Daqifi.Core.Device;

/// <summary>
/// Configuration options for device connection behavior.
/// </summary>
public class DeviceConnectionOptions
{
    /// <summary>
    /// Gets or sets the device name. Default is "DAQiFi Device".
    /// </summary>
    public string DeviceName { get; set; } = "DAQiFi Device";

    /// <summary>
    /// Gets or sets the connection retry options for transport-level retry behavior.
    /// Default is null, which uses the transport's default behavior.
    /// </summary>
    public ConnectionRetryOptions? ConnectionRetry { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to run device initialization after connection.
    /// When true, <see cref="DaqifiDevice.InitializeAsync"/> is called after connecting.
    /// Default is true.
    /// </summary>
    public bool InitializeDevice { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum time device initialization waits for the device to report its
    /// channel configuration before failing with a <see cref="TimeoutException"/>.
    /// Only applies when <see cref="InitializeDevice"/> is true. Must be positive; a non-positive
    /// value causes initialization (and the connecting call) to throw
    /// <see cref="ArgumentOutOfRangeException"/>. Default is 8 seconds.
    /// </summary>
    public TimeSpan ChannelPopulationTimeout { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Gets or sets a value indicating whether initialization must leave a stream that is already
    /// running on the device untouched. Default is <c>false</c>, which preserves the historical
    /// behavior: connecting takes control of the device and stops whatever it was streaming.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Streaming is a single global device state — one acquisition, one destination interface — so
    /// the default initialization sequence deliberately clears it, which is right for the usual
    /// single-session case (it also clears a stream orphaned by a crashed session). When a second
    /// session connects to a device another session is already streaming, that same sequence
    /// silently ends the first session's acquisition.
    /// </para>
    /// <para>
    /// Set this to <c>true</c> for a secondary "observe" connection. Initialization then omits every
    /// command that halts or re-routes the device's stream, and only queries the device for its
    /// identity and channel configuration. See
    /// <see cref="DaqifiDevice.PreserveActiveStream"/> for exactly which commands are skipped and
    /// what the resulting session can and cannot do.
    /// </para>
    /// <para>
    /// This only protects against <em>this</em> library's connect sequence. Two processes can still
    /// fight over one device; within a process, route connections through
    /// <see cref="DaqifiDeviceRegistry"/> so the same physical unit is not opened twice at all.
    /// </para>
    /// </remarks>
    public bool PreserveActiveStream { get; set; }

    /// <summary>
    /// Optional logger the constructed device routes its diagnostics through (bad calibration/
    /// resolution warnings, SCPI text-exchange timing). When null, the device uses a no-op logger.
    /// </summary>
    public ILogger? Logger { get; set; }

    /// <summary>
    /// Creates a default configuration with default retry behavior and device initialization enabled.
    /// </summary>
    public static DeviceConnectionOptions Default => new();

    /// <summary>
    /// Creates a configuration optimized for fast connections with minimal retries.
    /// Uses <see cref="ConnectionRetryOptions.Fast"/> for transport retry behavior.
    /// </summary>
    public static DeviceConnectionOptions Fast => new()
    {
        ConnectionRetry = ConnectionRetryOptions.Fast
    };

    /// <summary>
    /// Creates a configuration optimized for slow or unreliable connections.
    /// Uses <see cref="ConnectionRetryOptions.Resilient"/> for transport retry behavior.
    /// </summary>
    public static DeviceConnectionOptions Resilient => new()
    {
        ConnectionRetry = ConnectionRetryOptions.Resilient
    };

    /// <summary>
    /// Creates a configuration for a secondary session that must not disturb a stream another
    /// session may already be running on the device. Sets <see cref="PreserveActiveStream"/>.
    /// </summary>
    public static DeviceConnectionOptions Observing => new()
    {
        PreserveActiveStream = true
    };
}
