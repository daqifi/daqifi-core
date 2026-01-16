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
    /// Gets the tick period in seconds. The default is 20 nanoseconds (20E-9).
    /// </summary>
    double TickPeriod { get; }

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
    /// </summary>
    /// <param name="deviceId">Unique identifier for the device to reset.</param>
    void Reset(string deviceId);

    /// <summary>
    /// Resets all timestamp state for all devices.
    /// Call this when the processor should be completely cleared.
    /// </summary>
    void ResetAll();
}
