namespace Daqifi.Core.Communication.Consumers;

/// <summary>
/// Thrown by <see cref="StreamMessageConsumer{T}.Start"/> when a previous consumer thread has not
/// yet exited, so starting a new reader would put two concurrent <see cref="Stream.Read(byte[], int, int)"/>
/// loops on the same stream.
/// </summary>
/// <remarks>
/// Derives from <see cref="InvalidOperationException"/> so existing callers that catch the broader
/// type keep working; the distinct type lets callers that own the consumer recover by abandoning
/// the stale instance and constructing a fresh one against the current stream, rather than
/// surfacing an unactionable internal error (issue #383).
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
