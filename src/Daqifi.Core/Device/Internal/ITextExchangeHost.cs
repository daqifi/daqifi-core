using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device.Protocol;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace Daqifi.Core.Device.Internal
{
    /// <summary>
    /// The slice of <see cref="DaqifiDevice"/> that <see cref="TextExchangeEngine"/> drives: the
    /// state the exchange validates against, the consumer and transport bindings it swaps, and the
    /// device's operation-lock protocol it takes part in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every member forwards to something the device already had. The device implements this
    /// explicitly, so none of it widens the public API — the same arrangement
    /// <see cref="IDeviceOperationHost"/> uses.
    /// </para>
    /// <para>
    /// <b>Why some members are documented as "must not be async".</b> Three of them —
    /// <see cref="EnterOperationLockOwnership"/>, <see cref="ExitOperationLockOwnership"/> and the
    /// <see cref="IsInsideTextExchange"/> setter — write <see cref="AsyncLocal{T}"/> slots on the
    /// device. An <see cref="AsyncLocal{T}"/> write propagates to the writing frame's <i>callees</i>,
    /// never back to its caller once an <c>await</c> has established a boundary. These therefore
    /// have to run synchronously on the engine's own frame: the engine writes them, and the engine
    /// is what then invokes the caller's prepare / setup / finalize callbacks, which is precisely who
    /// must observe them. Making any of them <c>async Task</c> would compile, pass a casual reading,
    /// and silently stop the re-entrancy guard and the nested-lock detection from seeing anything —
    /// turning a clean <see cref="InvalidOperationException"/> into a deadlock on a semaphore the
    /// flow already holds. This is the same constraint the device's own
    /// <c>RunExclusiveAsync</c> observes by assigning the slot inline rather than in a helper.
    /// </para>
    /// </remarks>
    internal interface ITextExchangeHost
    {
        /// <inheritdoc cref="DaqifiDevice.IsConnected"/>
        bool IsConnected { get; }

        /// <summary>
        /// True once the device is disposed or is tearing its connection down, in which case no
        /// exchange may start.
        /// </summary>
        bool IsShuttingDown { get; }

        /// <inheritdoc cref="DaqifiDevice.Transport"/>
        IStreamTransport? Transport { get; }

        /// <summary>
        /// The protobuf consumer the exchange stops for the duration of the swap, or <c>null</c> on
        /// a device that has none.
        /// </summary>
        IMessageConsumer<DaqifiOutMessage>? MessageConsumer { get; }

        /// <summary>
        /// Subscribes the device's inbound-message handler to <paramref name="consumer"/>.
        /// </summary>
        /// <remarks>
        /// Attach and detach are the device's own because the handler is private to it. Routing them
        /// through here rather than exposing the delegate keeps the subscription symmetric — the
        /// engine cannot accidentally remove a differently-constructed equal delegate, or leave one
        /// attached twice.
        /// </remarks>
        void AttachInboundHandler(IMessageConsumer<DaqifiOutMessage> consumer);

        /// <summary>
        /// Unsubscribes the device's inbound-message handler from <paramref name="consumer"/>.
        /// </summary>
        void DetachInboundHandler(IMessageConsumer<DaqifiOutMessage> consumer);

        /// <summary>
        /// True when the current logical flow already holds the device's operation lock — in any
        /// session — and so must run nested rather than wait for a semaphore it is itself holding.
        /// </summary>
        bool HoldsOperationLock { get; }

        /// <summary>
        /// Waits for the device's operation lock.
        /// </summary>
        /// <remarks>
        /// Deliberately does <b>not</b> translate a disposed semaphore: it throws
        /// <see cref="ObjectDisposedException"/> and lets the engine report the failure in its own
        /// words, matching what callers of the text exchange have always seen.
        /// </remarks>
        /// <exception cref="ObjectDisposedException">Thrown when a concurrent dispose already tore the lock down.</exception>
        /// <exception cref="OperationCanceledException">Thrown when cancelled while waiting.</exception>
        Task WaitForOperationLockAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Records that this flow now owns the operation lock, and starts deferring other threads'
        /// sends behind it.
        /// </summary>
        /// <remarks>
        /// <b>Must not be made async</b> — see the remarks on <see cref="ITextExchangeHost"/>.
        /// </remarks>
        void EnterOperationLockOwnership();

        /// <summary>
        /// Gives up ownership: clears this flow's claim, replays the sends parked while it ran, and
        /// releases the lock.
        /// </summary>
        /// <remarks>
        /// <b>Must not be made async</b> — see the remarks on <see cref="ITextExchangeHost"/>.
        /// </remarks>
        void ExitOperationLockOwnership();

        /// <summary>
        /// Whether the current logical flow is already inside a consumer swap — a text exchange or
        /// a raw capture. Set by the engine around both, so a callback that re-enters either one is
        /// caught rather than restarting the consumer underneath the swap that is still running.
        /// </summary>
        /// <remarks>
        /// <b>The setter must not be made async</b> — see the remarks on
        /// <see cref="ITextExchangeHost"/>.
        /// </remarks>
        bool IsInsideTextExchange { get; set; }

        /// <inheritdoc cref="OperationSerializer.DrainOutboundQueueAsync"/>
        Task DrainOutboundQueueAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Takes an instantaneous reading of the device's outbound writer.
        /// </summary>
        /// <remarks>
        /// <b>Read, never waited on.</b> The exchange samples this to tell a leftover reply from its
        /// own (issue #593); waiting for the writer to catch up — which is what draining the
        /// outbound queue amounts to — is the fix that must not be made, because it puts a fast
        /// device's genuine reply on the wrong side of the exchange's stale-line boundary. The whole
        /// point of a sample is that it costs nothing and delays nothing.
        /// <para>
        /// Called on the text consumer's reader thread as well as the exchange's own, so it must
        /// stay cheap and must not take any lock the exchange holds.
        /// </para>
        /// <para>
        /// <c>null</c> on a device with no queued producer, and on one whose producer does not count
        /// its writes. The engine treats that as "cannot tell" and falls back to what it did before.
        /// </para>
        /// </remarks>
        OutboundWriterSample? SampleOutboundWriter();

        /// <summary>
        /// Whether <paramref name="line"/> is the reply that ends the exchange now running — the
        /// answer to a query the exchange deliberately sent last so that it would have something to
        /// finish on, rather than sitting out its whole inactivity window in silence.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The engine asks rather than decides: which query terminates an exchange, and what its
        /// reply looks like, are SCPI facts that belong to the device. Here they would be the one
        /// piece of protocol knowledge in an otherwise line-protocol-generic engine.
        /// </para>
        /// <para>
        /// Answering <c>true</c> only shortens the wait; it never changes what the exchange returns,
        /// and the engine still applies its own staleness boundaries before asking at all. So a
        /// host that cannot tell should answer <c>false</c> — the exchange then waits out its
        /// completion window exactly as it did before this seam existed.
        /// </para>
        /// <para>
        /// Called on the exchange's own thread while it holds no gate, but once per newly arrived
        /// line: it must stay cheap and must not take any lock the exchange holds.
        /// </para>
        /// </remarks>
        /// <param name="line">A line collected after this exchange's commands began going out.</param>
        bool IsExchangeTerminatorReply(string line);

        /// <summary>
        /// Forwards the temporary text consumer's failures to the device's
        /// <see cref="DaqifiDevice.ErrorOccurred"/> surface for the lifetime of the returned scope.
        /// </summary>
        /// <remarks>
        /// Scoped rather than a bare subscription because the text consumer can outlive the exchange:
        /// its stop and dispose are both time-bounded and may return with the reader thread still
        /// parked in an un-returning read. A live thread roots the consumer, which would root the
        /// device through the handler.
        /// </remarks>
        IDisposable SubscribeConsumerErrors(IMessageConsumer<string> consumer);

        /// <summary>
        /// The device's logger. The engine wraps every call to it, so a throwing logger cannot take
        /// down an exchange.
        /// </summary>
        ILogger Logger { get; }

        /// <summary>
        /// Called the instant the exchange has captured its stale-line boundary, before
        /// <c>setupActionAsync</c> runs. A no-op on the real device; it exists so a test double can
        /// release a line into the transport at precisely this point — the capture-to-send window
        /// described in issue #553, which no delay-based or access-counted seam can reliably target
        /// because it is normally sub-millisecond.
        /// </summary>
        void OnStaleLineBoundaryCaptured();

        /// <summary>
        /// Called the instant the exchange has captured its send boundary — after
        /// <c>setupActionAsync</c> has returned, and before the reply wait loop starts. A no-op on
        /// the real device; the counterpart to <see cref="OnStaleLineBoundaryCaptured"/>, and it
        /// exists for the same reason.
        /// </summary>
        /// <remarks>
        /// A test that needs a line to land <em>after</em> the send boundary — i.e. one the pre-send
        /// blank rule must NOT discard (issues #553 and #593) — otherwise has to guess, because the
        /// boundary is captured within microseconds of the setup action returning and nothing about
        /// that moment is observable from the transport. Guessing means a wall-clock delay racing
        /// the exchange's own response timeout, which is a flake (issue #632). This seam replaces
        /// the guess with an ordering the runtime guarantees.
        /// </remarks>
        void OnSendBoundaryCaptured();

        /// <summary>
        /// Called once the reply wait loop has finished, reporting whether the exchange ever found
        /// evidence that the device answered — <c>true</c> when it left the loop on the short
        /// completion timeout, <c>false</c> when it sat out the whole first-response timeout in
        /// silence. A no-op on the real device, like the two seams above.
        /// </summary>
        /// <remarks>
        /// This is the fact the #538 timing tests were really asserting. They inferred it from a
        /// stopwatch — "finished well inside the response timeout, so it must have recognised the
        /// reply" — and that inference is only as good as the machine's scheduling: the wait loop
        /// polls with <c>await Task.Delay(50)</c>, so on a thread-pool-starved runner an exchange
        /// that recognised the reply on its first poll can still take seconds of wall clock to
        /// return, and the stopwatch then reports a fast path as a timed-out one (issue #634).
        /// Reporting the branch the loop actually took removes the inference, and with it the
        /// machine's load as an input to the assertion.
        /// </remarks>
        /// <param name="sawResponse">
        /// Whether anything the exchange would keep as an answer arrived before the first-response
        /// timeout expired.
        /// </param>
        void OnReplyWaitCompleted(bool sawResponse);
    }
}
