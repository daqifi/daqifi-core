using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Xunit;

namespace Daqifi.Core.Tests.Device
{
    public class AcquisitionStatisticsTests
    {
        /// <summary>
        /// The host-clock reading every test starts from. Local kind on purpose: the timestamps these
        /// statistics measure against the host clock are local (see <c>TimestampProcessor</c>), and a
        /// UTC-kind fixture would quietly compare two different time bases.
        /// </summary>
        private static readonly DateTime Epoch = new DateTime(2026, 8, 13, 9, 0, 0, DateTimeKind.Local);

        #region Value statistics

        [Fact]
        public void Snapshot_WithNoSamples_IsEmptyButStillReportsWhenTheWindowStarted()
        {
            var clock = new TestClock(Epoch);
            using var stats = new AcquisitionStatistics(null, clock.Read);

            var snapshot = stats.Snapshot();

            Assert.Equal(Epoch, snapshot.StartedAt);
            Assert.Equal(0, snapshot.TotalSampleCount);
            Assert.Empty(snapshot.Channels);
            Assert.Equal(TimeSpan.Zero, snapshot.Duration);
            Assert.Equal(TimeSpan.Zero, snapshot.MeanLatency);
        }

        [Fact]
        public void Record_TracksCountAndValueExtremesAndMean()
        {
            var clock = new TestClock(Epoch);
            using var stats = new AcquisitionStatistics(null, clock.Read);
            var channel = new AnalogChannel(0) { Name = "AI0" };

            foreach (var value in new[] { 1.0, 3.0, 2.0 })
            {
                stats.Record(channel, Sample(clock.Now, value));
                clock.Advance(TimeSpan.FromMilliseconds(1));
            }

            var ai0 = Assert.Single(stats.Snapshot().Channels);
            Assert.Equal(ChannelType.Analog, ai0.ChannelType);
            Assert.Equal(0, ai0.ChannelNumber);
            Assert.Equal("AI0", ai0.Name);
            Assert.Equal(3, ai0.SampleCount);
            Assert.Equal(1.0, ai0.MinValue);
            Assert.Equal(3.0, ai0.MaxValue);
            Assert.Equal(2.0, ai0.MeanValue, 9);
        }

        [Fact]
        public void Record_SeedsValueExtremesFromTheFirstSample_NotFromZero()
        {
            // The defect this port fixes: desktop's SummaryLogger left min/max at their default 0 on
            // the first sample, so a channel sitting at 4.5 V reported a minimum of 0 V forever.
            var clock = new TestClock(Epoch);
            using var stats = new AcquisitionStatistics(null, clock.Read);
            var channel = new AnalogChannel(0);

            stats.Record(channel, Sample(clock.Now, 4.4));
            clock.Advance(TimeSpan.FromMilliseconds(1));
            stats.Record(channel, Sample(clock.Now, 4.6));

            var ai0 = Assert.Single(stats.Snapshot().Channels);
            Assert.Equal(4.4, ai0.MinValue);
            Assert.Equal(4.6, ai0.MaxValue);
            Assert.Equal(4.5, ai0.MeanValue, 9);
        }

        [Fact]
        public void Record_MeanValue_DividesByTheSamplesActuallySeen()
        {
            // Desktop divided by a configured window size instead, so a window that never filled
            // under-reported its mean by whatever fraction was missing.
            var clock = new TestClock(Epoch);
            using var stats = new AcquisitionStatistics(null, clock.Read);
            var channel = new AnalogChannel(0);

            stats.Record(channel, Sample(clock.Now, 10.0));
            clock.Advance(TimeSpan.FromMilliseconds(1));
            stats.Record(channel, Sample(clock.Now, 20.0));

            Assert.Equal(15.0, Assert.Single(stats.Snapshot().Channels).MeanValue, 9);
        }

        #endregion

        #region Rate and jitter

        [Fact]
        public void Record_MeasuredRateAndDeviceClockRate_AgreeWhenTheDeviceClockKeepsRealTime()
        {
            var clock = new TestClock(Epoch);
            using var stats = new AcquisitionStatistics(null, clock.Read);
            var channel = new AnalogChannel(0);

            // 1 kHz: one sample every millisecond, on both clocks.
            for (var i = 0; i < 1000; i++)
            {
                stats.Record(channel, Sample(clock.Now, 1.0));
                clock.Advance(TimeSpan.FromMilliseconds(1));
            }

            var ai0 = Assert.Single(stats.Snapshot().Channels);
            Assert.Equal(1000.0, ai0.MeasuredSampleRateHz, 6);
            Assert.Equal(1000.0, ai0.DeviceClockSampleRateHz, 6);
        }

