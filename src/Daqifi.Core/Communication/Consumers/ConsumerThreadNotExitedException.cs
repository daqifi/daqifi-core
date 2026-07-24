namespace Daqifi.Core.Communication.Consumers;

/// <summary>
/// Thrown by <see cref="StreamMessageConsumer{T}.Start"/> when a previous consumer thread has not
/// yet exited, so starting a new reader would put two concurrent <see cref="Stream.Read(byte[], int, int)"/>
/// loops on the same stream.
/// </summary>
/// <remarks>
/// Derives from <see cref="InvalidOperationException"/> so existing callers that catch the broader
/// type keep working; the distinct type lets callers recognize this specific condition rather than
/// pattern-matching on a message (issue #383).
/// <para>
/// <see cref="StreamMessageConsumer{T}.Start"/> only raises this after waiting a grace period for
/// the stopped reader to exit, so it means the reader's read is not returning at all — the stream
/// itself is stuck. Constructing a fresh consumer against that same stream is <b>not</b> a valid
/// recovery: it would be a second concurrent reader on it, which is the framing corruption the
/// guard prevents, and it would block on the stuck stream just the same.
/// </para>
/// </remarks>
public class ConsumerThreadNotExitedException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConsumerThreadNotExitedException"/> class.
    /// </summary>
    public ConsumerThreadNotExitedException()
        : base("Cannot start the consumer: a previous consumer thread has not yet exited.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConsumerThreadNotExitedException"/> class
    /// with a specified error message.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    public ConsumerThreadNotExitedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConsumerThreadNotExitedException"/> class
    /// with a specified error message and inner exception.
    /// </summary>
    /// <param name="message">The message that describes the error.</param>
    /// <param name="innerException">The exception that caused this exception.</param>
    public ConsumerThreadNotExitedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
