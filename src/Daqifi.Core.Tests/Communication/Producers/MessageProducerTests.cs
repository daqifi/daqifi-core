using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
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
    public void MessageProducer_WhenWriteThrows_ShouldRaiseSendFailed()
    {
        // Arrange
        using var stream = new ThrowOnWriteStream();
        using var producer = new MessageProducer<string>(stream);
        MessageSendFailedEventArgs<string>? captured = null;
        producer.SendFailed += (_, e) => captured = e;
        producer.Start();

        // Act - the background thread will attempt to write and fail
        var message = new ScpiMessage("TEST:COMMAND");
        producer.Send(message);
        var raised = SpinWait.SpinUntil(() => captured != null, TimeSpan.FromSeconds(2));
        producer.StopSafely();

        // Assert - the caller has a way to observe that this specific message was not delivered
        Assert.True(raised, "Expected SendFailed to be raised when the stream write throws.");
        Assert.Same(message, captured!.Message);
        Assert.IsType<IOException>(captured.Error);
        Assert.False(captured.IsTimeout);
    }

    [Fact]
    public void MessageProducer_WhenWriteTimesOut_ShouldRaiseSendFailedWithIsTimeoutTrue()
    {
        // Arrange
        using var stream = new ThrowOnWriteStream(new TimeoutException("Simulated write timeout."));
        var logger = new CaptureLogger<MessageProducer<string>>();
        using var producer = new MessageProducer<string>(stream, logger);
        MessageSendFailedEventArgs<string>? captured = null;
        producer.SendFailed += (_, e) => captured = e;
        producer.Start();

        // Act
        producer.Send(new ScpiMessage("TEST:COMMAND"));
        var raised = SpinWait.SpinUntil(() => captured != null, TimeSpan.FromSeconds(2));
        producer.StopSafely();

        // Assert - a timeout is distinguishable both on the event and in the log text, so the
        // "device is busy" case can be told apart from any other delivery failure (issue #408).
        Assert.True(raised, "Expected SendFailed to be raised when the stream write times out.");
        Assert.True(captured!.IsTimeout);
        var warning = logger.Entries.First(e => e.Level == LogLevel.Warning);
        Assert.Contains("Timed out", warning.Message);
    }

    [Fact]
    public void MessageProducer_WhenSendFailedSubscriberThrows_ShouldKeepDrainingAndStopCleanly()
    {
        // Arrange - a subscriber that throws must not be allowed to take down the background loop,
        // mirroring the existing guarantee for a faulting ILogger.
        using var stream = new ThrowOnWriteStream();
        using var producer = new MessageProducer<string>(stream);
        producer.SendFailed += (_, _) => throw new InvalidOperationException("Simulated subscriber failure.");
        producer.Start();

        // Act
        for (var i = 0; i < 5; i++)
        {
            producer.Send(new ScpiMessage($"CMD{i}"));
        }
        var stopped = producer.StopSafely(2000);

        // Assert
        Assert.True(stopped, "StopSafely should not hang when a SendFailed subscriber throws.");
        Assert.False(producer.IsRunning);
        Assert.Equal(0, producer.QueuedMessageCount);
    }

    [Fact]
    public void MessageProducer_Send_WhenSucceeding_ShouldNotRaiseSendFailed()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var producer = new MessageProducer<string>(stream);
        var raised = false;
        producer.SendFailed += (_, _) => raised = true;
        producer.Start();

        // Act
        producer.Send(new ScpiMessage("TEST:COMMAND"));
        producer.StopSafely();

        // Assert
        Assert.False(raised);
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

    [Fact]
    public void MessageProducer_WhenLoggerThrows_ShouldKeepDrainingAndStopCleanly()
    {
        // Arrange - a logger that always throws, combined with writes that always fail,
        // exercises the logging path on the background thread on every message.
        using var stream = new ThrowOnWriteStream();
        var logger = new ThrowingLogger<MessageProducer<string>>();
        using var producer = new MessageProducer<string>(stream, logger);
        producer.Start();

        // Act - queue several messages; each failed write triggers a logging call that throws.
        for (int i = 0; i < 5; i++)
        {
            producer.Send(new ScpiMessage($"CMD{i}"));
        }

        var stopped = producer.StopSafely(2000);

        // Assert - a faulting logger must not kill the loop: the queue still drains, the
        // producer stops within the timeout, and it never reports a stale running state.
        Assert.True(stopped, "StopSafely should not hang when the logger throws.");
        Assert.False(producer.IsRunning);
        Assert.Equal(0, producer.QueuedMessageCount);
    }

    [Fact]
    public void MessageProducer_StartedWriteCount_CountsAWriteAsSoonAsItBegins()
    {
        // The number has to rise BEFORE the bytes go out, not after they are done, because a
        // reader on another thread uses it to conclude "nothing had been asked yet" (issue #593).
        // A count of completions would still read zero while a command was physically on the wire,
        // and a device that answered in the meantime would have its reply written off as a
        // leftover. The write is held open here so the difference is observable at all.
        using var stream = new BlockingWriteStream();
        using var producer = new MessageProducer<string>(stream);

        Assert.Equal(0L, producer.StartedWriteCount);

        producer.Start();
        producer.Send(new ScpiMessage("TEST:COMMAND"));

        Assert.True(stream.WaitForWriteStarted(TimeSpan.FromSeconds(5)), "The producer never started the write.");

        // Still inside the blocking write, so this write has begun and has not finished.
        Assert.Equal(1L, producer.StartedWriteCount);

        stream.ReleaseWrite();
        Assert.False(
            stream.HoldExpired,
            "The write hold expired on its own bound, so the write was no longer in flight when the count was read.");

        Assert.True(producer.StopSafely(2000));
        Assert.Equal(1L, producer.StartedWriteCount);
    }

    [Fact]
    public void MessageProducer_StartedWriteCount_CountsAWriteThatFailed()
    {
        // A write that threw part-way may still have put bytes on the wire, so "it never started"
        // is the answer that could get a genuine reply discarded. Counting it is the safe way to
        // be wrong: at worst a leftover reply is kept, which is what happened before #593 anyway.
        using var stream = new ThrowOnWriteStream();
        using var producer = new MessageProducer<string>(stream);
        producer.Start();

        producer.Send(new ScpiMessage("TEST:COMMAND"));
        Assert.True(producer.StopSafely(2000));

        Assert.Equal(1L, producer.StartedWriteCount);
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
    /// A logger whose every <see cref="Log"/> call throws, used to verify the producer's
    /// background loop survives a faulting logging provider.
    /// </summary>
    private sealed class ThrowingLogger<TCategory> : ILogger<TCategory>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("Simulated logger failure.");
    }

    /// <summary>
    /// A write-only stream whose <see cref="Write"/> always throws, simulating a mid-stream failure.
    /// </summary>
    private sealed class ThrowOnWriteStream : Stream
    {
        private readonly Exception _exceptionToThrow;

        public ThrowOnWriteStream(Exception? exceptionToThrow = null)
        {
            _exceptionToThrow = exceptionToThrow ?? new IOException("Simulated write failure for error-handling test.");
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw _exceptionToThrow;
    }

    /// <summary>
    /// A write-only stream that parks inside <see cref="Write"/> until released, so a test can
    /// observe the producer's state while a write is genuinely in flight.
    /// </summary>
    private sealed class BlockingWriteStream : Stream
    {
        /// <summary>
        /// A plain monitor rather than a pair of events, so there is nothing here that has to be
        /// disposed: disposable wait handles would need the parked writer to have left them before
        /// teardown could free them, which is a race a test double should not have.
        /// </summary>
        private readonly object _gate = new();
        private bool _writeStarted;
        private bool _released;
        private bool _holdExpired;

        /// <summary>
        /// True if the held write gave up on its bound instead of being released by the test.
        /// The bound only exists so a test that never releases fails on its own assertion rather
        /// than wedging the producer thread for the rest of the run; if it ever does expire, the
        /// write was no longer in flight and anything read about it afterwards is meaningless.
        /// Recorded rather than thrown: this runs on the producer's thread, where an exception
        /// would be swallowed into SendFailed and the failure-run counter instead of reaching the
        /// test.
        /// </summary>
        public bool HoldExpired
        {
            get
            {
                lock (_gate)
                {
                    return _holdExpired;
                }
            }
        }

        public bool WaitForWriteStarted(TimeSpan timeout)
        {
            // Monotonic on purpose: a wall-clock step (NTP, a VM resuming) must not be able to
            // cut this wait short or stretch it, which is how a bounded test wait turns flaky.
            var clock = Stopwatch.StartNew();

            lock (_gate)
            {
                while (!_writeStarted)
                {
                    var remaining = timeout - clock.Elapsed;
                    if (remaining <= TimeSpan.Zero)
                    {
                        return false;
                    }

                    Monitor.Wait(_gate, remaining);
                }

                return true;
            }
        }

        public void ReleaseWrite()
        {
            lock (_gate)
            {
                _released = true;
                Monitor.PulseAll(_gate);
            }
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            var limit = TimeSpan.FromSeconds(10);
            var clock = Stopwatch.StartNew();

            lock (_gate)
            {
                _writeStarted = true;
                Monitor.PulseAll(_gate);

                // Bounded: a test that forgets to release must fail on its own assertion rather
                // than wedge the producer thread for the rest of the run. Expiry is recorded so it
                // cannot pass for a release: see HoldExpired.
                while (!_released)
                {
                    var remaining = limit - clock.Elapsed;
                    if (remaining <= TimeSpan.Zero)
                    {
                        _holdExpired = true;
                        return;
                    }

                    Monitor.Wait(_gate, remaining);
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Frees a writer that a failed test left parked; nothing here needs disposing.
                ReleaseWrite();
            }

            base.Dispose(disposing);
        }
    }
}