        [Fact]
        public void Record_MeasuredRate_FollowsTheHostClock_WhenTheDeviceClockRunsSlow()
        {
            // Reproduces what a device whose timestamps do not track real time looks like
            // (daqifi-nyquist-firmware #716): the device stamps a millisecond per sample while the
            // host sees 1.25 ms pass. The device-clock rate still claims 1 kHz; the measured rate is
            // what the caller actually received, and their disagreement is the diagnosis.
            var clock = new TestClock(Epoch);
            using var stats = new AcquisitionStatistics(null, clock.Read);
            var channel = new AnalogChannel(0);
            var deviceTime = Epoch;

            for (var i = 0; i < 1000; i++)
            {
                stats.Record(channel, Sample(deviceTime, 1.0));
                deviceTime = deviceTime.AddMilliseconds(1);
                clock.Advance(TimeSpan.FromMilliseconds(1.25));
            }

            var ai0 = Assert.Single(stats.Snapshot().Channels);
            Assert.Equal(1000.0, ai0.DeviceClockSampleRateHz, 6);
            Assert.Equal(800.0, ai0.MeasuredSampleRateHz, 6);
        }

        [Fact]
        public void Record_SampleIntervals_ReportTheSmallestLargestAndMeanGap()
        {
            var clock = new TestClock(Epoch);
            using var stats = new AcquisitionStatistics(null, clock.Read);
            var channel = new AnalogChannel(0);
            var deviceTime = Epoch;

            // Gaps of 1 ms, 4 ms, 1 ms — a stall in the middle, which is what a dropped block of
            // samples looks like from the host's side.
            foreach (var gapMs in new[] { 0.0, 1.0, 4.0, 1.0 })
            {
                deviceTime = deviceTime.AddMilliseconds(gapMs);
                stats.Record(channel, Sample(deviceTime, 1.0));
                clock.Advance(TimeSpan.FromMilliseconds(gapMs));
            }

            var ai0 = Assert.Single(stats.Snapshot().Channels);
            Assert.Equal(TimeSpan.FromMilliseconds(1), ai0.MinSampleInterval);
            Assert.Equal(TimeSpan.FromMilliseconds(4), ai0.MaxSampleInterval);
            Assert.Equal(TimeSpan.FromMilliseconds(2), ai0.MeanSampleInterval);
        }

        [Fact]
        public void Record_ASingleSample_HasNoRateAndNoInterval()
        {
            var clock = new TestClock(Epoch);
            using var stats = new AcquisitionStatistics(null, clock.Read);

            stats.Record(new AnalogChannel(0), Sample(clock.Now, 1.0));

            var ai0 = Assert.Single(stats.Snapshot().Channels);
            Assert.Equal(1, ai0.SampleCount);
            Assert.Equal(0.0, ai0.MeasuredSampleRateHz);
            Assert.Equal(0.0, ai0.DeviceClockSampleRateHz);
            Assert.Equal(TimeSpan.Zero, ai0.MinSampleInterval);
            Assert.Equal(TimeSpan.Zero, ai0.MaxSampleInterval);
            Assert.Equal(TimeSpan.Zero, ai0.MeanSampleInterval);
        }

        [Fact]
        public void Record_RepeatedDeviceTimestamps_ReportZeroIntervalRatherThanDividingByZero()
        {
            // Firmware that stamps several samples with the same tick value at high rates
            // (daqifi-nyquist-firmware #717): the device-clock span collapses to nothing.
            var clock = new TestClock(Epoch);
            using var stats = new AcquisitionStatistics(null, clock.Read);
            var channel = new AnalogChannel(0);

            for (var i = 0; i < 3; i++)
            {
                stats.Record(channel, Sample(Epoch, 1.0));
                clock.Advance(TimeSpan.FromMilliseconds(1));
            }

            var ai0 = Assert.Single(stats.Snapshot().Channels);
            Assert.Equal(0.0, ai0.DeviceClockSampleRateHz);
            Assert.Equal(TimeSpan.Zero, ai0.MaxSampleInterval);
            Assert.Equal(1000.0, ai0.MeasuredSampleRateHz, 6);
        }

        [Fact]
        public void Record_ATimestampThatMovesBackwards_IsCounted_AndKeptOutOfTheJitterBounds()
        {
            // TimestampProcessor reconstructs a time that moves backwards when the device sends a
            // frame out of order, so a sample's timestamp can precede the one before it. A negative
            // number is not a gap: it must not land in the interval bounds.
            var clock = new TestClock(Epoch);
            using var stats = new AcquisitionStatistics(null, clock.Read);
            var channel = new AnalogChannel(0);

            foreach (var offsetMs in new[] { 0.0, 1.0, 3.0, 2.0, 4.0 }) // the 4th sample steps back
            {
                stats.Record(channel, Sample(Epoch.AddMilliseconds(offsetMs), 1.0));
                clock.Advance(TimeSpan.FromMilliseconds(1));
            }

            var ai0 = Assert.Single(stats.Snapshot().Channels);
            Assert.Equal(1, ai0.OutOfOrderSampleCount);
            Assert.Equal(TimeSpan.FromMilliseconds(1), ai0.MinSampleInterval);
            Assert.Equal(TimeSpan.FromMilliseconds(2), ai0.MaxSampleInterval);
        }

