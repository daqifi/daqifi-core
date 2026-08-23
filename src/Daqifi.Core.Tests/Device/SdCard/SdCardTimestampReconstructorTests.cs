using System;
using Daqifi.Core.Device.SdCard;
using Xunit;

namespace Daqifi.Core.Tests.Device.SdCard;

/// <summary>
/// Direct unit tests for <see cref="SdCardTimestampReconstructor.ForTimestampFrequency"/> — the
/// frequency-to-tick-period conversion previously re-derived identically by the CSV, JSON, and
/// binary/protobuf SD card log parsers.
/// </summary>
public class SdCardTimestampReconstructorTests
{
    [Fact]
    public void ForTimestampFrequency_WithKnownFrequency_ReportsADeviceClock()
    {
        var reconstructor = SdCardTimestampReconstructor.ForTimestampFrequency(50_000_000u);

        Assert.True(reconstructor.HasDeviceClock);
    }

    [Fact]
    public void ForTimestampFrequency_WithUnknownFrequency_ReportsNoDeviceClock()
    {
        // Zero means "the log never said" — it has to land on an unknown tick period rather than
        // dividing by zero and reconstructing every sample at an infinite offset.
        var reconstructor = SdCardTimestampReconstructor.ForTimestampFrequency(0u);

        Assert.False(reconstructor.HasDeviceClock);
    }

    [Fact]
    public void ForTimestampFrequency_UsesTheReciprocalOfTheFrequencyAsTheTickPeriod()
    {
        var reconstructor = SdCardTimestampReconstructor.ForTimestampFrequency(50_000_000u);
        var baseTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(baseTime, reconstructor.Advance(0u, baseTime));

        // One second's worth of ticks at 50 MHz has to reconstruct as one second elapsed.
        var oneSecondLater = reconstructor.Advance(50_000_000u, baseTime);

        Assert.Equal(1.0, (oneSecondLater - baseTime).TotalSeconds, 6);
    }
}
