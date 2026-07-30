using Daqifi.Core.Communication.Messages;

namespace Daqifi.Core.Communication.Producers;

/// <summary>
/// Interface for message producers that handle queuing and sending messages to devices.
/// </summary>
/// <typeparam name="T">The type of message data to produce.</typeparam>
public interface IMessageProducer<T> : IDisposable
{
    /// <summary>
    /// Starts the message producer, beginning background message processing.
    /// </summary>
    void Start();

    /// <summary>
    /// Stops the message producer immediately, clearing any pending messages.
    /// </summary>
    void Stop();

    /// <summary>
    /// Stops the message producer safely, waiting for pending messages to be sent.
    /// </summary>
    /// <param name="timeoutMs">Maximum time to wait for pending messages in milliseconds.</param>
    /// <returns>True if all messages were sent, false if timeout occurred.</returns>
    bool StopSafely(int timeoutMs = 1000);

    /// <summary>
    /// Queues a message for sending to the device.
    /// </summary>
    /// <remarks>
    /// This is fire-and-forget: the message is handed to a background thread and this call
    /// returns before the write happens, so delivery is not guaranteed. A write that fails —
    /// including one that never reaches the device — does not throw back to the caller; it is
    /// only observable via <see cref="SendFailed"/> (issue #408).
    /// </remarks>
    /// <param name="message">The message to send.</param>
    void Send(IOutboundMessage<T> message);

    /// <summary>
    /// Occurs when a queued message fails to write to the underlying stream.
    /// </summary>
    /// <remarks>
    /// <see cref="Send"/> never throws for a failed write, so this is the only signal a caller
    /// gets that a specific message was not delivered. Raised on the producer's background
    /// thread; the producer keeps draining the remaining queue regardless of subscribers.
    /// </remarks>
    event EventHandler<MessageSendFailedEventArgs<T>>? SendFailed;

    /// <summary>
    /// Gets the number of messages currently queued for sending.
    /// </summary>
    int QueuedMessageCount { get; }

    /// <summary>
    /// Gets a value indicating whether the producer is currently running.
    /// </summary>
    bool IsRunning { get; }
}