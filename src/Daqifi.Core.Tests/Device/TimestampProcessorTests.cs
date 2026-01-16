using Daqifi.Core.Device;

namespace Daqifi.Core.Tests.Device;

public class TimestampProcessorTests
{
    private const string TestDeviceId = "test-device-001";

    #region Constructor Tests

    [Fact]
    public void Constructor_Default_UsesDefaultTickPeriod()
    {
        // Arrange & Act
        var processor = new TimestampProcessor();

        // Assert
        Assert.Equal(TimestampProcessor.DefaultTickPeriod, processor.TickPeriod);
    }

    [Fact]
    public void Constructor_WithCustomTickPeriod_SetsTickPeriod()
    {
        // Arrange
        const double customTickPeriod = 10E-9;

        // Act
        var processor = new TimestampProcessor(customTickPeriod);

        // Assert
        Assert.Equal(customTickPeriod, processor.TickPeriod);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1E-9)]
    [InlineData(-100)]
    public void Constructor_WithInvalidTickPeriod_ThrowsException(double invalidTickPeriod)
    {
        // Arrange, Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimestampProcessor(invalidTickPeriod));
    }

    #endregion

    #region First Message Tests

    [Fact]
    public void ProcessTimestamp_FirstMessage_ReturnsIsFirstMessage()
    {
        // Arrange
        var processor = new TimestampProcessor();

        // Act
        var result = processor.ProcessTimestamp(TestDeviceId, 1000);

        // Assert
        Assert.True(result.IsFirstMessage);
        Assert.False(result.WasRollover);
        Assert.Equal(0u, result.ClockCyclesBetweenMessages);
        Assert.Equal(0.0, result.SecondsBetweenMessages);
    }

    [Fact]
    public void ProcessTimestamp_FirstMessage_TimestampIsNow()
    {
        // Arrange
        var processor = new TimestampProcessor();
        var before = DateTime.Now;

        // Act
        var result = processor.ProcessTimestamp(TestDeviceId, 1000);
        var after = DateTime.Now;

        // Assert
        Assert.InRange(result.Timestamp, before, after);
    }

    #endregion

    #region Normal Timestamp Processing Tests

    [Fact]
    public void ProcessTimestamp_SecondMessage_CalculatesElapsedTimeCorrectly()
    {
        // Arrange
        var processor = new TimestampProcessor();
        const uint firstTimestamp = 1000;
        const uint secondTimestamp = 50_000_000; // 50 million cycles = 1 second at 20ns tick

        // Act
        var firstResult = processor.ProcessTimestamp(TestDeviceId, firstTimestamp);
        var secondResult = processor.ProcessTimestamp(TestDeviceId, secondTimestamp);

        // Assert
        Assert.False(secondResult.IsFirstMessage);
        Assert.False(secondResult.WasRollover);
        Assert.Equal(secondTimestamp - firstTimestamp, secondResult.ClockCyclesBetweenMessages);

        // Calculate expected seconds: (50_000_000 - 1000) * 20E-9 ≈ 0.99998 seconds
        var expectedSeconds = (secondTimestamp - firstTimestamp) * TimestampProcessor.DefaultTickPeriod;
        Assert.Equal(expectedSeconds, secondResult.SecondsBetweenMessages, precision: 6);
    }

    [Fact]
    public void ProcessTimestamp_MultipleMessages_AccumulatesTimeCorrectly()
    {
        // Arrange
        var processor = new TimestampProcessor();
        const uint ticksPerSecond = 50_000_000; // 1 second at 20ns

        // Act
        var first = processor.ProcessTimestamp(TestDeviceId, 0);
        var second = processor.ProcessTimestamp(TestDeviceId, ticksPerSecond);
        var third = processor.ProcessTimestamp(TestDeviceId, ticksPerSecond * 2);

        // Assert
        var elapsed1to2 = (second.Timestamp - first.Timestamp).TotalSeconds;
        var elapsed2to3 = (third.Timestamp - second.Timestamp).TotalSeconds;

        Assert.Equal(1.0, elapsed1to2, precision: 2);
        Assert.Equal(1.0, elapsed2to3, precision: 2);
    }

    #endregion

    #region Rollover Detection Tests

    [Fact]
    public void ProcessTimestamp_DetectsRollover_WhenTimestampWrapsAround()
    {
        // Arrange
        var processor = new TimestampProcessor();
        const uint nearMax = uint.MaxValue - 1_000_000; // Just under max
        const uint afterRollover = 1_000_000; // Just after rollover

        // Act
        processor.ProcessTimestamp(TestDeviceId, nearMax);
        var result = processor.ProcessTimestamp(TestDeviceId, afterRollover);

        // Assert
        Assert.True(result.WasRollover);
    }

    [Fact]
    public void ProcessTimestamp_Rollover_CalculatesCorrectClockCycles()
    {
        // Arrange
        var processor = new TimestampProcessor();
        const uint nearMax = uint.MaxValue - 1000;
        const uint afterRollover = 1000;
        // Expected cycles: (uint.MaxValue - nearMax) + afterRollover = 1000 + 1000 = 2000
        const uint expectedCycles = 2000u;

        // Act
        processor.ProcessTimestamp(TestDeviceId, nearMax);
        var result = processor.ProcessTimestamp(TestDeviceId, afterRollover);

        // Assert
        Assert.Equal(expectedCycles, result.ClockCyclesBetweenMessages);
    }

    [Fact]
    public void ProcessTimestamp_Rollover_CalculatesCorrectTime()
    {
        // Arrange
        var processor = new TimestampProcessor();
        const uint nearMax = uint.MaxValue - 25_000_000; // 0.5 seconds before max
        const uint afterRollover = 25_000_000; // 0.5 seconds after rollover
        // Total: ~1 second

        // Act
        processor.ProcessTimestamp(TestDeviceId, nearMax);
        var result = processor.ProcessTimestamp(TestDeviceId, afterRollover);

        // Assert
        // Expected: (uint.MaxValue - nearMax + afterRollover + 1) * 20E-9 ≈ 1.0 seconds
        Assert.True(result.WasRollover);
        Assert.InRange(result.SecondsBetweenMessages, 0.9, 1.1);
    }

    #endregion

    #region False Positive Rollover (Sanity Check) Tests

    [Fact]
    public void ProcessTimestamp_FalsePositiveRollover_NegativeTimeWhenExceeds10Seconds()
    {
        // Arrange
        var processor = new TimestampProcessor();
        // Set up a scenario where detected rollover would yield > 10 seconds
        // This simulates out-of-order messages
        const uint firstTimestamp = 1_000_000_000u;
        const uint secondTimestamp = 100_000_000u; // Lower, appears as rollover

        // At 20ns tick, uint.MaxValue cycles = ~85.9 seconds
        // So going from 1B to 100M with rollover would be:
        // (uint.MaxValue - 1B) + 100M ≈ 3.4B cycles ≈ 68 seconds > 10 seconds

        // Act
        processor.ProcessTimestamp(TestDeviceId, firstTimestamp);
        var result = processor.ProcessTimestamp(TestDeviceId, secondTimestamp);

        // Assert
        Assert.True(result.WasRollover); // Rollover was detected
        Assert.True(result.SecondsBetweenMessages < 0); // But time is negative (sanity check applied)
    }

    [Fact]
    public void ProcessTimestamp_ValidRollover_PositiveTimeWhenUnder10Seconds()
    {
        // Arrange
        var processor = new TimestampProcessor();
        // Set up a valid rollover scenario that yields < 10 seconds
        const uint nearMax = uint.MaxValue - 50_000_000; // 1 second before max
        const uint afterRollover = 50_000_000; // 1 second after rollover

        // Act
        processor.ProcessTimestamp(TestDeviceId, nearMax);
        var result = processor.ProcessTimestamp(TestDeviceId, afterRollover);

        // Assert
        Assert.True(result.WasRollover);
        Assert.True(result.SecondsBetweenMessages > 0);
        Assert.InRange(result.SecondsBetweenMessages, 1.5, 2.5); // Approximately 2 seconds
    }

    #endregion

    #region Multi-Device Tests

    [Fact]
    public void ProcessTimestamp_MultipleDevices_TrackedIndependently()
    {
        // Arrange
        var processor = new TimestampProcessor();
        const string device1 = "device-001";
        const string device2 = "device-002";

        // Act
        var device1First = processor.ProcessTimestamp(device1, 1000);
        var device2First = processor.ProcessTimestamp(device2, 2000);
        var device1Second = processor.ProcessTimestamp(device1, 50_000_000);
        var device2Second = processor.ProcessTimestamp(device2, 100_000_000);

        // Assert
        Assert.True(device1First.IsFirstMessage);
        Assert.True(device2First.IsFirstMessage);
        Assert.False(device1Second.IsFirstMessage);
        Assert.False(device2Second.IsFirstMessage);

        // Device 1: 50M - 1K cycles
        Assert.Equal(50_000_000u - 1000u, device1Second.ClockCyclesBetweenMessages);
        // Device 2: 100M - 2K cycles
        Assert.Equal(100_000_000u - 2000u, device2Second.ClockCyclesBetweenMessages);
    }

    [Fact]
    public void ProcessTimestamp_MultipleDevices_ResetOneDoesNotAffectOther()
    {
        // Arrange
        var processor = new TimestampProcessor();
        const string device1 = "device-001";
        const string device2 = "device-002";

        processor.ProcessTimestamp(device1, 1000);
        processor.ProcessTimestamp(device2, 2000);

        // Act
        processor.Reset(device1);
        var device1AfterReset = processor.ProcessTimestamp(device1, 5000);
        var device2AfterReset = processor.ProcessTimestamp(device2, 50_000_000);

        // Assert
        Assert.True(device1AfterReset.IsFirstMessage); // Reset, so first message
        Assert.False(device2AfterReset.IsFirstMessage); // Not reset, continues
    }

    #endregion

    #region Reset Tests

    [Fact]
    public void Reset_ClearsDeviceState()
    {
        // Arrange
        var processor = new TimestampProcessor();
        processor.ProcessTimestamp(TestDeviceId, 1000);
        processor.ProcessTimestamp(TestDeviceId, 2000);

        // Act
        processor.Reset(TestDeviceId);
        var resultAfterReset = processor.ProcessTimestamp(TestDeviceId, 3000);

        // Assert
        Assert.True(resultAfterReset.IsFirstMessage);
    }

    [Fact]
    public void Reset_NonExistentDevice_DoesNotThrow()
    {
        // Arrange
        var processor = new TimestampProcessor();

        // Act & Assert
        var exception = Record.Exception(() => processor.Reset("non-existent-device"));
        Assert.Null(exception);
    }

    [Fact]
    public void ResetAll_ClearsAllDeviceStates()
    {
        // Arrange
        var processor = new TimestampProcessor();
        processor.ProcessTimestamp("device-001", 1000);
        processor.ProcessTimestamp("device-002", 2000);
        processor.ProcessTimestamp("device-003", 3000);

        // Act
        processor.ResetAll();

        // Assert
        Assert.True(processor.ProcessTimestamp("device-001", 4000).IsFirstMessage);
        Assert.True(processor.ProcessTimestamp("device-002", 5000).IsFirstMessage);
        Assert.True(processor.ProcessTimestamp("device-003", 6000).IsFirstMessage);
    }

    #endregion

    #region Thread Safety Tests

    [Fact]
    public void ProcessTimestamp_IsThreadSafe()
    {
        // Arrange
        var processor = new TimestampProcessor();
        var tasks = new List<Task>();
        var results = new System.Collections.Concurrent.ConcurrentBag<TimestampResult>();

        // Act - Process 100 timestamps in parallel on the same device
        for (int i = 0; i < 100; i++)
        {
            var timestamp = (uint)(i * 1000);
            tasks.Add(Task.Run(() =>
            {
                var result = processor.ProcessTimestamp(TestDeviceId, timestamp);
                results.Add(result);
            }));
        }

        Task.WaitAll(tasks.ToArray());

        // Assert - All tasks completed without exception
        Assert.Equal(100, results.Count);
        Assert.Single(results.Where(r => r.IsFirstMessage)); // Only one first message
    }

    [Fact]
    public void ProcessTimestamp_MultipleDevicesInParallel_IsThreadSafe()
    {
        // Arrange
        var processor = new TimestampProcessor();
        var tasks = new List<Task>();
        var results = new System.Collections.Concurrent.ConcurrentDictionary<string, List<TimestampResult>>();

        // Act - Process timestamps for 10 devices in parallel
        for (int deviceNum = 0; deviceNum < 10; deviceNum++)
        {
            var deviceId = $"device-{deviceNum:D3}";
            results[deviceId] = new List<TimestampResult>();

            for (int msgNum = 0; msgNum < 10; msgNum++)
            {
                var timestamp = (uint)(msgNum * 1_000_000);
                var localDeviceId = deviceId;
                tasks.Add(Task.Run(() =>
                {
                    var result = processor.ProcessTimestamp(localDeviceId, timestamp);
                    lock (results[localDeviceId])
                    {
                        results[localDeviceId].Add(result);
                    }
                }));
            }
        }

        Task.WaitAll(tasks.ToArray());

        // Assert - All devices have 10 results
        Assert.Equal(10, results.Count);
        foreach (var deviceResults in results.Values)
        {
            Assert.Equal(10, deviceResults.Count);
        }
    }

    #endregion

    #region Null Parameter Tests

    [Fact]
    public void ProcessTimestamp_NullDeviceId_ThrowsArgumentNullException()
    {
        // Arrange
        var processor = new TimestampProcessor();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => processor.ProcessTimestamp(null!, 1000));
    }

    [Fact]
    public void Reset_NullDeviceId_ThrowsArgumentNullException()
    {
        // Arrange
        var processor = new TimestampProcessor();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => processor.Reset(null!));
    }

    #endregion

    #region TimestampResult Tests

    [Fact]
    public void TimestampResult_PropertiesAreSetCorrectly()
    {
        // Arrange
        var timestamp = DateTime.Now;
        const bool wasRollover = true;
        const uint clockCycles = 12345u;
        const double seconds = 0.00024690;
        const bool isFirstMessage = false;

        // Act
        var result = new TimestampResult(timestamp, wasRollover, clockCycles, seconds, isFirstMessage);

        // Assert
        Assert.Equal(timestamp, result.Timestamp);
        Assert.Equal(wasRollover, result.WasRollover);
        Assert.Equal(clockCycles, result.ClockCyclesBetweenMessages);
        Assert.Equal(seconds, result.SecondsBetweenMessages);
        Assert.Equal(isFirstMessage, result.IsFirstMessage);
    }

    #endregion

    #region Edge Case Tests

    [Fact]
    public void ProcessTimestamp_ZeroTimestamp_HandledCorrectly()
    {
        // Arrange
        var processor = new TimestampProcessor();

        // Act
        var result = processor.ProcessTimestamp(TestDeviceId, 0);

        // Assert
        Assert.True(result.IsFirstMessage);
    }

    [Fact]
    public void ProcessTimestamp_MaxTimestamp_HandledCorrectly()
    {
        // Arrange
        var processor = new TimestampProcessor();

        // Act
        var result = processor.ProcessTimestamp(TestDeviceId, uint.MaxValue);

        // Assert
        Assert.True(result.IsFirstMessage);
    }

    [Fact]
    public void ProcessTimestamp_SameTimestampTwice_ZeroElapsedTime()
    {
        // Arrange
        var processor = new TimestampProcessor();
        const uint timestamp = 50_000_000;

        // Act
        processor.ProcessTimestamp(TestDeviceId, timestamp);
        var result = processor.ProcessTimestamp(TestDeviceId, timestamp);

        // Assert
        Assert.Equal(0u, result.ClockCyclesBetweenMessages);
        Assert.Equal(0.0, result.SecondsBetweenMessages);
    }

    [Fact]
    public void ProcessTimestamp_EmptyDeviceId_HandledCorrectly()
    {
        // Arrange
        var processor = new TimestampProcessor();

        // Act
        var result = processor.ProcessTimestamp("", 1000);

        // Assert
        Assert.True(result.IsFirstMessage);
    }

    #endregion
}
