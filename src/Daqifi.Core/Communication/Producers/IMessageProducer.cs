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
    /// <param name="message">The message to send.</param>
    void Send(IOutboundMessage<T> message);

    /// <summary>
    /// Gets the number of messages currently queued for sending.
    /// </summary>
    int QueuedMessageCount { get; }

    /// <summary>
    /// Gets a value indicating whether the producer is currently running.
    /// </summary>
    bool IsRunning { get; }
}