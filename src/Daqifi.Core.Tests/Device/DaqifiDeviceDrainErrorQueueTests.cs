using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Daqifi.Core.Tests.Device
{
    public class DaqifiDeviceDrainErrorQueueTests
    {
        [Fact]
        public async Task DrainErrorQueueAsync_WhenQueueIsClean_ReturnsEmpty()
        {
            var device = new SequencedReplyDevice("TestDevice");
            device.Replies.Enqueue(new[] { "0,\"No error\"" });
            device.Connect();

            var popped = await device.DrainErrorQueueAsync();

            Assert.Empty(popped);
            Assert.Equal(1, device.ExecuteTextCommandCallCount);
        }

        [Fact]
        public async Task DrainErrorQueueAsync_WhenSeveralErrorsQueued_ReturnsAllInOrder()
        {
            var device = new SequencedReplyDevice("TestDevice");
            device.Replies.Enqueue(new[] { "-200,\"Execution error\"" });
            device.Replies.Enqueue(new[] { "-113,\"Undefined header\"" });
            device.Replies.Enqueue(new[] { "-410,\"Query INTERRUPTED\"" });
            device.Replies.Enqueue(new[] { "0,\"No error\"" });
            device.Connect();

            var popped = await device.DrainErrorQueueAsync();

            Assert.Equal(new[]
            {
                "-200,\"Execution error\"",
                "-113,\"Undefined header\"",
                "-410,\"Query INTERRUPTED\"",
            }, popped);
            Assert.Equal(4, device.ExecuteTextCommandCallCount);
        }

        [Fact]
        public async Task DrainErrorQueueAsync_AcceptsPlusZeroNoErrorTerminator()
        {
            var device = new SequencedReplyDevice("TestDevice");
            device.Replies.Enqueue(new[] { "-200,\"Execution error\"" });
            device.Replies.Enqueue(new[] { "+0,\"No error\"" });
            device.Connect();

            var popped = await device.DrainErrorQueueAsync();

            Assert.Single(popped);
            Assert.Equal("-200,\"Execution error\"", popped[0]);
        }

        [Fact]
        public async Task DrainErrorQueueAsync_WhenReplyHasTrailingWhitespace_TrimsBeforeReturning()
        {
            var device = new SequencedReplyDevice("TestDevice");
            device.Replies.Enqueue(new[] { "  -200,\"Execution error\"\r\n" });
            device.Replies.Enqueue(new[] { "0,\"No error\"" });
            device.Connect();

            var popped = await device.DrainErrorQueueAsync();

            Assert.Single(popped);
            Assert.Equal("-200,\"Execution error\"", popped[0]);
        }

        [Fact]
        public async Task DrainErrorQueueAsync_OnEmptyReply_TerminatesEarly()
        {
            // Empty reply should NOT be treated as an error and should NOT cause
            // the loop to keep hammering the device. Instead, drain stops and
            // returns whatever it has so far.
            var device = new SequencedReplyDevice("TestDevice");
            device.Replies.Enqueue(new[] { "-200,\"Execution error\"" });
            device.Replies.Enqueue(Array.Empty<string>()); // simulated timeout
            device.Replies.Enqueue(new[] { "0,\"No error\"" }); // should never be consumed
            device.Connect();

            var popped = await device.DrainErrorQueueAsync();

            Assert.Equal(new[] { "-200,\"Execution error\"" }, popped);
            Assert.Equal(2, device.ExecuteTextCommandCallCount);
            Assert.Single(device.Replies); // unconsumed reply remains
        }

        [Fact]
        public async Task DrainErrorQueueAsync_WhenQueueExceedsCap_ReturnsCapManyAndStops()
        {
            var device = new SequencedReplyDevice("TestDevice");
            for (int i = 0; i < 10; i++)
            {
                device.Replies.Enqueue(new[] { $"-200,\"Execution error #{i}\"" });
            }
            device.Replies.Enqueue(new[] { "0,\"No error\"" });
            device.Connect();

            var popped = await device.DrainErrorQueueAsync(maxIterations: 3);

            Assert.Equal(3, popped.Count);
            Assert.Equal(3, device.ExecuteTextCommandCallCount);
            Assert.Equal(new[]
            {
                "-200,\"Execution error #0\"",
                "-200,\"Execution error #1\"",
                "-200,\"Execution error #2\"",
            }, popped);
        }

        [Fact]
        public async Task DrainErrorQueueAsync_SendsSystemErrorQueryEachIteration()
        {
            var device = new SequencedReplyDevice("TestDevice");
            device.Replies.Enqueue(new[] { "-200,\"Execution error\"" });
            device.Replies.Enqueue(new[] { "-113,\"Undefined header\"" });
            device.Replies.Enqueue(new[] { "0,\"No error\"" });
            device.Connect();

            await device.DrainErrorQueueAsync();

            var commands = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Equal(3, commands.Count);
            Assert.All(commands, c => Assert.Equal("SYSTem:ERRor?", c));
        }

        [Fact]
        public async Task DrainErrorQueueAsync_WhenCancelled_ThrowsOperationCanceled()
        {
            var device = new SequencedReplyDevice("TestDevice");
            // Pre-queue a long stream of errors. The cancellation should fire
            // before any of them is processed.
            for (int i = 0; i < 10; i++)
            {
                device.Replies.Enqueue(new[] { $"-200,\"Execution error #{i}\"" });
            }
            device.Connect();

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => device.DrainErrorQueueAsync(cancellationToken: cts.Token));
        }

        [Fact]
        public async Task DrainErrorQueueAsync_WhenMaxIterationsNotPositive_Throws()
        {
            var device = new SequencedReplyDevice("TestDevice");

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => device.DrainErrorQueueAsync(maxIterations: 0));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => device.DrainErrorQueueAsync(maxIterations: -1));
        }

        /// <summary>
        /// A testable DaqifiDevice that returns a different canned response on each
        /// successive ExecuteTextCommandAsync call, so drain-style tests can verify
        /// per-iteration behavior without a real transport.
        /// </summary>
        private class SequencedReplyDevice : DaqifiDevice
        {
            public List<IOutboundMessage<string>> SentMessages { get; } = new();
            public Queue<IReadOnlyList<string>> Replies { get; } = new();
            public int ExecuteTextCommandCallCount { get; private set; }

            public SequencedReplyDevice(string name, IPAddress? ipAddress = null)
                : base(name, ipAddress)
            {
            }

            public override void Send<T>(IOutboundMessage<T> message)
            {
                if (message is IOutboundMessage<string> stringMessage)
                {
                    SentMessages.Add(stringMessage);
                }
            }

            protected override Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
                Action setupAction,
                int responseTimeoutMs = 1000,
                int completionTimeoutMs = 250,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                setupAction();
                ExecuteTextCommandCallCount++;
                var reply = Replies.Count > 0 ? Replies.Dequeue() : Array.Empty<string>();
                return Task.FromResult(reply);
            }
        }
    }
}
