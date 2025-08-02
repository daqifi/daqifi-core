using Daqifi.Core.Communication.Messages;

namespace Daqifi.Core.Communication.Consumers;

/// <summary>
/// Interface for message consumers that handle reading and processing inbound messages from devices.
/// </summary>
/// <typeparam name="T">The type of message data to consume.</typeparam>
public interface IMessageConsumer<T> : IDisposable
{
    /// <summary>
    /// Gets a value indicating whether the consumer is currently running.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Gets the number of messages currently in the processing queue.
    /// </summary>
    int QueuedMessageCount { get; }

    /// <summary>
    /// Occurs when a message is received and parsed from the device.
    /// </summary>
    event EventHandler<MessageReceivedEventArgs<T>>? MessageReceived;

    /// <summary>
    /// Occurs when an error occurs during message processing.
    /// </summary>
    event EventHandler<MessageConsumerErrorEventArgs>? ErrorOccurred;

    /// <summary>
    /// Starts the message consumer, beginning background message reading.
    /// </summary>
    void Start();

    /// <summary>
    /// Stops the message consumer immediately.
    /// </summary>
    void Stop();

    /// <summary>
    /// Stops the message consumer safely, waiting for current processing to complete.
    /// </summary>
    /// <param name="timeoutMs">Maximum time to wait for processing to complete in milliseconds.</param>
    /// <returns>True if stopped cleanly, false if timeout occurred.</returns>
    bool StopSafely(int timeoutMs = 1000);
}