        [Fact]
        public void Record_TheSampleAfterABackwardsStep_IsNotMeasuredFromTheRewoundTimestamp()
        {
            // What a stale frame actually looks like: the clock rewinds, then the next frame jumps
            // forward again to roughly where it was. Measuring that jump from the rewound value
            // would report the whole five-second rewind as the worst gap in the acquisition.
            var clock = new TestClock(Epoch);
            using var stats = new AcquisitionStatistics(null, clock.Read);
            var channel = new AnalogChannel(0);

            foreach (var offset in new[]
                     {
                         TimeSpan.Zero,
                         TimeSpan.FromMilliseconds(1),
                         TimeSpan.FromSeconds(-5),      // stale frame
                         TimeSpan.FromMilliseconds(2),  // the stream carrying on as if nothing happened
                     })
            {
                stats.Record(channel, Sample(Epoch.Add(offset), 1.0));
                clock.Advance(TimeSpan.FromMilliseconds(1));
            }

            var ai0 = Assert.Single(stats.Snapshot().Channels);
            Assert.Equal(1, ai0.OutOfOrderSampleCount);
            Assert.Equal(TimeSpan.FromMilliseconds(1), ai0.MinSampleInterval);
            Assert.Equal(TimeSpan.FromMilliseconds(1), ai0.MaxSampleInterval);
        }

        [Fact]
        public void Record_ATimestampThatStepsFarBack_DoesNotCollapseTheDeviceClockRate()
        {
            // The damaging case: the most recently recorded sample is not the latest in time, so a
            // span taken from first-and-last would be negative and the reported rate would drop to
            // zero — a device that looked stopped because one frame arrived late.
            var clock = new TestClock(Epoch);
            using var stats = new AcquisitionStatistics(null, clock.Read);
            var channel = new AnalogChannel(0);

            stats.Record(channel, Sample(Epoch, 1.0));
            clock.Advance(TimeSpan.FromMilliseconds(1));
            stats.Record(channel, Sample(Epoch.AddMilliseconds(2), 1.0));
            clock.Advance(TimeSpan.FromMilliseconds(1));
            stats.Record(channel, Sample(Epoch.AddSeconds(-5), 1.0));

            var ai0 = Assert.Single(stats.Snapshot().Channels);
            Assert.Equal(1, ai0.OutOfOrderSampleCount);
            Assert.Equal(Epoch.AddSeconds(-5), ai0.EarliestSampleTimestamp);
            Assert.Equal(Epoch.AddMilliseconds(2), ai0.LatestSampleTimestamp);
            Assert.Equal(2 / 5.002, ai0.DeviceClockSampleRateHz, 6);
            Assert.True(ai0.MeanSampleInterval > TimeSpan.Zero);
        }

        [Fact]
        public void Record_MonotonicTimestamps_ReportNoOutOfOrderSamples()
        {
            var clock = new TestClock(Epoch);
            using var stats = new AcquisitionStatistics(null, clock.Read);
            var channel = new AnalogChannel(0);

            for (var i = 0; i < 10; i++)
            {
                stats.Record(channel, Sample(Epoch.AddMilliseconds(i), 1.0));
                clock.Advance(TimeSpan.FromMilliseconds(1));
            }

            Assert.Equal(0, Assert.Single(stats.Snapshot().Channels).OutOfOrderSampleCount);
        }

        #endregion

        #region Latency and window

        [Fact]
        public void Record_Latency_MeasuresHostArrivalAgainstTheDeviceTimestamp()
        {
            var clock = new TestClock(Epoch);
            using var stats = new AcquisitionStatistics(null, clock.Read);
            var channel = new AnalogChannel(0);
            var deviceTime = Epoch;

            // Arrivals 2 ms, 6 ms and 4 ms after the timestamps they carry.
            foreach (var latencyMs in new[] { 2.0, 6.0, 4.0 })
            {
                clock.Set(deviceTime.AddMilliseconds(latencyMs));
                stats.Record(channel, Sample(deviceTime, 1.0));
                deviceTime = deviceTime.AddMilliseconds(10);
            }

            var snapshot = stats.Snapshot();
            Assert.Equal(TimeSpan.FromMilliseconds(2), snapshot.MinLatency);
            Assert.Equal(TimeSpan.FromMilliseconds(6), snapshot.MaxLatency);
            Assert.Equal(TimeSpan.FromMilliseconds(4), snapshot.MeanLatency);
        }

