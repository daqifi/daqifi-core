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
/// The tick period defaults to 20ns (50MHz) but should be set per device from the
/// device-reported timestamp frequency (the <c>timestamp_freq</c> field of the protobuf
/// system info message, surfaced as <see cref="DaqifiDevice.TimestampFrequency"/>) via
/// <see cref="SetTimestampFrequency"/>. Devices without a configured frequency fall back
/// to the processor-wide <see cref="TickPeriod"/>; that fallback is reported by
/// <see cref="TimestampResult.UsedFallbackTickPeriod"/> and can be checked up front with
/// <see cref="HasTimestampFrequency"/>, since a device whose real clock differs from the
/// fallback otherwise produces timestamps wrong by a constant factor with no error at all.
/// </para>
/// <para>
/// Configured frequencies are device configuration rather than session state, so neither
/// <see cref="Reset"/> nor <see cref="ResetAll"/> discards them; both only clear session
/// baselines. Clear a device's frequency explicitly with <c>SetTimestampFrequency(id, 0)</c>.
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
    /// Default tick period in seconds (20 nanoseconds = 20E-9 seconds), corresponding to a
    /// 50 MHz clock.
    /// </summary>
    /// <remarks>
    /// This is a historical default, not a measurement of any shipped board: current firmware
    /// reports a 42 MHz timestamp clock, so relying on this value instead of the
    /// device-reported frequency scales every reconstructed timestamp by roughly 1.19. Always
    /// prefer <see cref="SetTimestampFrequency"/> with the device's own figure, and use
    /// <see cref="HasTimestampFrequency"/> to find out whether that has happened.
    /// </remarks>
    public const double DefaultTickPeriod = 20E-9;

    /// <summary>
    /// Default timestamp clock frequency in Hz (50 MHz), corresponding to
    /// <see cref="DefaultTickPeriod"/>. See that field for why it should not be relied on in
    /// place of the device-reported frequency.
    /// </summary>
    public const uint DefaultTimestampFrequency = 50_000_000;

    /// <summary>
    /// Maximum time in seconds between messages before a rollover is considered invalid.
    /// If a detected rollover would result in more than this time between messages,
    /// the rollover is treated as a false positive.
    /// </summary>
    private const double MaxRolloverTimeBetweenMessages = 10.0;

    private readonly ConcurrentDictionary<string, DeviceTimestampState> _deviceStates = new();
    private readonly ConcurrentDictionary<string, double> _deviceTickPeriods = new();

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
    public void SetTimestampFrequency(string deviceId, uint frequencyHz)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        if (frequencyHz == 0)
        {
            // Unknown/unreported frequency (e.g., older firmware): revert to the fallback tick period
            _deviceTickPeriods.TryRemove(deviceId, out _);
            return;
        }

        _deviceTickPeriods[deviceId] = 1.0 / frequencyHz;
    }

    /// <inheritdoc />
    public double GetTickPeriod(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        return _deviceTickPeriods.TryGetValue(deviceId, out var tickPeriod) ? tickPeriod : TickPeriod;
    }

    /// <inheritdoc />
    public bool HasTimestampFrequency(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        return _deviceTickPeriods.ContainsKey(deviceId);
    }

    /// <inheritdoc />
    public TimestampResult ProcessTimestamp(string deviceId, uint deviceTimestamp)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        // Read the device entry once so the fallback flag reported on the result cannot
        // disagree with the tick period actually used for this message.
        var hasDeviceTickPeriod = _deviceTickPeriods.TryGetValue(deviceId, out var deviceTickPeriod);
        var tickPeriod = hasDeviceTickPeriod ? deviceTickPeriod : TickPeriod;
        var usedFallbackTickPeriod = !hasDeviceTickPeriod;
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

                return TimestampResult.CreateFirstMessage(now, deviceTimestamp, usedFallbackTickPeriod);
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

            var secondsBetweenMessages = clockCyclesBetweenMessages * tickPeriod;

            // Apply sanity check for false positive rollover detection
            // If we detected rollover but the time between messages is > 10 seconds,
            // it's likely a false positive (e.g., out-of-order messages)
            if (rollover && secondsBetweenMessages > MaxRolloverTimeBetweenMessages)
            {
                // Recalculate as if no rollover occurred (going backwards in time)
                clockCyclesBetweenMessages = previousDeviceTimestamp - deviceTimestamp;
                secondsBetweenMessages = clockCyclesBetweenMessages * tickPeriod * -1;
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
                isFirstMessage: false,
                usedFallbackTickPeriod: usedFallbackTickPeriod);
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
        // Session baselines only. Device tick periods are static device configuration, not
        // session state: clearing them used to be silent (GetTickPeriod falls back to the
        // 50 MHz default while current firmware clocks at 42 MHz, scaling every reconstructed
        // timestamp by ~1.19), and forced every consumer to re-apply the frequency after each
        // reset behind its own "have I applied it yet" gate. Reset(deviceId) has always
        // preserved them; ResetAll now matches (#398 gap 3). Clear a single device's frequency
        // with SetTimestampFrequency(deviceId, 0).
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
