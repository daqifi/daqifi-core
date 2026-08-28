using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device.Internal;
using Daqifi.Core.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using System.Diagnostics;

namespace Daqifi.Core.Tests.Device.Internal;

/// <summary>
/// The outbound drain barrier's bound, on a stepped clock (issue #637).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="OperationSerializer.DrainOutboundQueueAsync"/> is what keeps an earlier command's
/// reply out of the next text exchange (issue #342): it waits for the producer to go idle before
/// the exchange takes the stream. It is deliberately <em>bounded</em>, so a device that never
/// drains cannot stall the exchange forever — and that bound is the half nothing covered, because
/// reaching it meant spending the whole budget in real time.
/// </para>
/// <para>
/// Driven through the <see cref="IOperationSerializationHost"/> seam rather than a whole device:
/// the barrier's inputs are a producer's <see cref="IMessageProducer{T}.IsIdle"/> and a budget, and
/// a device would add a transport, a consumer and a reader thread to a question that involves none
/// of them.
/// </para>
/// </remarks>
public class OperationSerializerDrainBarrierTests
{
    /// <summary>
    /// Real-time bound on the pump loops below. Nothing here is supposed to take real time; this
    /// only ever fires on a test that has already failed.
    /// </summary>
    private static readonly TimeSpan RealTimeBound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AProducerThatNeverGoesIdle_HoldsTheBarrierForTheWholeBudgetAndNoLonger()
    {
        // The bound, asserted on the budget the device actually ships (250 ms) — and, more to the
        // point, asserted precisely. A real-clock version of this test could only say "it returned
        // somewhere around 250 ms", because the alternative is a bound tight enough for a loaded
        // runner to break.
        var clock = new FakeTimeProvider();
        var host = new StubHost(clock)
        {
            OutboundDrainWait = TimeSpan.FromMilliseconds(250),
            Producer = new StubProducer { IsIdle = false },
        };
        var serializer = new OperationSerializer(host);

        var drain = serializer.DrainOutboundQueueAsync(CancellationToken.None);

        var advanced = await FakeClockPump.UntilAsync(clock, drain, TimeSpan.FromMilliseconds(10), RealTimeBound);

        Assert.True(drain.IsCompleted, "the barrier never gave up on a producer that never went idle.");
        await drain;

        // It waited out the budget rather than giving up on the first poll...
        Assert.True(
            advanced >= host.OutboundDrainWait,
            $"the barrier gave up after {advanced.TotalMilliseconds:F0}ms of device time, short of "
            + $"its {host.OutboundDrainWait.TotalMilliseconds:F0}ms budget.");

        // ...and it is bounded, so it did not keep waiting on a producer that will never idle. One
        // poll interval of slack: the loop can only notice the budget is spent on a poll boundary.
        Assert.True(
            advanced <= host.OutboundDrainWait + TimeSpan.FromMilliseconds(20),
            $"the barrier ran {advanced.TotalMilliseconds:F0}ms past a "
            + $"{host.OutboundDrainWait.TotalMilliseconds:F0}ms budget, so it is not bounded.");
    }

    [Fact]
    public async Task AProducerThatIsAlreadyIdle_IsNotWaitedOnAtAll()
    {
        // The common case, and the one the bound above must not have cost: an idle producer means
        // the exchange proceeds now, without a single poll interval of delay. Asserted without
        // advancing the clock at all — if the barrier waited even once, this never completes.
        var clock = new FakeTimeProvider();
        var host = new StubHost(clock)
        {
            OutboundDrainWait = TimeSpan.FromMinutes(30),
            Producer = new StubProducer { IsIdle = true },
        };
        var serializer = new OperationSerializer(host);

        await serializer.DrainOutboundQueueAsync(CancellationToken.None).WaitAsync(RealTimeBound);
    }

    [Fact]
    public async Task AProducerThatGoesIdlePartWayThrough_StopsWaitingThere()
    {
        // The barrier is a wait for a condition, not a fixed sleep: once the producer reports idle
        // it must return, not sit out the rest of a budget it no longer needs. With a 30-minute
        // budget on the real clock this property is simply unobservable.
        var clock = new FakeTimeProvider();
        var producer = new StubProducer { IsIdle = false };
        var host = new StubHost(clock)
        {
            OutboundDrainWait = TimeSpan.FromMinutes(30),
            Producer = producer,
        };
        var serializer = new OperationSerializer(host);

        var drain = serializer.DrainOutboundQueueAsync(CancellationToken.None);

        // A few polls in, the producer finishes its write.
        await FakeClockPump.ForAsync(clock, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(10));
        Assert.False(drain.IsCompleted, "the barrier returned while the producer was still writing.");

        producer.IsIdle = true;

        var advanced = await FakeClockPump.UntilAsync(clock, drain, TimeSpan.FromMilliseconds(10), RealTimeBound);
        await drain;

        Assert.True(
            advanced < TimeSpan.FromMinutes(1),
            $"the barrier spent {advanced.TotalSeconds:F0}s of device time after the producer went "
            + "idle, so it was sitting out its budget rather than watching the producer.");
    }

    [Fact]
    public async Task ACancelledCaller_StopsTheBarrierRatherThanWaitingItOut()
    {
        // The budget is 30 minutes here; a barrier that only observed its token between polls of a
        // real clock would be untestable at that size.
        var clock = new FakeTimeProvider();
        var host = new StubHost(clock)
        {
            OutboundDrainWait = TimeSpan.FromMinutes(30),
            Producer = new StubProducer { IsIdle = false },
        };
        var serializer = new OperationSerializer(host);

        using var cancellation = new CancellationTokenSource();
        var drain = serializer.DrainOutboundQueueAsync(cancellation.Token);

        await FakeClockPump.ForAsync(clock, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(10));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drain.WaitAsync(RealTimeBound));
    }

    /// <summary>
    /// The smallest <see cref="IOperationSerializationHost"/> the barrier needs: a budget, a
    /// producer and a clock. Everything else throws, so a change that made the barrier reach for
    /// more says so.
    /// </summary>
    private sealed class StubHost : IOperationSerializationHost
    {
        public StubHost(TimeProvider timeProvider) => TimeProvider = timeProvider;

        public TimeProvider TimeProvider { get; }

        public TimeSpan OutboundDrainWait { get; init; }

        public IMessageProducer<string>? Producer { get; init; }

        public IMessageProducer<string>? MessageProducer => Producer;

        public Microsoft.Extensions.Logging.ILogger Logger => NullLogger.Instance;

        public int MaxDeferredSends => throw new NotSupportedException();

        public void SendNow<T>(IOutboundMessage<T> message) => throw new NotSupportedException();
    }

    /// <summary>A producer whose idleness the test sets directly.</summary>
    private sealed class StubProducer : IMessageProducer<string>
    {
        private volatile bool _isIdle;

        public bool IsIdle
        {
            get => _isIdle;
            set => _isIdle = value;
        }

        public int QueuedMessageCount => IsIdle ? 0 : 1;

        public bool IsRunning => true;

        public event EventHandler<MessageSendFailedEventArgs<string>>? SendFailed
        {
            add { }
            remove { }
        }

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public bool StopSafely(int timeoutMs = 1000) => true;

        public void Send(IOutboundMessage<string> message) => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
