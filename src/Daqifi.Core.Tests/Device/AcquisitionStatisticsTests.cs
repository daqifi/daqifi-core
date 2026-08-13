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

        [Fact]
        public void Record_AfterTheFirstSamplePerChannel_AllocatesNothing()
        {
            using var stats = new AcquisitionStatistics();
            var channel = new AnalogChannel(0);
            var sample = Sample(Epoch, 1.0);

            // Warm up: JIT the path and create the channel's accumulator, the one allocation there is.
            for (var i = 0; i < 100; i++)
            {
                stats.Record(channel, sample);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10_000; i++)
            {
                stats.Record(channel, sample);
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(allocated == 0, $"recording 10,000 samples allocated {allocated} bytes; expected none");
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

            Push(200, 50_000u); // warm up the JIT, the decoder's channel cache and the accumulators

            var before = GC.GetAllocatedBytesForCurrentThread();
            Push(frames, 50_000_000u);
            return GC.GetAllocatedBytesForCurrentThread() - before;

            static void Inert(object? sender, SampleReceivedEventArgs e)
            {
            }
        }

        #endregion

        #region Helpers

        private static IDataSample Sample(DateTime timestamp, double value) =>
            new DataSample(timestamp, value);

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
