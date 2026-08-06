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
    public class DaqifiStreamingDeviceLiveStreamTests
    {
        /// <summary>
        /// Upper bound on every await in this file. The live stream's reads are unbounded by design —
        /// it waits for the next sample — so a regression in cancellation, in delivery, or in argument
        /// validation would otherwise park a test forever and stall the whole run. Every wait here is
        /// bounded so such a regression surfaces as a fast <see cref="TimeoutException"/> instead.
        /// Generous enough not to flake on a loaded CI agent.
        /// </summary>
        private static readonly TimeSpan MoveNextTimeout = TimeSpan.FromSeconds(5);

        [Fact]
        public async Task StreamSamplesAsync_YieldsInjectedSample_WithChannelAndValue()
        {
            var device = CreateStreaming(analogCount: 1);
            var ai0 = AnalogChannel(device, 0);
            ai0.IsEnabled = true;
            device.StartStreaming();

            await using var e = device.StreamSamplesAsync(CancellationToken.None).GetAsyncEnumerator();
            var moveNext = e.MoveNextAsync(); // runs the body: subscribes synchronously, then awaits
            device.InvokeStreamMessage(AnalogFrame(1000, 1.5f));

            Assert.True(await moveNext.AsTask().WaitAsync(MoveNextTimeout));
            Assert.Same(ai0, e.Current.Channel);
            Assert.Equal(1.5, e.Current.Sample.Value);
            Assert.Equal(1000u, e.Current.Sample.DeviceTimestamp);
        }

        [Fact]
        public async Task StreamSamplesAsync_MultipleFrames_YieldsInOrder()
        {
            var device = CreateStreaming(analogCount: 1);
            AnalogChannel(device, 0).IsEnabled = true;
            device.StartStreaming();

            await using var e = device.StreamSamplesAsync(CancellationToken.None).GetAsyncEnumerator();
            var first = e.MoveNextAsync(); // subscribes synchronously before we inject
            device.InvokeStreamMessage(AnalogFrame(1000, 1f));
            device.InvokeStreamMessage(AnalogFrame(1010, 2f));
            device.InvokeStreamMessage(AnalogFrame(1020, 3f));

            Assert.True(await first.AsTask().WaitAsync(MoveNextTimeout));
            Assert.Equal(1.0, e.Current.Sample.Value);
            Assert.True(await e.MoveNextAsync().AsTask().WaitAsync(MoveNextTimeout));
            Assert.Equal(2.0, e.Current.Sample.Value);
            Assert.True(await e.MoveNextAsync().AsTask().WaitAsync(MoveNextTimeout));
            Assert.Equal(3.0, e.Current.Sample.Value);
        }

        [Fact]
        public async Task StreamSamplesAsync_Cancellation_EndsEnumeration_ButNotDeviceStream()
        {
            var device = CreateStreaming(analogCount: 1);
            AnalogChannel(device, 0).IsEnabled = true;
            device.StartStreaming();

            using var cts = new CancellationTokenSource();

            // Disposed explicitly below rather than by `await using`: a regression here leaves the
            // read parked, and disposing an async iterator whose MoveNextAsync is still in flight
            // throws NotSupportedException from the finally, masking the TimeoutException that
            // actually names the problem.
            var e = device.StreamSamplesAsync(cts.Token).GetAsyncEnumerator();
            var moveNext = e.MoveNextAsync();
            cts.Cancel();

            // Bounded on purpose: if cancellation ever stops ending the read, an unbounded await here
            // would park forever and hang the whole run. The timeout turns that into a fast, named
            // failure (TimeoutException instead of the expected OperationCanceledException).
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => moveNext.AsTask().WaitAsync(MoveNextTimeout));
            await e.DisposeAsync();

            Assert.True(device.IsStreaming); // cancelling enumeration must NOT stop the device stream
        }

        [Fact]
        public async Task StreamSamplesAsync_ConsumerFallsBehind_DropsOldest_AndCountsDrops()
        {
            var device = CreateStreaming(analogCount: 1);
            AnalogChannel(device, 0).IsEnabled = true;
            device.StartStreaming();

            await using var e = device.StreamSamplesAsync(CancellationToken.None, bufferCapacity: 2).GetAsyncEnumerator();
            var moveNext = e.MoveNextAsync(); // subscribes; reader is awaiting (not consuming synchronously)

            // Push far more than the buffer holds, synchronously, before the reader runs.
            for (uint i = 0; i < 20; i++) device.InvokeStreamMessage(AnalogFrame(1000 + i * 10, i));

            Assert.True(await moveNext.AsTask().WaitAsync(MoveNextTimeout));
            Assert.True(device.DroppedLiveSampleCount > 0, "drop-oldest should have dropped and counted overflow samples");
        }

        [Fact]
        public async Task StreamSamplesAsync_WithCancellation_EndsEnumeration_ButNotDeviceStream()
        {
            var device = CreateStreaming(analogCount: 1);
            AnalogChannel(device, 0).IsEnabled = true;
            device.StartStreaming();

            using var cts = new CancellationTokenSource();

            // The token supplied by WithCancellation rather than by the argument. This device method
            // hands back LiveSampleStream's async iterator as-is, so the token has to reach that
            // iterator's [EnumeratorCancellation] parameter; re-wrapping the forward in another
            // iterator without the attribute would drop it silently and hang here instead.
            var enumeration = Task.Run(async () =>
            {
                await foreach (var _ in device.StreamSamplesAsync().WithCancellation(cts.Token)) { }
            });

            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => enumeration.WaitAsync(MoveNextTimeout));
            Assert.True(device.IsStreaming);
        }

        [Fact]
        public async Task StreamSamplesAsync_InvalidBufferCapacity_Throws()
        {
            var device = CreateStreaming(analogCount: 1);

            // Bounded on purpose: were the validation to stop throwing, this enumeration would block
            // on an empty buffer that nothing ever writes to, so an unbounded await would hang the run
            // rather than fail it.
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => ConsumeAsync().WaitAsync(MoveNextTimeout));

            async Task ConsumeAsync()
            {
                await foreach (var _ in device.StreamSamplesAsync(CancellationToken.None, bufferCapacity: 0)) { }
            }
        }

        #region Helpers

        private static LiveStreamDevice CreateStreaming(int analogCount)
        {
            var device = new LiveStreamDevice("TestDevice");
            device.Connect();
            var status = new DaqifiOutMessage
            {
                AnalogInPortNum = (uint)analogCount,
                DigitalPortNum = 0,
                AnalogInRes = 65535,
            };
            for (var i = 0; i < analogCount; i++) status.AnalogInPortRange.Add(1.0f);
            device.PopulateChannelsFromStatus(status);
            return device;
        }

        private static IAnalogChannel AnalogChannel(DaqifiStreamingDevice device, int number) =>
            (IAnalogChannel)device.Channels.First(c => c.Type == ChannelType.Analog && c.ChannelNumber == number);

        private static DaqifiOutMessage AnalogFrame(uint timestamp, float value)
        {
            var frame = new DaqifiOutMessage { MsgTimeStamp = timestamp };
            frame.AnalogInDataFloat.Add(value);
            return frame;
        }

        private sealed class LiveStreamDevice : DaqifiStreamingDevice
        {
            public LiveStreamDevice(string name) : base(name) { }

            public void InvokeStreamMessage(DaqifiOutMessage message) => OnStreamMessageReceived(message);

            public override void Send<T>(IOutboundMessage<T> message) { /* no transport in tests */ }
        }

        #endregion
    }
}
