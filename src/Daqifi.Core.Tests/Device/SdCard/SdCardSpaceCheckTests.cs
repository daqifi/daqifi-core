using System;
using Daqifi.Core.Device.SdCard;
using Xunit;

namespace Daqifi.Core.Tests.Device.SdCard
{
    public class SdCardSpaceCheckTests
    {
        private const long FourGib = 4L * 1024 * 1024 * 1024;
        private const long OneHundredMib = 100L * 1024 * 1024;

        [Fact]
        public void DefaultMinimumFreeBytes_Is100Mb()
        {
            Assert.Equal(OneHundredMib, SdCardSpaceCheck.DefaultMinimumFreeBytes);
        }

        [Fact]
        public void Evaluate_WhenPlentyOfSpaceAndNoEstimate_DoesNotWarn()
        {
            var storage = new SdCardStorageInfo(FreeBytes: 2L * 1024 * 1024 * 1024, TotalBytes: FourGib);

            var result = SdCardSpaceCheck.Evaluate(storage);

            Assert.False(result.ShouldWarn);
            Assert.False(result.IsNearlyFull);
            Assert.False(result.IsInsufficientForCapture);
            Assert.Null(result.EstimatedCaptureBytes);
            Assert.Null(result.EstimatedTimeUntilFull);
            Assert.Null(result.Message);
        }

        [Fact]
        public void Evaluate_WhenBelowMinimum_FlagsNearlyFull()
        {
            // 50 MB free — below the 100 MB default floor.
            var storage = new SdCardStorageInfo(FreeBytes: 50L * 1024 * 1024, TotalBytes: FourGib);

            var result = SdCardSpaceCheck.Evaluate(storage);

            Assert.True(result.ShouldWarn);
            Assert.True(result.IsNearlyFull);
            Assert.False(result.IsInsufficientForCapture);
            Assert.NotNull(result.Message);
            Assert.Contains("nearly full", result.Message);
        }

        [Fact]
        public void Evaluate_WhenCaptureWontFit_FlagsInsufficientWithTruncationEta()
        {
            // 200 MB free; capture estimate ~220 MB (8 h at 8000 B/s) won't fit.
            var storage = new SdCardStorageInfo(FreeBytes: 200L * 1024 * 1024, TotalBytes: FourGib);
            var estimate = new SdCardCaptureEstimate(1000, 4, TimeSpan.FromHours(8), bytesPerSamplePerChannel: 2);

            var result = SdCardSpaceCheck.Evaluate(storage, estimate);

            Assert.True(result.ShouldWarn);
            Assert.False(result.IsNearlyFull);
            Assert.True(result.IsInsufficientForCapture);
            Assert.Equal(estimate.EstimatedBytes, result.EstimatedCaptureBytes);
            Assert.NotNull(result.EstimatedTimeUntilFull);
            // free / rate = (200 * 1024 * 1024) / 8000 ≈ 26214 s.
            Assert.Equal(
                (double)(200L * 1024 * 1024) / 8000,
                result.EstimatedTimeUntilFull!.Value.TotalSeconds,
                precision: 0);
            Assert.Contains("will not fit", result.Message);
            Assert.Contains("truncating", result.Message);
        }

        [Fact]
        public void Evaluate_WhenCaptureFitsAndSpaceAmple_DoesNotWarn()
        {
            // 2 GB free; capture estimate ~28 MB (1 h at 8000 B/s) fits comfortably.
            var storage = new SdCardStorageInfo(FreeBytes: 2L * 1024 * 1024 * 1024, TotalBytes: FourGib);
            var estimate = new SdCardCaptureEstimate(1000, 4, TimeSpan.FromHours(1), bytesPerSamplePerChannel: 2);

            var result = SdCardSpaceCheck.Evaluate(storage, estimate);

            Assert.False(result.ShouldWarn);
            Assert.False(result.IsNearlyFull);
            Assert.False(result.IsInsufficientForCapture);
            Assert.Equal(estimate.EstimatedBytes, result.EstimatedCaptureBytes);
            // The time-until-full figure is still reported for informational use.
            Assert.NotNull(result.EstimatedTimeUntilFull);
            Assert.Null(result.Message);
        }

        [Fact]
        public void Evaluate_WhenBothNearlyFullAndWontFit_FlagsBothInMessage()
        {
            // 50 MB free (below floor) and a 1 h capture (~28 MB)... that fits. Use a bigger capture so both trip.
            var storage = new SdCardStorageInfo(FreeBytes: 50L * 1024 * 1024, TotalBytes: FourGib);
            var estimate = new SdCardCaptureEstimate(1000, 4, TimeSpan.FromHours(4), bytesPerSamplePerChannel: 2);

            var result = SdCardSpaceCheck.Evaluate(storage, estimate);

            Assert.True(result.ShouldWarn);
            Assert.True(result.IsNearlyFull);
            Assert.True(result.IsInsufficientForCapture);
            Assert.Contains("will not fit", result.Message);
            Assert.Contains("nearly full", result.Message);
        }

        [Fact]
        public void Evaluate_RespectsCustomMinimumFreeBytes()
        {
            // 200 MB free is fine under the 100 MB default but flagged when the floor is raised to 500 MB.
            var storage = new SdCardStorageInfo(FreeBytes: 200L * 1024 * 1024, TotalBytes: FourGib);

            var result = SdCardSpaceCheck.Evaluate(storage, minimumFreeBytes: 500L * 1024 * 1024);

            Assert.True(result.IsNearlyFull);
            Assert.True(result.ShouldWarn);
        }

        [Fact]
        public void Evaluate_WithNullStorage_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => SdCardSpaceCheck.Evaluate(null!));
        }

        [Fact]
        public void Evaluate_WithNegativeMinimum_Throws()
        {
            var storage = new SdCardStorageInfo(FreeBytes: 1024, TotalBytes: FourGib);

            Assert.Throws<ArgumentOutOfRangeException>(
                () => SdCardSpaceCheck.Evaluate(storage, minimumFreeBytes: -1));
        }
    }
}
