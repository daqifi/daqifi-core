using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Daqifi.Core.Tests.Device.Internal;

/// <summary>
/// Every way a held-back message can leave the backlog. It goes out, or its sender is told once that
/// it will not: a sender that recorded the message as sent has no other way to find out.
/// </summary>
public class OperationSerializerHeldBackSendTests
{
    [Fact]
    public async Task ItsTurnComes_IsSent()
    {
        var (_, message, end) = await HoldBackAsync();

        await end();

        Assert.Equal(1, message.Sent);
        Assert.Empty(message.NotSent);
    }

    [Fact]
    public async Task TheBacklogOverflows_IsReportedWithTheReason()
    {
        var (serializer, message, end) = await HoldBackAsync(cap: 1);

        Assert.True(serializer.TryDeferSend(new ScpiMessage("NEWER")));

        Assert.Contains("backlog", Assert.Single(message.NotSent)!.Message, StringComparison.Ordinal);
        await end();
        Assert.Equal(0, message.Sent);
        Assert.Single(message.NotSent);
    }

    [Fact]
    public async Task TheConnectionIsTornDown_IsReported()
    {
        var (serializer, message, end) = await HoldBackAsync();

        serializer.ResetDeferralState();

        Assert.Null(Assert.Single(message.NotSent));
        await end();
        Assert.Equal(0, message.Sent);
        Assert.Single(message.NotSent);
    }

    [Fact]
    public async Task TheSendFailsWhenItsTurnComes_IsReportedWithTheFailure()
    {
        var failure = new InvalidOperationException("gone");
        var (_, message, end) = await HoldBackAsync(onSend: _ => throw failure);

        await end();

        Assert.Same(failure, Assert.Single(message.NotSent));
    }

    [Fact]
    public async Task TheDeviceIsDisposedBeforeTheRestOfTheBacklogIsReplayed_IsReported()
    {
        // Past 64 messages the operation hands the rest to a flush of its own, which needs the lock.
        OperationSerializer? disposing = null;
        var message = new HeldBackMessage();
        var host = new StubHost(1024, text =>
        {
            if (text == "63")
            {
                disposing!.Dispose();
            }
        });
        var serializer = disposing = new OperationSerializer(host);
        var end = await HoldAsync(serializer);
        for (var i = 0; i < 64; i++)
        {
            serializer.TryDeferSend(new ScpiMessage($"{i}"));
        }

        Assert.True(serializer.TryDeferSend(new ScpiMessage("LAST"), message));
        await end();

        Assert.True(SpinWait.SpinUntil(() => message.NotSent.Count > 0, TimeSpan.FromSeconds(5)));
        Assert.Null(Assert.Single(message.NotSent));
        Assert.Equal(0, message.Sent);
    }

    private static async Task<(OperationSerializer Serializer, HeldBackMessage Message, Func<Task> End)> HoldBackAsync(
        int cap = 1024, Action<string>? onSend = null)
    {
        var serializer = new OperationSerializer(new StubHost(cap, onSend));
        var message = new HeldBackMessage();
        var end = await HoldAsync(serializer);

        Assert.True(serializer.TryDeferSend(new ScpiMessage("HELD BACK"), message));

        return (serializer, message, end);
    }

    /// <summary>Has an operation on another flow own the device until the returned action is called.</summary>
    private static async Task<Func<Task>> HoldAsync(OperationSerializer serializer)
    {
        var inOperation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var endOperation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = Task.Run(() => serializer.RunExclusiveAsync(
            async _ =>
            {
                inOperation.SetResult();
                await endOperation.Task;
                return true;
            },
            CancellationToken.None));
        await inOperation.Task;

        return () =>
        {
            endOperation.SetResult();
            return operation.WaitAsync(TimeSpan.FromSeconds(10));
        };
    }

    private sealed class HeldBackMessage : IHeldBackSend
    {
        public int Sent { get; private set; }

        public List<Exception?> NotSent { get; } = new();

        public void Replay(Action send)
        {
            send();
            Sent++;
        }

        void IHeldBackSend.NotSent(Exception? reason) => NotSent.Add(reason);
    }

    private sealed class StubHost(int cap, Action<string>? onSend) : IOperationSerializationHost
    {
        public ILogger Logger => NullLogger.Instance;

        public int MaxDeferredSends => cap;

        public TimeSpan OutboundDrainWait => TimeSpan.Zero;

        public IMessageProducer<string>? MessageProducer => null;

        public TimeProvider TimeProvider => TimeProvider.System;

        public void SendNow<T>(IOutboundMessage<T> message) => onSend?.Invoke((string)(object)message.Data!);
    }
}
