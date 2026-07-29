namespace Daqifi.Core.Device;

/// <summary>
/// Processes device timestamps and handles uint32 rollover scenarios.
/// This interface provides stateful timestamp processing for streaming device data,
/// maintaining per-device/session state to correctly calculate system timestamps
/// from device clock cycles.
/// </summary>
public interface ITimestampProcessor
{
    /// <summary>
    /// Gets the fallback tick period in seconds, used for devices that have no
    /// device-specific frequency set via <see cref="SetTimestampFrequency"/>.
    /// The default is 20 nanoseconds (20E-9), corresponding to a 50MHz clock.
    /// </summary>
    double TickPeriod { get; }

    /// <summary>
    /// Sets the timestamp clock frequency for a specific device, as reported by the
    /// device (e.g., the <c>timestamp_freq</c> field of the protobuf system info message).
    /// Subsequent <see cref="ProcessTimestamp"/> calls for that device use a tick period
    /// of <c>1 / frequencyHz</c> seconds.
    /// </summary>
    /// <param name="deviceId">Unique identifier for the device (e.g., serial number).</param>
    /// <param name="frequencyHz">
    /// The timestamp clock frequency in Hz. A value of zero clears any device-specific
    /// frequency and reverts the device to the fallback <see cref="TickPeriod"/>.
    /// </param>
    void SetTimestampFrequency(string deviceId, uint frequencyHz);

    /// <summary>
    /// Gets the effective tick period in seconds for a specific device.
    /// Returns the device-specific tick period if a frequency has been set via
    /// <see cref="SetTimestampFrequency"/>; otherwise returns the fallback <see cref="TickPeriod"/>.
    /// </summary>
    /// <param name="deviceId">Unique identifier for the device (e.g., serial number).</param>
    /// <returns>The tick period in seconds used for the device's timestamp calculations.</returns>
    double GetTickPeriod(string deviceId);

    /// <summary>
    /// Gets a value indicating whether a device-specific frequency has been set for a device via
    /// <see cref="SetTimestampFrequency"/>. Use this to make the otherwise-silent fallback
    /// observable: processing timestamps for a device with no configured frequency scales every
    /// reconstructed timestamp by <c>actualFrequency / </c><see cref="TickPeriod"/>'s frequency,
    /// which is very often a bug rather than an intentional default.
    /// </summary>
    /// <param name="deviceId">Unique identifier for the device (e.g., serial number).</param>
    /// <returns>
    /// <c>true</c> when the device has its own frequency configured; <c>false</c> when
    /// <see cref="GetTickPeriod"/> would fall back to <see cref="TickPeriod"/>.
    /// </returns>
    bool HasTimestampFrequency(string deviceId);

    /// <summary>
    /// Processes a device timestamp and returns the calculated system timestamp.
    /// Handles uint32 rollover detection with a 10-second sanity check to avoid
    /// false positive rollover detection.
    /// </summary>
    /// <param name="deviceId">Unique identifier for the device (e.g., serial number).</param>
    /// <param name="deviceTimestamp">The raw device timestamp in clock cycles (uint32).</param>
    /// <returns>A <see cref="TimestampResult"/> containing the calculated system timestamp and metadata.</returns>
    TimestampResult ProcessTimestamp(string deviceId, uint deviceTimestamp);

    /// <summary>
    /// Resets the timestamp state for a specific device.
    /// Call this when starting a new streaming session.
    /// Any device-specific frequency set via <see cref="SetTimestampFrequency"/> is preserved,
    /// since the timestamp clock frequency is static device configuration that does not
    /// change between streaming sessions.
    /// </summary>
    /// <param name="deviceId">Unique identifier for the device to reset.</param>
    void Reset(string deviceId);

    /// <summary>
    /// Resets the session baselines for every device. Call this when all streaming sessions
    /// should start over.
    /// </summary>
    /// <remarks>
    /// Like <see cref="Reset"/>, device-specific frequencies set via
    /// <see cref="SetTimestampFrequency"/> are preserved: the timestamp clock frequency is static
    /// device configuration, not session state, and dropping it silently reverts the device to
    /// <see cref="TickPeriod"/> and rescales every reconstructed timestamp. Clear a single
    /// device's frequency explicitly with <c>SetTimestampFrequency(deviceId, 0)</c>.
    /// </remarks>
    void ResetAll();
}
