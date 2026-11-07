namespace Daqifi.Core.Communication.Producers;

/// <summary>
/// Thrown by <see cref="MessageProducer{T}.Start"/> when a previous writer thread has not yet
/// exited, so starting a new one would put two concurrent
/// <see cref="Stream.Write(byte[], int, int)"/> loops on the same stream.
/// </summary>
/// <remarks>
/// Derives from <see cref="InvalidOperationException"/> so callers that catch the broader type keep
/// working; the distinct type lets callers recognize this specific condition rather than
/// pattern-matching on a message. This is the writer-side sibling of
/// <see cref="Consumers.ConsumerThreadNotExitedException"/>.
/// <para>
/// <see cref="MessageProducer{T}.Start"/> only raises this after waiting a grace period for the
/// stopped writer to exit, so it means the writer's write is not returning at all: the stream
/// itself is stuck. Calling <see cref="MessageProducer{T}.Start"/> again once the write has returned
/// is valid. Constructing a fresh producer against that same stream is <b>not</b> a valid recovery:
/// it would be a second concurrent writer on it, which can interleave two commands on the wire.
/// </para>
/// </remarks>
public class ProducerThreadNotExitedException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProducerThreadNotExitedException"/> class.
    /// </summary>
    public ProducerThreadNotExitedException()
        : base("Cannot start the producer: a previous producer thread has not yet exited.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ProducerThreadNotExitedException"/> class
    /// with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public ProducerThreadNotExitedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ProducerThreadNotExitedException"/> class
    /// with a specified error message and inner exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that caused this exception.</param>
    public ProducerThreadNotExitedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
