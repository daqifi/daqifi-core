namespace Daqifi.Core.Device;

/// <summary>
/// Describes a failure that happened on one of a device's background threads — the message
/// consumer's read loop, or the per-frame stream decoder.
/// </summary>
/// <remarks>
/// <para>
/// Purely observational (issue #378). Raising this event never changes what the device does: no
/// tear-down, no retry, no status change, and a single bad frame is still isolated so the stream
/// survives it. Escalating a genuinely dead link to
/// <see cref="ConnectionStatus.Lost"/> is the transports' job and is reported through
/// <see cref="DaqifiDevice.StatusChanged"/>.
/// </para>
/// <para>
/// Delivery is throttled per <see cref="Source"/> and exception type — see
/// <see cref="DaqifiDevice.ErrorOccurred"/> for the policy — so <see cref="SuppressedCount"/> may be
/// non-zero on the events that do get through.
/// </para>
/// </remarks>
public class DeviceErrorEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceErrorEventArgs"/> class.
    /// </summary>
    /// <param name="source">Which part of the pipeline failed.</param>
    /// <param name="error">The exception that was caught.</param>
    /// <param name="suppressedCount">
    /// How many like failures were collapsed by the throttle since the previous raise. Zero when
    /// nothing was suppressed.
    /// </param>
    /// <param name="rawData">The raw bytes being processed when the failure occurred, if available.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="error"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="suppressedCount"/> is negative.</exception>
    public DeviceErrorEventArgs(
        DeviceErrorSource source,
        Exception error,
        int suppressedCount = 0,
        byte[]? rawData = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (suppressedCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(suppressedCount), suppressedCount, "Suppressed count cannot be negative.");
        }

        Source = source;
        Error = error;
        SuppressedCount = suppressedCount;
        RawData = rawData;
        Timestamp = DateTime.UtcNow;
    }

    /// <summary>
    /// Gets the part of the device pipeline that produced the failure.
    /// </summary>
    public DeviceErrorSource Source { get; }

    /// <summary>
    /// Gets the exception that was caught.
    /// </summary>
    public Exception Error { get; }

    /// <summary>
    /// Gets the number of like failures (same <see cref="Source"/>, same exception type) that the
    /// throttle collapsed since the previous raise, or zero if none were.
    /// </summary>
    /// <remarks>
    /// A large value is the signal that matters: it means the failure is systematic rather than a
    /// one-off, which is exactly the case that used to be invisible.
    /// </remarks>
    public int SuppressedCount { get; }

    /// <summary>
    /// Gets the raw bytes being processed when the failure occurred, if the failing stage had any.
    /// </summary>
    public byte[]? RawData { get; }

    /// <summary>
    /// Gets the UTC time at which the failure was observed.
    /// </summary>
    public DateTime Timestamp { get; }
}
