using System;
using Daqifi.Core.Device;
using Xunit;

namespace Daqifi.Core.Tests.Device
{
    public class TimestampGapDetectorTests
    {
        private readonly TimestampGapDetector _detector = new();

        /// <summary>
        /// Seeds the detector with <paramref name="count"/> constant deltas after the first
        /// (null) message, stabilising the EMA around <paramref name="period"/>.
        /// </summary>
        private void WarmUp(double period, int count)
        {
            _detector.IsGap(null); // first message — no prior reference
            for (var i = 0; i < count; i++)
            {
                _detector.IsGap(period);
            }
        }

        #region First-sample behavior

        [Fact]
        public void IsGap_NullDelta_ReturnsFalse()
        {
            Assert.False(_detector.IsGap(null));
        }

        [Fact]
        public void IsGap_ZeroDelta_ReturnsFalse()
        {
            Assert.False(_detector.IsGap(0.0));
        }

        [Fact]
        public void IsGap_NegativeDelta_ReturnsFalse()
        {
            Assert.False(_detector.IsGap(-5.0));
        }

        [Fact]
        public void IsGap_FirstRealDelta_SeedsEmaAndReturnsFalse()
        {
            _detector.IsGap(null);
            Assert.False(_detector.IsGap(10.0));
        }

        #endregion

        #region Steady state

        [Fact]
        public void IsGap_ConsistentCadence_NeverDetectsGap()
        {
            const double period = 0.01; // 100 Hz
            WarmUp(period, count: 10);

            for (var i = 1; i <= 50; i++)
            {
                Assert.False(_detector.IsGap(period), $"Sample {i} at steady cadence should not be a gap.");
            }
        }

        [Fact]
        public void IsGap_DeltaExactlyAtThreshold_ReturnsFalse()
        {
            const double period = 0.01;
            WarmUp(period, count: 10);

            // Equal to 2x the average is not strictly greater — not a gap.
            Assert.False(_detector.IsGap(period * 2.0));
        }

        [Fact]
        public void IsGap_MildJitter_DoesNotFalsePositive()
        {
            const double period = 0.001; // 1 kHz
            WarmUp(period, count: 20);

            var rng = new Random(42);
            for (var i = 0; i < 100; i++)
            {
                var jittered = period * (0.9 + rng.NextDouble() * 0.2); // +/-10%
                Assert.False(_detector.IsGap(jittered), $"Mild jitter at step {i} should not be a gap.");
            }
        }

        [Fact]
        public void IsGap_GraduallySlowingRate_DoesNotFalsePositive()
        {
            _detector.IsGap(null);
            _detector.IsGap(0.010);

            for (var i = 1; i <= 30; i++)
            {
                var period = 0.010 + (0.040 / 30) * i; // ramp 10 ms -> 50 ms
                Assert.False(_detector.IsGap(period), $"Gradual slowdown at step {i} should not be a gap.");
            }
        }

        #endregion

        #region Gap detection

        [Fact]
        public void IsGap_DeltaWellAboveThreshold_ReturnsTrue()
        {
            const double period = 0.01;
            WarmUp(period, count: 10);

            Assert.True(_detector.IsGap(5 * period));
        }

        [Fact]
        public void IsGap_DeltaJustAboveThreshold_ReturnsTrue()
        {
            const double period = 0.01;
            WarmUp(period, count: 20);

            Assert.True(_detector.IsGap(period * 2.01));
        }

        [Fact]
        public void IsGap_AfterGap_ResumesWithoutFalsePositives()
        {
            const double period = 0.01;
            WarmUp(period, count: 10);

            Assert.True(_detector.IsGap(0.5), "Large gap should be detected.");

            for (var i = 1; i <= 5; i++)
            {
                Assert.False(_detector.IsGap(period), $"Post-gap steady sample {i} should not be a gap.");
            }
        }

        [Fact]
        public void IsGap_AfterGap_FutureGapStillDetected()
        {
            const double period = 0.01;
            WarmUp(period, count: 10);

            Assert.True(_detector.IsGap(0.5), "First gap should be detected.");
            Assert.False(_detector.IsGap(period), "First post-gap sample re-seeds the EMA.");
            Assert.True(_detector.IsGap(period * 2.5), "A later gap should still be detected.");
        }

        #endregion

        #region Reset

        [Fact]
        public void Reset_ReturnsToUnseededState()
        {
            const double period = 0.01;
            WarmUp(period, count: 10);

            _detector.Reset();

            // Behaves as freshly created: first delta only re-seeds, cannot be a gap.
            Assert.False(_detector.IsGap(null));
            Assert.False(_detector.IsGap(period));
            Assert.False(_detector.IsGap(period), "Steady cadence after reset should not be a gap.");
        }

        [Fact]
        public void Reset_BeforeSecondDelta_DoesNotFireOnWhatWouldHaveBeenAGap()
        {
            const double period = 0.01;
            WarmUp(period, count: 10);
            _detector.Reset();

            _detector.IsGap(period); // re-seed
            // Without reset the EMA would be ~period and 0.5 would trip; but the very next after
            // a single seed compares against that seed.
            Assert.True(_detector.IsGap(0.5), "A gap after re-seed is still detectable.");
        }

        #endregion

        #region Configuration

        [Fact]
        public void Constructor_DefaultsMatchPublishedConstants()
        {
            var d = new TimestampGapDetector();
            Assert.Equal(TimestampGapDetector.DefaultGapThresholdMultiplier, d.GapThresholdMultiplier);
            Assert.Equal(TimestampGapDetector.DefaultEmaAlpha, d.EmaAlpha);
        }

        [Fact]
        public void Constructor_CustomMultiplier_ChangesSensitivity()
        {
            var loose = new TimestampGapDetector(gapThresholdMultiplier: 3.0);
            loose.IsGap(null);
            for (var i = 0; i < 10; i++) loose.IsGap(0.01);

            // 2.5x would trip the default (2.0x) detector but not a 3.0x one.
            Assert.False(loose.IsGap(0.01 * 2.5), "2.5x delta should not be a gap at a 3.0x threshold.");
            Assert.True(loose.IsGap(0.01 * 3.5), "3.5x delta should be a gap at a 3.0x threshold.");
        }

        [Theory]
        [InlineData(1.0)]   // must be strictly greater than 1
        [InlineData(0.5)]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        public void Constructor_InvalidMultiplier_Throws(double multiplier)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new TimestampGapDetector(gapThresholdMultiplier: multiplier));
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-0.1)]
        [InlineData(1.1)]
        public void Constructor_InvalidEmaAlpha_Throws(double alpha)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new TimestampGapDetector(emaAlpha: alpha));
        }

        [Fact]
        public void Constructor_BoundaryEmaAlphaOfOne_IsAllowed()
        {
            var d = new TimestampGapDetector(emaAlpha: 1.0);
            Assert.Equal(1.0, d.EmaAlpha);
        }

        #endregion
    }
}
