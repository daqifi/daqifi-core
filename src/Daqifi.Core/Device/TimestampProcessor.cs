using System.Collections.Concurrent;

namespace Daqifi.Core.Device;

/// <summary>
/// Processes device timestamps and handles uint32 rollover scenarios.
/// This class maintains per-device state to correctly calculate system timestamps
/// from device clock cycles during streaming.
/// </summary>
/// <remarks>
/// <para>
/// Device timestamps are 32-bit unsigned integers representing clock cycles.
/// At 50MHz (20ns tick period), the counter rolls over approximately every 85.9 seconds.
/// This processor detects rollover and calculates accurate elapsed time.
/// </para>
/// <para>
/// A 10-second sanity check is applied to detected rollovers. If the calculated
/// time between messages exceeds 10 seconds after rollover correction, the rollover
/// is considered a false positive (likely caused by out-of-order messages).
/// </para>
/// <para>
/// This class is thread-safe and can be used with multiple devices simultaneously.
/// </para>
/// </remarks>
public sealed class TimestampProcessor : ITimestampProcessor
{
    /// <summary>
    /// Default tick period in seconds (20 nanoseconds = 20E-9 seconds).
    /// This corresponds to a 50MHz clock.
    /// </summary>
    public const double DefaultTickPeriod = 20E-9;

    /// <summary>
    /// Maximum time in seconds between messages before a rollover is considered invalid.
    /// If a detected rollover would result in more than this time between messages,
    /// the rollover is treated as a false positive.
    /// </summary>
    private const double MaxRolloverTimeBetweenMessages = 10.0;

    private readonly ConcurrentDictionary<string, DeviceTimestampState> _deviceStates = new();

    /// <inheritdoc />
    public double TickPeriod { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="TimestampProcessor"/> class
    /// with the default tick period of 20 nanoseconds.
    /// </summary>
    public TimestampProcessor() : this(DefaultTickPeriod)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TimestampProcessor"/> class
    /// with a custom tick period.
    /// </summary>
    /// <param name="tickPeriod">The tick period in seconds.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="tickPeriod"/> is less than or equal to zero.
    /// </exception>
    public TimestampProcessor(double tickPeriod)
    {
        if (tickPeriod <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tickPeriod), "Tick period must be greater than zero.");
        }

        TickPeriod = tickPeriod;
    }

    /// <inheritdoc />
    public TimestampResult ProcessTimestamp(string deviceId, uint deviceTimestamp)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        var state = _deviceStates.GetOrAdd(deviceId, _ => new DeviceTimestampState());

        lock (state.SyncLock)
        {
            // First message for this device
            if (!state.HasPreviousTimestamp)
            {
                var now = DateTime.Now;
                state.PreviousSystemTimestamp = now;
                state.PreviousDeviceTimestamp = deviceTimestamp;
                state.HasPreviousTimestamp = true;

                return TimestampResult.CreateFirstMessage(now, deviceTimestamp);
            }

            // Calculate clock cycles between messages, handling rollover
            var previousDeviceTimestamp = state.PreviousDeviceTimestamp;
            var rollover = previousDeviceTimestamp > deviceTimestamp;
            uint clockCyclesBetweenMessages;

            if (rollover)
            {
                // Rollover detected: timestamp wrapped from uint.MaxValue to 0
                var cyclesToMax = uint.MaxValue - previousDeviceTimestamp;
                clockCyclesBetweenMessages = cyclesToMax + deviceTimestamp;
            }
            else
            {
                clockCyclesBetweenMessages = deviceTimestamp - previousDeviceTimestamp;
            }

            var secondsBetweenMessages = clockCyclesBetweenMessages * TickPeriod;

            // Apply sanity check for false positive rollover detection
            // If we detected rollover but the time between messages is > 10 seconds,
            // it's likely a false positive (e.g., out-of-order messages)
            if (rollover && secondsBetweenMessages > MaxRolloverTimeBetweenMessages)
            {
                // Recalculate as if no rollover occurred (going backwards in time)
                clockCyclesBetweenMessages = previousDeviceTimestamp - deviceTimestamp;
                secondsBetweenMessages = clockCyclesBetweenMessages * TickPeriod * -1;
            }

            var messageTimestamp = state.PreviousSystemTimestamp.AddSeconds(secondsBetweenMessages);

            // Update state for next message
            state.PreviousSystemTimestamp = messageTimestamp;
            state.PreviousDeviceTimestamp = deviceTimestamp;

            return new TimestampResult(
                timestamp: messageTimestamp,
                wasRollover: rollover,
                clockCyclesBetweenMessages: clockCyclesBetweenMessages,
                secondsBetweenMessages: secondsBetweenMessages,
                isFirstMessage: false);
        }
    }

    /// <inheritdoc />
    public void Reset(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        _deviceStates.TryRemove(deviceId, out _);
    }

    /// <inheritdoc />
    public void ResetAll()
    {
        _deviceStates.Clear();
    }

    /// <summary>
    /// Internal state tracking for a single device.
    /// </summary>
    private sealed class DeviceTimestampState
    {
        public readonly object SyncLock = new();
        public DateTime PreviousSystemTimestamp;
        public uint PreviousDeviceTimestamp;
        public bool HasPreviousTimestamp;
    }
}