        [Fact]
        public void Record_Latency_CanBeNegative_WhenTheDeviceClockOutrunsTheHost()
        {
            var clock = new TestClock(Epoch);
            using var stats = new AcquisitionStatistics(null, clock.Read);

            stats.Record(new AnalogChannel(0), Sample(Epoch.AddMilliseconds(5), 1.0));

            Assert.Equal(TimeSpan.FromMilliseconds(-5), stats.Snapshot().MinLatency);
        }

        [Fact]
        public void Snapshot_Duration_SpansTheSamples_NotTheLifetimeOfTheAggregator()
        {
            var clock = new TestClock(Epoch);
            using var stats = new AcquisitionStatistics(null, clock.Read);
            var channel = new AnalogChannel(0);

            clock.Advance(TimeSpan.FromSeconds(30)); // idle before anything streams
            stats.Record(channel, Sample(clock.Now, 1.0));
            clock.Advance(TimeSpan.FromSeconds(2));
            stats.Record(channel, Sample(clock.Now, 1.0));

            var snapshot = stats.Snapshot();
            Assert.Equal(Epoch, snapshot.StartedAt);
            Assert.Equal(TimeSpan.FromSeconds(2), snapshot.Duration);
            Assert.Equal(Epoch.AddSeconds(30), snapshot.FirstReceivedAt);
            Assert.Equal(Epoch.AddSeconds(32), snapshot.LastReceivedAt);
        }

        [Fact]
        public void Reset_DiscardsTheWindow_AndStartsANewOne()
        {
            var clock = new TestClock(Epoch);
            using var stats = new AcquisitionStatistics(null, clock.Read);
            var channel = new AnalogChannel(0);

            stats.Record(channel, Sample(clock.Now, 1.0));
            clock.Advance(TimeSpan.FromSeconds(1));

            stats.Reset();

            var afterReset = stats.Snapshot();
            Assert.Equal(0, afterReset.TotalSampleCount);
            Assert.Empty(afterReset.Channels);
            Assert.Equal(Epoch.AddSeconds(1), afterReset.StartedAt);

            stats.Record(channel, Sample(clock.Now, 7.0));
            var ai0 = Assert.Single(stats.Snapshot().Channels);
            Assert.Equal(1, ai0.SampleCount);
            Assert.Equal(7.0, ai0.MinValue);
        }

        #endregion

        #region Multiple channels

        [Fact]
        public void Record_TracksChannelsSeparately_AndReportsThemInDeviceOrder()
        {
            var clock = new TestClock(Epoch);
            using var stats = new AcquisitionStatistics(null, clock.Read);

            stats.Record(new DigitalChannel(2), Sample(clock.Now, 1.0));
            stats.Record(new AnalogChannel(1), Sample(clock.Now, 2.0));
            stats.Record(new DigitalChannel(0), Sample(clock.Now, 0.0));
            stats.Record(new AnalogChannel(0), Sample(clock.Now, 3.0));
            stats.Record(new AnalogChannel(0), Sample(clock.Now, 5.0));

            var channels = stats.Snapshot().Channels;
            Assert.Equal(
                new[]
                {
                    (ChannelType.Analog, 0),
                    (ChannelType.Analog, 1),
                    (ChannelType.Digital, 0),
                    (ChannelType.Digital, 2),
                },
                channels.Select(c => (c.ChannelType, c.ChannelNumber)));
            Assert.Equal(2, channels[0].SampleCount);
            Assert.Equal(4.0, channels[0].MeanValue, 9);
            Assert.Equal(5, stats.Snapshot().TotalSampleCount);
        }

        [Fact]
        public void Record_LiveSampleOverload_RecordsTheSameAsTheChannelOverload()
        {
            var clock = new TestClock(Epoch);
            using var stats = new AcquisitionStatistics(null, clock.Read);
            var channel = new AnalogChannel(0);

            stats.Record(new LiveSample(channel, Sample(clock.Now, 2.5)));

            var ai0 = Assert.Single(stats.Snapshot().Channels);
            Assert.Equal(1, ai0.SampleCount);
            Assert.Equal(2.5, ai0.MeanValue);
        }

        [Fact]
        public void Record_FromSeveralThreadsAtOnce_CountsEverySample()
        {
            using var stats = new AcquisitionStatistics();
            var channels = new[] { new AnalogChannel(0), new AnalogChannel(1), new AnalogChannel(2) };
            const int perThread = 5_000;

            Parallel.ForEach(channels, channel =>
            {
                for (var i = 0; i < perThread; i++)
                {
                    stats.Record(channel, Sample(Epoch.AddMilliseconds(i), i));
                }
            });

            var snapshot = stats.Snapshot();
            Assert.Equal(channels.Length * perThread, snapshot.TotalSampleCount);
            Assert.All(snapshot.Channels, c => Assert.Equal(perThread, c.SampleCount));
        }

