using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Transport;
using System.Text;

namespace Daqifi.Core.Tests.Communication.Consumers;

/// <summary>
/// Issue #382: the reader loop is the first component to see a device that has gone away, so it
/// must tell the transport — both when reads keep failing (a disconnect) and when a read succeeds
/// again (a blip that must not disconnect anything).
/// </summary>
public class StreamMessageConsumerHealthReportingTests
{
    [Fact]
    public void ReaderLoop_WhenStreamStartsFailing_ReportsFaultsToTheTransport()
    {
        using var stream = new ScriptedStream();
        var sink = new RecordingHealthSink();
        using var consumer = new StreamMessageConsumer<string>(
            stream, new LineBasedMessageParser(), healthSink: sink);

        stream.FailReads = true;
        consumer.Start();

        Assert.True(WaitUntil(() => sink.FaultCount >= 3, TimeSpan.FromSeconds(5)),
            $"expected repeated fault reports, saw {sink.FaultCount}");
        Assert.Equal(0, sink.SuccessCount);
        Assert.All(sink.Faults, ex => Assert.IsType<IOException>(ex));

        consumer.StopSafely(timeoutMs: 2000);
    }

    [Fact]
    public void ReaderLoop_WhenAFailingStreamRecovers_ReportsSuccessSoTheBlipIsNotADisconnect()
    {
        using var stream = new ScriptedStream();
        var sink = new RecordingHealthSink();
        using var consumer = new StreamMessageConsumer<string>(
            stream, new LineBasedMessageParser(), healthSink: sink);

        stream.FailReads = true;
        consumer.Start();

        Assert.True(WaitUntil(() => sink.FaultCount >= 1, TimeSpan.FromSeconds(5)));

        // The blip ends: reads work again.
        stream.Enqueue("$DAQiFi\r\n");
        stream.FailReads = false;

        Assert.True(WaitUntil(() => sink.SuccessCount >= 1, TimeSpan.FromSeconds(5)),
            "a successful read must be reported so the transport clears the failure run");

        consumer.StopSafely(timeoutMs: 2000);
    }

    [Fact]
    public void ReaderLoop_OnATimeout_ReportsNeitherFaultNorSuccess()
    {
        // An idle read timeout is the normal state of a quiet device, not evidence of anything.
        using var stream = new ScriptedStream();
        var sink = new RecordingHealthSink();
        using var consumer = new StreamMessageConsumer<string>(
            stream, new LineBasedMessageParser(), healthSink: sink);

        stream.TimeoutReads = true;
        consumer.Start();

        Assert.True(WaitUntil(() => stream.ReadCount >= 5, TimeSpan.FromSeconds(5)));
        Assert.Equal(0, sink.FaultCount);
        Assert.Equal(0, sink.SuccessCount);

        consumer.StopSafely(timeoutMs: 2000);
    }

    [Fact]
    public void ReaderLoop_OnAZeroByteReadFromANonSocketStream_ReportsNoFault()
    {
        // Plenty of stream types return 0 for "nothing right now"; only a socket's 0 means closed.
        using var stream = new ScriptedStream();
        var sink = new RecordingHealthSink();
        using var consumer = new StreamMessageConsumer<string>(
            stream, new LineBasedMessageParser(), healthSink: sink);

        consumer.Start();

        Assert.True(WaitUntil(() => stream.ReadCount >= 5, TimeSpan.FromSeconds(5)));
        Assert.Equal(0, sink.FaultCount);

        consumer.StopSafely(timeoutMs: 2000);
    }

    [Fact]
    public void ReaderLoop_WithNoHealthSink_BehavesExactlyAsBefore()
    {
        // The feedback path is opt-in; a consumer constructed without one still just raises
        // ErrorOccurred and keeps going.
        using var stream = new ScriptedStream();
        var errors = 0;
        using var consumer = new StreamMessageConsumer<string>(stream, new LineBasedMessageParser());
        consumer.ErrorOccurred += (_, _) => Interlocked.Increment(ref errors);

        stream.FailReads = true;
        consumer.Start();

        Assert.True(WaitUntil(() => Volatile.Read(ref errors) >= 2, TimeSpan.FromSeconds(5)));
        Assert.True(consumer.IsRunning);

        consumer.StopSafely(timeoutMs: 2000);
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return condition();
    }

    /// <summary>
    /// Records what the reader loop reports, standing in for a real transport.
    /// </summary>
    private sealed class RecordingHealthSink : ITransportHealthSink
    {
        private readonly object _gate = new();
        private readonly List<Exception> _faults = [];
        private int _successCount;

        public int FaultCount
        {
            get
            {
                lock (_gate)
                {
                    return _faults.Count;
                }
            }
        }

        public IReadOnlyList<Exception> Faults
        {
            get
            {
                lock (_gate)
                {
                    return _faults.ToArray();
                }
            }
        }

        public int SuccessCount => Volatile.Read(ref _successCount);

        public void ReportIoFault(Exception error)
        {
            lock (_gate)
            {
                _faults.Add(error);
            }
        }

        public void ReportIoSuccess() => Interlocked.Increment(ref _successCount);
    }

    /// <summary>
    /// A stream whose reads can be made to fail, time out, or deliver scripted data, so a
    /// "connection that starts failing" can be reproduced without hardware.
    /// </summary>
    private sealed class ScriptedStream : Stream
    {
        private readonly Queue<byte[]> _pending = new();
        private readonly object _gate = new();
        private int _readCount;

        public volatile bool FailReads;
        public volatile bool TimeoutReads;

        public int ReadCount => Volatile.Read(ref _readCount);

        public void Enqueue(string text)
        {
            lock (_gate)
            {
                _pending.Enqueue(Encoding.UTF8.GetBytes(text));
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Interlocked.Increment(ref _readCount);

            if (TimeoutReads)
            {
                Thread.Sleep(5);
                throw new TimeoutException("no data within the read timeout");
            }

            if (FailReads)
            {
                Thread.Sleep(5);
                throw new IOException("the device is gone");
            }

            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    return 0;
                }

                var chunk = _pending.Dequeue();
                var length = Math.Min(chunk.Length, count);
                Array.Copy(chunk, 0, buffer, offset, length);
                return length;
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
