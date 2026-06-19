using System;
using Daqifi.Core.Device.SdCard;
using Xunit;

namespace Daqifi.Core.Tests.Device.SdCard
{
    public class SdCardCaptureEstimateTests
    {
        [Fact]
        public void Constructor_ComputesBytesPerSecondAndEstimatedBytes()
        {
            // 1000 Hz × 4 channels × 2 bytes = 8000 B/s; over 60 s = 480000 B.
            var estimate = new SdCardCaptureEstimate(1000, 4, TimeSpan.FromSeconds(60), bytesPerSamplePerChannel: 2);

            Assert.Equal(8000, estimate.BytesPerSecond);
            Assert.Equal(480000, estimate.EstimatedBytes);
        }

        [Fact]
        public void Constructor_DefaultsBytesPerSampleToRawAdcWidth()
        {
            var estimate = new SdCardCaptureEstimate(100, 1, TimeSpan.FromSeconds(1));

            Assert.Equal(SdCardCaptureEstimate.DefaultBytesPerSamplePerChannel, estimate.BytesPerSamplePerChannel);
            Assert.Equal(2, estimate.BytesPerSamplePerChannel);
            Assert.Equal(200, estimate.BytesPerSecond);
        }

        [Theory]
        [InlineData(0, 1, 1)]
        [InlineData(-1, 1, 1)]
        [InlineData(100, 0, 1)]
        [InlineData(100, -1, 1)]
        [InlineData(100, 1, 0)]
        [InlineData(100, 1, -1)]
        public void Constructor_WithNonPositiveArgument_Throws(int frequencyHz, int channelCount, int bytesPerSample)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new SdCardCaptureEstimate(frequencyHz, channelCount, TimeSpan.FromSeconds(1), bytesPerSample));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void Constructor_WithNonPositiveDuration_Throws(int seconds)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new SdCardCaptureEstimate(100, 1, TimeSpan.FromSeconds(seconds)));
        }

        [Fact]
        public void EstimatedBytes_WhenOverflowing_ClampsToMaxValue()
        {
            // An astronomically long capture at high rate would overflow a long; the property clamps instead.
            // BytesPerSecond ≈ 2.6e8 × TimeSpan.MaxValue (~9.2e11 s) ≈ 2.4e20 > long.MaxValue (~9.2e18).
            var estimate = new SdCardCaptureEstimate(1_000_000, 32, TimeSpan.MaxValue, bytesPerSamplePerChannel: 8);

            Assert.Equal(long.MaxValue, estimate.EstimatedBytes);
        }
    }
}