        #endregion

        #region Argument validation

        [Fact]
        public void Ctor_WithNullDevice_Throws() =>
            Assert.Throws<ArgumentNullException>(() => new AcquisitionStatistics((IStreamingDevice)null!));

        [Fact]
        public void Record_WithNullArguments_Throws()
        {
            using var stats = new AcquisitionStatistics();

            Assert.Throws<ArgumentNullException>(() => stats.Record(null!, Sample(Epoch, 1.0)));
            Assert.Throws<ArgumentNullException>(() => stats.Record(new AnalogChannel(0), null!));
            Assert.Throws<ArgumentNullException>(() => stats.Record(null!));
        }

        #endregion

        #region Attached to a device

        [Fact]
        public void AttachedToDevice_RecordsEveryDecodedSample()
        {
            var device = CreateStreaming(analogCount: 2);
            EnableAllChannels(device);
            device.StartStreaming();

            using var stats = new AcquisitionStatistics(device);

            device.InvokeStreamMessage(AnalogFrame(1_000, 1.0f, 2.0f));
            device.InvokeStreamMessage(AnalogFrame(51_000, 3.0f, 4.0f));

            var snapshot = stats.Snapshot();
            Assert.Equal(4, snapshot.TotalSampleCount);
            Assert.Equal(2, snapshot.Channels.Count);
            Assert.Equal(1.0, snapshot.Channels[0].MinValue);
            Assert.Equal(3.0, snapshot.Channels[0].MaxValue);
            Assert.Equal(2.0, snapshot.Channels[1].MinValue);
            Assert.Equal(4.0, snapshot.Channels[1].MaxValue);

            // Frames one millisecond apart on the device's 50 MHz tick clock.
            Assert.Equal(TimeSpan.FromMilliseconds(1), snapshot.Channels[0].MaxSampleInterval);
        }

        [Fact]
        public void AttachedToDevice_SurvivesAChannelRepopulation_WithoutSplittingOrDoubleCountingAChannel()
        {
            var device = CreateStreaming(analogCount: 1);
            EnableAllChannels(device);
            device.StartStreaming();

            using var stats = new AcquisitionStatistics(device);
            device.InvokeStreamMessage(AnalogFrame(1_000, 1.0f));

            // A second status message re-runs population and re-raises ChannelsPopulated, which is
            // what the aggregator resubscribes on.
            device.PopulateChannelsFromStatus(StatusMessage(analogCount: 1));
            EnableAllChannels(device);
            device.InvokeStreamMessage(AnalogFrame(51_000, 2.0f));

            var ai0 = Assert.Single(stats.Snapshot().Channels);
            Assert.Equal(2, ai0.SampleCount); // one entry, and the second sample counted exactly once
            Assert.Equal(1.0, ai0.MinValue);
            Assert.Equal(2.0, ai0.MaxValue);
        }

        [Fact]
        public void Dispose_StopsRecording_ButLeavesTheSnapshotReadable()
        {
            var device = CreateStreaming(analogCount: 1);
            EnableAllChannels(device);
            device.StartStreaming();

            var stats = new AcquisitionStatistics(device);
            device.InvokeStreamMessage(AnalogFrame(1_000, 1.0f));
            stats.Dispose();
            device.InvokeStreamMessage(AnalogFrame(51_000, 9.0f));

            var ai0 = Assert.Single(stats.Snapshot().Channels);
            Assert.Equal(1, ai0.SampleCount);
            Assert.Equal(1.0, ai0.MaxValue);

            stats.Dispose(); // idempotent
            stats.Record(new AnalogChannel(0), Sample(Epoch, 5.0)); // ignored, not thrown
            Assert.Equal(1, stats.Snapshot().TotalSampleCount);
        }

        #endregion

        #region Cost

        // The two tests below are coarse guards, not instruments. They answer one yes/no question
        // each - does this allocate per call, and does attaching the aggregator cost the decode
        // path anything - and they answer it with a hand-rolled warmup on whatever machine the
        // suite happens to be running on, which is exactly the arrangement issue #531 was filed
        // about. The instrument is src/Daqifi.Core.Benchmarks (issue #640): StreamDecodeBenchmarks
        // reports allocation per frame under [MemoryDiagnoser], with BenchmarkDotNet handling
        // warmup and tiering. Take a number from there; take a pass/fail from here.

