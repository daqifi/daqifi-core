using Daqifi.Core.Communication.Transport;

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
}
