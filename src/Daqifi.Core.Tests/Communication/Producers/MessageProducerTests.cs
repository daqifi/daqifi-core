using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text;

namespace Daqifi.Core.Tests.Communication.Producers;

public class MessageProducerTests
{
    [Fact]
    public void MessageProducer_Constructor_ShouldInitializeCorrectly()
    {
        // Arrange
        using var stream = new MemoryStream();
        
        // Act
        using var producer = new MessageProducer<string>(stream);
        
        // Assert
        Assert.Equal(0, producer.QueuedMessageCount);
        Assert.False(producer.IsRunning);
    }

    [Fact]
    public void MessageProducer_Start_ShouldSetRunningState()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var producer = new MessageProducer<string>(stream);
        
        // Act
        producer.Start();
        
        // Assert
        Assert.True(producer.IsRunning);
    }

    [Fact]
    public void MessageProducer_Stop_ShouldClearRunningState()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var producer = new MessageProducer<string>(stream);
        producer.Start();
        
        // Act
        producer.Stop();
        
        // Assert
        Assert.False(producer.IsRunning);
    }

    [Fact]
    public void MessageProducer_Send_WhenRunning_ShouldWriteToStream()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var producer = new MessageProducer<string>(stream);
        var message = new ScpiMessage("TEST:COMMAND");
        
        producer.Start();
        
        // Act
        producer.Send(message);
        
        // Stop safely to ensure all messages are processed
        producer.StopSafely();
        
        // Assert
        var written = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains("TEST:COMMAND", written);
    }

    [Fact]
    public void MessageProducer_Send_WhenNotRunning_ShouldThrowException()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var producer = new MessageProducer<string>(stream);
        var message = new ScpiMessage("TEST:COMMAND");
        
        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => producer.Send(message));
    }

    [Fact]
    public void MessageProducer_Send_WithNullMessage_ShouldThrowException()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var producer = new MessageProducer<string>(stream);
        producer.Start();
        
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => producer.Send(null!));
    }

    [Fact]
    public void MessageProducer_StopSafely_ShouldProcessRemainingMessages()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var producer = new MessageProducer<string>(stream);
        var message1 = new ScpiMessage("COMMAND1");
        var message2 = new ScpiMessage("COMMAND2");
        
        producer.Start();
        producer.Send(message1);
        producer.Send(message2);
        
        // Act
        var result = producer.StopSafely(2000); // Give extra time for background thread
        
        // Assert
        Assert.True(result);
        var written = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains("COMMAND1", written);
        Assert.Contains("COMMAND2", written);
    }

    [Fact]
    public void MessageProducer_BackgroundThreading_ShouldProcessMessagesAsynchronously()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var producer = new MessageProducer<string>(stream);
        var message = new ScpiMessage("ASYNC:TEST");
        
        producer.Start();
        
        // Act
        producer.Send(message);
        
        // Stop safely to ensure all messages are processed
        producer.StopSafely();
        
        // Assert
        var written = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains("ASYNC:TEST", written);
    }

    [Fact]
    public void MessageProducer_MultipleMessages_ShouldProcessInOrder()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var producer = new MessageProducer<string>(stream);
        
        producer.Start();
        
        // Act - Send multiple messages quickly
        for (int i = 1; i <= 5; i++)
        {
            producer.Send(new ScpiMessage($"MESSAGE{i}"));
        }
        
        // Wait for processing
        Thread.Sleep(50);
        producer.StopSafely();
        
        // Assert - All messages should be written
        var written = Encoding.UTF8.GetString(stream.ToArray());
        for (int i = 1; i <= 5; i++)
        {
            Assert.Contains($"MESSAGE{i}", written);
        }
    }

    [Fact]
    public void MessageProducer_Start_WhenAlreadyRunning_ShouldNotCreateMultipleThreads()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var producer = new MessageProducer<string>(stream);
        
        // Act
        producer.Start();
        Assert.True(producer.IsRunning);
        
        producer.Start(); // Call again
        Assert.True(producer.IsRunning); // Should still be running
        
        // Should work normally
        producer.Send(new ScpiMessage("TEST"));
        Thread.Sleep(20);
        
        producer.StopSafely();
        
        // Assert
        var written = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains("TEST", written);
    }

    [Fact]
    public void MessageProducer_WhenWriteThrows_ShouldLogWarning()
    {
        // Arrange
        using var stream = new ThrowOnWriteStream();
        var logger = new CaptureLogger<MessageProducer<string>>();
        using var producer = new MessageProducer<string>(stream, logger);
        producer.Start();

        // Act - the background thread will attempt to write and fail
        producer.Send(new ScpiMessage("TEST:COMMAND"));
        var warningLogged = SpinWait.SpinUntil(
            () => logger.Entries.Any(e => e.Level == LogLevel.Warning),
            TimeSpan.FromSeconds(2));
        producer.StopSafely();

        // Assert - the write failure is surfaced through the logger, not swallowed
        Assert.True(warningLogged, "Expected a warning to be logged when the stream write throws.");
        var warning = logger.Entries.First(e => e.Level == LogLevel.Warning);
        Assert.IsType<IOException>(warning.Exception);
    }

    [Fact]
    public void MessageProducer_WithNoLogger_WhenWriteThrows_ShouldNotThrowToCaller()
    {
        // Arrange - omitting the logger must preserve the original silent-continue behavior
        using var stream = new ThrowOnWriteStream();
        using var producer = new MessageProducer<string>(stream);
        producer.Start();

        // Act & Assert - a failing write on the background thread must not surface to the caller
        producer.Send(new ScpiMessage("TEST:COMMAND"));
        Thread.Sleep(50);
        Assert.True(producer.IsRunning);
        Assert.True(producer.StopSafely());
    }

    [Fact]
    public void MessageProducer_WhenStoppedNormally_ShouldLogCleanExit()
    {
        // Arrange
        using var stream = new MemoryStream();
        var logger = new CaptureLogger<MessageProducer<string>>();
        using var producer = new MessageProducer<string>(stream, logger);
        producer.Start();

        // Act
        producer.StopSafely();

        // Assert - the background loop reports a clean lifecycle exit
        var infoLogged = SpinWait.SpinUntil(
            () => logger.Entries.Any(e => e.Level == LogLevel.Information),
            TimeSpan.FromSeconds(2));
        Assert.True(infoLogged, "Expected an information log when the background loop exits cleanly.");
    }

    /// <summary>
    /// Captures log entries in-memory so tests can assert that the producer logs as expected.
    /// </summary>
    private sealed class CaptureLogger<TCategory> : ILogger<TCategory>
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _entries.Enqueue(new LogEntry(logLevel, exception, formatter(state, exception)));
        }

        public sealed record LogEntry(LogLevel Level, Exception? Exception, string Message);
    }

    /// <summary>
    /// A write-only stream whose <see cref="Write"/> always throws, simulating a mid-stream failure.
    /// </summary>
    private sealed class ThrowOnWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new IOException("Simulated write failure for error-handling test.");
    }
}