        [Fact]
        public void Record_AfterTheFirstSamplePerChannel_AllocatesNothing()
        {
            using var stats = new AcquisitionStatistics();
            var channel = new AnalogChannel(0);
            var sample = Sample(Epoch, 1.0);

            // Warm up over the SAME count as the measurement, not a token 100.
            //
            // Tiered compilation promotes a hot method to tier 1 partway through a long loop, and
            // that rejit allocates. With a 100-iteration warmup the promotion landed inside the
            // measured window instead of before it, and the test reported ~7 KB across 10,000
            // records -- 0.71 bytes per call, which is not a per-call allocation at all (a real one
            // is >= 24 bytes an object, so >= 240 KB here) but was enough to fail an == 0 assertion.
            //
            // That is issue #531: "fails on a full net10 run, passes in isolation". It is not
            // actually run-order dependent, it is warmup-dependent -- any change that makes
            // RecordCore larger pushes the promotion later, and #534's scaling-comparison did.
            // Warming to the measured count puts the promotion firmly before `before` is read.
            //
            // The assertion keeps its teeth: a genuine per-call allocation is three orders of
            // magnitude above the noise this removes.
            const int iterations = 10_000;

            for (var i = 0; i < iterations; i++)
            {
                stats.Record(channel, sample);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < iterations; i++)
            {
                stats.Record(channel, sample);
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(allocated == 0, $"recording {iterations:N0} samples allocated {allocated} bytes; expected none");
        }

        [Fact]
        public void AttachedToDevice_CostsTheDecodePathNoMoreThanAnEmptySubscriber()
        {
            // The honest baseline is a subscriber that does nothing, not no subscriber at all: the
            // per-sample SampleReceivedEventArgs is allocated by the channel for whoever is listening,
            // and it is charged to any subscriber equally. What this pins is that the aggregator adds
            // nothing of its own on top of that. (Detached, it is not merely cheap but absent — no
            // handler is subscribed, and no code on the decode path changed to accommodate one.)
            const int frames = 2_000;

            var inert = MeasureDecodeAllocations(frames, attach: false);
            var recording = MeasureDecodeAllocations(frames, attach: true);
            var perFrame = (recording - inert) / (double)frames;

            Assert.True(
                perFrame < 1.0,
                $"attached recording added {perFrame:F2} bytes per frame ({recording - inert} bytes over {frames} frames)");
        }

        private static long MeasureDecodeAllocations(int frames, bool attach)
        {
            var device = CreateStreaming(analogCount: 2);
            EnableAllChannels(device);
            device.StartStreaming();

            using var stats = attach ? new AcquisitionStatistics(device) : null;
            if (!attach)
            {
                foreach (var channel in device.Channels)
                {
                    channel.SampleReceived += Inert;
                }
            }

            var frame = AnalogFrame(0, 1.0f, 2.0f);

            void Push(int count, uint firstTick)
            {
                for (var i = 0; i < count; i++)
                {
                    frame.MsgTimeStamp = firstTick + (uint)i * 50_000u;
                    device.InvokeStreamMessage(frame);
                }
            }

            // Warm up over the SAME count as the measurement, not a token 200.
            //
            // This is #531's cause again, in the sibling test (#559). Tiered compilation promotes
            // a hot method to tier 1 partway through a long loop and the rejit allocates, so a
            // short warmup leaves the promotion to land INSIDE the measured window. It shows up as
            // a full-run-only failure -- in isolation the method is cold and the promotion falls
            // in the warmup; in a full run earlier tests have already made it partly hot, so the
            // threshold is crossed later, during the measurement. Nothing about run ORDER is
            // load-bearing, which is why the issue title's "passes in isolation" is a symptom
            // rather than the mechanism.
            //
            // The measurement base tick starts beyond the warmup's last one so timestamps stay
            // monotonic: the warmup ends at 50_000 + (frames-1) * 50_000.
            Push(frames, 50_000u);

            var before = GC.GetAllocatedBytesForCurrentThread();
            Push(frames, 200_000_000u);
            return GC.GetAllocatedBytesForCurrentThread() - before;

            static void Inert(object? sender, SampleReceivedEventArgs e)
            {
            }
        }

        #endregion

        #region Engineering units (issue #534)

        [Fact]
        public void Record_WithAChannelScaling_ReportsTheEngineeringValueAndItsUnit()
        {
            // A channel configured to read PSI must not have its statistics reported in volts.
            // Before this fix RecordCore read sample.Value, so a pressure transducer's snapshot
            // came back as the raw voltage with nothing saying which of the two it was -- and the
            // snapshot cannot be converted afterwards, because it has already consumed the samples.
            var clock = new TestClock(Epoch);
            using var stats = new AcquisitionStatistics(null, clock.Read);
            var channel = new AnalogChannel(0) { Name = "AI0" };
            var scaling = new ChannelScaling(2.0, 10.0, "PSI");

            foreach (var volts in new[] { 1.0, 3.0, 2.0 })
            {
                stats.Record(channel, Sample(clock.Now, volts, scaling));
                clock.Advance(TimeSpan.FromMilliseconds(1));
            }

            var channelStats = Assert.Single(stats.Snapshot().Channels);
            Assert.Equal("PSI", channelStats.Unit);
            Assert.Equal(12.0, channelStats.MinValue);   // 1.0 * 2 + 10
            Assert.Equal(16.0, channelStats.MaxValue);   // 3.0 * 2 + 10
            Assert.Equal(14.0, channelStats.MeanValue);  // mean of 12, 16, 14
        }

        [Fact]
        public void Record_WithAnInvertingScaling_DoesNotTransposeTheExtremes()
        {
            // The case that makes a manual fix-up unsafe rather than merely inconvenient: a
            // negative gain swaps which raw reading is the extreme. Recording raw volts and
            // letting the consumer re-apply the scaling would hand them a MinValue that is
            // actually the maximum. ChannelScaling permits a negative gain -- only finiteness
            // is validated -- so this is a supported configuration, not an abuse.
            var clock = new TestClock(Epoch);
            using var stats = new AcquisitionStatistics(null, clock.Read);
            var channel = new AnalogChannel(0) { Name = "AI0" };
            var scaling = new ChannelScaling(-3.0, 5.0, "PSI");

            foreach (var volts in new[] { 1.0, 2.0 })
            {
                stats.Record(channel, Sample(clock.Now, volts, scaling));
                clock.Advance(TimeSpan.FromMilliseconds(1));
            }

            var channelStats = Assert.Single(stats.Snapshot().Channels);

            // 1.0 V -> 2.0 PSI (the MAXIMUM); 2.0 V -> -1.0 PSI (the MINIMUM).
            Assert.Equal(-1.0, channelStats.MinValue);
            Assert.Equal(2.0, channelStats.MaxValue);
        }

        [Fact]
        public void Record_WithNoScaling_IsUnchangedAndReportsNoUnit()
        {
            // The control: an unscaled channel must read exactly as it did before, so the fix
            // cannot be "always transform something".
            var clock = new TestClock(Epoch);
            using var stats = new AcquisitionStatistics(null, clock.Read);
            var channel = new AnalogChannel(0) { Name = "AI0" };

            foreach (var value in new[] { 1.0, 3.0, 2.0 })
            {
                stats.Record(channel, Sample(clock.Now, value));
                clock.Advance(TimeSpan.FromMilliseconds(1));
            }

            var channelStats = Assert.Single(stats.Snapshot().Channels);
            Assert.Null(channelStats.Unit);
            Assert.Equal(1.0, channelStats.MinValue);
            Assert.Equal(3.0, channelStats.MaxValue);
            Assert.Equal(2.0, channelStats.MeanValue);
        }

        [Fact]
        public void Record_WhenTheScalingChangesMidWindow_RestartsTheValueFiguresAndSaysSo()
        {
            // IScaledChannel supports reassigning a scaling mid-session, and samples keep the one
            // in force when they were decoded -- so a window can genuinely hold two units. A
            // minimum in PSI and a maximum in Bar are not the extremes of anything, and reporting
            // the newest unit over both would put a label on them that is actively false.
            var clock = new TestClock(Epoch);
            using var stats = new AcquisitionStatistics(null, clock.Read);
            var channel = new AnalogChannel(0) { Name = "AI0" };
            var psi = new ChannelScaling(2.0, 10.0, "PSI");
            var bar = new ChannelScaling(1.0, 0.0, "Bar");

            foreach (var volts in new[] { 1.0, 3.0 })          // 12, 16 PSI
            {
                stats.Record(channel, Sample(clock.Now, volts, psi));
                clock.Advance(TimeSpan.FromMilliseconds(1));
            }

            foreach (var volts in new[] { 5.0, 7.0 })          // 5, 7 Bar
            {
                stats.Record(channel, Sample(clock.Now, volts, bar));
                clock.Advance(TimeSpan.FromMilliseconds(1));
            }

            var channelStats = Assert.Single(stats.Snapshot().Channels);

            // Only the Bar samples describe the value figures.
            Assert.Equal("Bar", channelStats.Unit);
            Assert.Equal(5.0, channelStats.MinValue);
            Assert.Equal(7.0, channelStats.MaxValue);
            Assert.Equal(6.0, channelStats.MeanValue);
            Assert.Equal(2, channelStats.ValueSampleCount);

            // Timing and counting are unit-independent and continue across the change.
            Assert.Equal(4, channelStats.SampleCount);
        }

        [Fact]
        public void Record_WhenOnlyTheGainChanges_AlsoRestartsTheValueFigures()
        {
            // Compared on the whole scaling, not just the unit label: recalibrating a transducer
            // leaves the unit reading "PSI" while making the earlier numbers incomparable with the
            // later ones. Aggregating those is the same error wearing a matching label.
            var clock = new TestClock(Epoch);
            using var stats = new AcquisitionStatistics(null, clock.Read);
            var channel = new AnalogChannel(0) { Name = "AI0" };

            stats.Record(channel, Sample(clock.Now, 1.0, new ChannelScaling(2.0, 0.0, "PSI")));
            clock.Advance(TimeSpan.FromMilliseconds(1));
            stats.Record(channel, Sample(clock.Now, 1.0, new ChannelScaling(50.0, 0.0, "PSI")));

            var channelStats = Assert.Single(stats.Snapshot().Channels);

            Assert.Equal("PSI", channelStats.Unit);
            Assert.Equal(50.0, channelStats.MinValue);
            Assert.Equal(50.0, channelStats.MaxValue);
            Assert.Equal(1, channelStats.ValueSampleCount);
            Assert.Equal(2, channelStats.SampleCount);
        }

        [Fact]
        public void Record_WithAStableScaling_CountsEveryValueSample()
        {
            // The control for the restart: an unchanging scaling must not trigger it, or every
            // window would collapse to its last sample.
            var clock = new TestClock(Epoch);
            using var stats = new AcquisitionStatistics(null, clock.Read);
            var channel = new AnalogChannel(0) { Name = "AI0" };
            var psi = new ChannelScaling(2.0, 10.0, "PSI");

            foreach (var volts in new[] { 1.0, 3.0, 2.0 })
            {
                // A NEW but EQUAL scaling instance each time -- ChannelScaling is a record, so
                // this must compare equal and must not look like a reassignment.
                stats.Record(channel, Sample(clock.Now, volts, new ChannelScaling(2.0, 10.0, "PSI")));
                clock.Advance(TimeSpan.FromMilliseconds(1));
            }

            var channelStats = Assert.Single(stats.Snapshot().Channels);
            Assert.Equal(3, channelStats.ValueSampleCount);
            Assert.Equal(3, channelStats.SampleCount);
            Assert.Equal(12.0, channelStats.MinValue);
            Assert.Equal(16.0, channelStats.MaxValue);
            Assert.Equal(psi.Unit, channelStats.Unit);
        }

        #endregion

        #region Helpers

        private static IDataSample Sample(DateTime timestamp, double value) =>
            new DataSample(timestamp, value);

        private static IDataSample Sample(DateTime timestamp, double value, ChannelScaling scaling) =>
            new DataSample(timestamp, value) { Scaling = scaling };

        private static void EnableAllChannels(DaqifiStreamingDevice device)
        {
            foreach (var channel in device.Channels)
            {
                channel.IsEnabled = true;
            }
        }

        private static DaqifiOutMessage StatusMessage(int analogCount)
        {
            var status = new DaqifiOutMessage
            {
                AnalogInPortNum = (uint)analogCount,
                DigitalPortNum = 0,
                AnalogInRes = 65535,
            };
            for (var i = 0; i < analogCount; i++)
            {
                status.AnalogInPortRange.Add(1.0f);
            }

            return status;
        }

        private static StatisticsDevice CreateStreaming(int analogCount)
        {
            var device = new StatisticsDevice("TestDevice");
            device.Connect();
            device.PopulateChannelsFromStatus(StatusMessage(analogCount));
            return device;
        }

        private static DaqifiOutMessage AnalogFrame(uint timestamp, params float[] values)
        {
            var frame = new DaqifiOutMessage { MsgTimeStamp = timestamp };
            foreach (var value in values)
            {
                frame.AnalogInDataFloat.Add(value);
            }

            return frame;
        }

        /// <summary>
        /// A streaming device with no transport whose stream frames are injected by the test, mirroring
        /// the double the live-stream tests use.
        /// </summary>
        private sealed class StatisticsDevice : DaqifiStreamingDevice
        {
            public StatisticsDevice(string name) : base(name) { }

            public void InvokeStreamMessage(DaqifiOutMessage message) => OnStreamMessageReceived(message);

            public override void Send<T>(IOutboundMessage<T> message) { /* no transport in tests */ }
        }

        /// <summary>
        /// A host clock the test moves by hand, so arrival rates and latencies are exact rather than
        /// whatever the machine happened to be doing.
        /// </summary>
        private sealed class TestClock
        {
            private long _ticks;

            public TestClock(DateTime start) => _ticks = start.Ticks;

            public DateTime Now => Read();

            public DateTime Read() => new DateTime(Volatile.Read(ref _ticks), DateTimeKind.Local);

            public void Advance(TimeSpan by) => Volatile.Write(ref _ticks, _ticks + by.Ticks);

            public void Set(DateTime now) => Volatile.Write(ref _ticks, now.Ticks);
        }

        #endregion
    }
}
