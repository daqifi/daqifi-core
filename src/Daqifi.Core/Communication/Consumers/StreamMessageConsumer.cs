using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Transport;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Daqifi.Core.Communication.Consumers;

/// <summary>
/// Stream-based message consumer that reads messages from a stream using background processing.
/// Handles line-based text protocols (like SCPI responses) and binary data parsing.
/// </summary>
/// <typeparam name="T">The type of message data to consume.</typeparam>
public class StreamMessageConsumer<T> : IMessageConsumer<T>
{
    private readonly Stream _stream;
    private readonly IMessageParser<T> _messageParser;
    private readonly ITransportHealthSink? _healthSink;
    private readonly byte[] _buffer;
    private readonly List<byte> _messageBuffer;

    /// <summary>
    /// Guards every access to <see cref="_messageBuffer"/>. The consumer thread appends to and
    /// drains the buffer while callers can query <see cref="QueuedMessageCount"/> or request a
    /// clear on their own thread; <see cref="List{T}"/> is not safe for concurrent mutation.
    /// </summary>
    private readonly object _bufferLock = new();

    /// <summary>
    /// Set by <see cref="ClearBuffer"/> when the consumer thread is running, so the clear (buffer
    /// reset + stream drain) is performed on the consumer thread itself rather than racing it.
    /// </summary>
    private volatile bool _clearRequested;

    /// <summary>
    /// Serializes <see cref="Start"/> so the check / grace-wait / publish sequence is atomic and
    /// only one reader thread can ever be spawned.
    /// </summary>
    /// <remarks>
    /// Deliberately <b>not</b> taken by <see cref="Stop"/> / <see cref="StopSafely"/>: those would
    /// then block for the whole of a concurrent start's grace wait, which is the opposite of what a
    /// stop should do. This closes the double-start hazard specifically; a caller that races
    /// <see cref="Start"/> against a stop is asking for an ill-defined result either way.
    /// <para>
    /// A <see cref="MessageReceived"/> callback that calls <see cref="Start"/> while another thread
    /// holds this lock waits, but only until that thread's grace elapses — the holder is joining the
    /// callback's own thread, so it gives up after <see cref="StaleReaderGraceMs"/> and both callers
    /// then refuse. Bounded, and only in an already re-entrant scenario.
    /// </para>
    /// </remarks>
    private readonly object _startLock = new();

    private volatile bool _isRunning;
    private Thread? _consumerThread;

    /// <summary>
    /// Set under <see cref="_startLock"/> by <see cref="Dispose"/>, but read from other threads
    /// without it (<see cref="ClearBuffer"/>), so it must be volatile for those reads to be
    /// well-defined.
    /// </summary>
    private volatile bool _disposed;

    /// <summary>
    /// Initializes a new instance of the StreamMessageConsumer class.
    /// </summary>
    /// <param name="stream">The stream to read messages from.</param>
    /// <param name="messageParser">The parser to convert raw data to messages.</param>
    /// <param name="bufferSize">The size of the read buffer in bytes.</param>
    /// <param name="healthSink">
    /// Optional transport to report read outcomes to. This reader loop is the first thing to see a
    /// device that has gone away, and a transport cannot tell from its own OS handle — so when
    /// supplied, every failed read and every successful read is reported, letting the transport
    /// escalate a persistently failing stream to a lost connection (issue #382). When null, read
    /// failures are only surfaced through <see cref="ErrorOccurred"/>, exactly as before.
    /// </param>
    public StreamMessageConsumer(Stream stream, IMessageParser<T> messageParser, int bufferSize = 4096,
        ITransportHealthSink? healthSink = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _messageParser = messageParser ?? throw new ArgumentNullException(nameof(messageParser));
        _healthSink = healthSink;
        _buffer = new byte[bufferSize];
        _messageBuffer = new List<byte>();
    }

    /// <summary>
    /// Gets a value indicating whether the consumer is currently running.
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Gets the number of bytes currently in the message buffer.
    /// </summary>
    public int QueuedMessageCount
    {
        get
        {
            lock (_bufferLock)
            {
                return _messageBuffer.Count;
            }
        }
    }

    /// <summary>
    /// Occurs when a message is received and parsed from the device.
    /// </summary>
    /// <remarks>
    /// Subscribing to this event makes the consumer snapshot its accumulation buffer on every read
    /// so that <see cref="MessageReceivedEventArgs{T}.RawData"/> can carry it. Consumers that only
    /// need the parsed message should use <see cref="MessageParsed"/> instead, which costs nothing
    /// per read — that is what Core's own subscribers do (issue #490).
    /// <para>
    /// A handler attached while a batch is already being dispatched can see an empty
    /// <see cref="MessageReceivedEventArgs{T}.RawData"/> for the remainder of that batch, because
    /// the decision to snapshot was taken before it subscribed. Every subsequent read carries the
    /// snapshot as usual.
    /// </para>
    /// </remarks>
    public event EventHandler<MessageReceivedEventArgs<T>>? MessageReceived;

    /// <summary>
    /// Occurs when a message is received and parsed, carrying only the message itself.
    /// </summary>
    /// <remarks>
    /// The raw-buffer snapshot that <see cref="MessageReceived"/> carries is a full copy of
    /// everything buffered at the time of the read — on a device streaming at kilohertz rates that
    /// is the entire wire throughput duplicated as garbage, and no subscriber inside Core has ever
    /// looked at it. This event exists so those subscribers can stop paying for it; it fires for
    /// exactly the same messages, immediately before <see cref="MessageReceived"/>, on the same
    /// reader thread and with the same per-subscriber exception isolation.
    /// </remarks>
    internal event Action<IInboundMessage<T>>? MessageParsed;

    /// <summary>
    /// Occurs when an error occurs during message processing.
    /// </summary>
    public event EventHandler<MessageConsumerErrorEventArgs>? ErrorOccurred;

    /// <summary>
    /// Grace period, in milliseconds, that <see cref="Start"/> waits for a stopped-but-not-yet-exited
    /// reader thread before refusing to start.
    /// </summary>
    /// <remarks>
    /// A prior stop already cleared <see cref="_isRunning"/>, so a still-alive reader is guaranteed
    /// to be on its way out — it exits as soon as its in-flight read returns and never issues
    /// another one. Waiting for that is therefore always correct, and it is what lets a restart
    /// succeed on the same instance instead of failing the caller (issue #383). Only a reader whose
    /// read never returns at all outlasts this, and that means the stream itself is stuck.
    /// </remarks>
    private const int StaleReaderGraceMs = 1000;

    /// <summary>
    /// Starts the message consumer, beginning background message reading.
    /// </summary>
    /// <remarks>
    /// If a previous reader thread has been stopped but has not yet exited, this waits up to
    /// <see cref="StaleReaderGraceMs"/> for it rather than failing immediately.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">Thrown when this instance has been disposed.</exception>
    /// <exception cref="ConsumerThreadNotExitedException">
    /// Thrown when a previous consumer thread is still alive after the grace period, so starting
    /// would put two concurrent readers on the same stream. A reader that outlasts the grace is one
    /// whose <see cref="Stream.Read(byte[], int, int)"/> is not returning at all, which means the
    /// stream is stuck — constructing a fresh consumer against that same stream would not help, so
    /// callers should surface the failure rather than start a second reader on it.
    /// </exception>
    public void Start()
    {
        // Serialize the whole transition — disposal check, running check, grace wait, and publish.
        // Without this, two callers racing a restart can both observe a cleared running flag, both
        // wait out the grace, and then each spawn a reader: two concurrent Stream.Read loops on one
        // stream, which is precisely what this class must never allow.
        //
        // The disposal check belongs inside the lock too. Dispose() sets _disposed under this same
        // lock, so holding it means _disposed cannot change underneath us — checking once here is
        // sufficient, and no reader can be spawned after disposal has begun.
        lock (_startLock)
        {
            ThrowIfDisposed();

            if (_isRunning)
                return; // Already running

            // A prior Stop()/StopSafely() whose Join timed out leaves the old reader thread alive.
            // Refuse to spawn a second reader against the same stream/buffer — two concurrent
            // Stream.Read loops would reintroduce the framing corruption this class guards against.
            // The stop already cleared _isRunning, so give that reader a bounded chance to finish
            // its in-flight read and exit before giving up on the caller.
            //
            // Exception: if we ARE the consumer thread (Start called from a MessageReceived callback
            // after another thread requested stop), joining would just wait on ourselves until the
            // grace elapses and then refuse anyway. Refuse immediately instead — same guarantee, no
            // pointless stall on the reader thread. Mirrors the self-join guard in ClearBuffer.
            var staleThread = _consumerThread;
            if (staleThread is { IsAlive: true }
                && (ReferenceEquals(staleThread, Thread.CurrentThread)
                    || !staleThread.Join(StaleReaderGraceMs)))
            {
                throw new ConsumerThreadNotExitedException();
            }

            _clearRequested = false;
            _isRunning = true;
            _consumerThread = new Thread(ProcessMessages)
            {
                IsBackground = true,
                Name = $"MessageConsumer-{typeof(T).Name}"
            };
            _consumerThread.Start();
        }
    }

    /// <summary>
    /// Stops the message consumer immediately.
    /// </summary>
    public void Stop()
    {
        _isRunning = false;
        var stopped = _consumerThread?.Join(1000) ?? true;
        if (stopped)
        {
            // Only tear down once the reader has actually exited: clearing the buffer (and later
            // letting Start() reuse the slot) while the thread is still alive would race it.
            _consumerThread = null;
            lock (_bufferLock)
            {
                _messageBuffer.Clear();
            }
        }
    }

    /// <summary>
    /// Stops the message consumer safely, waiting for current processing to complete.
    /// </summary>
    /// <param name="timeoutMs">Maximum time to wait for processing to complete in milliseconds.</param>
    /// <returns>True if stopped cleanly, false if timeout occurred.</returns>
    public bool StopSafely(int timeoutMs = 1000)
    {
        if (!_isRunning)
            return true;

        _isRunning = false;
        var stopped = _consumerThread?.Join(timeoutMs) ?? true;
        if (stopped)
        {
            // Only clear once the reader has exited. Doing so unconditionally would (a) let a
            // later Start() spawn a second reader over a still-alive one, and (b) block past the
            // advertised timeout here if the reader is holding _bufferLock in a slow parse.
            _consumerThread = null;
            lock (_bufferLock)
            {
                _messageBuffer.Clear();
            }
        }

        return stopped;
    }

    /// <summary>
    /// Clears any buffered data from the stream and internal buffers. Useful for devices that may
    /// have residual data on connection (e.g. after a reconnect).
    /// </summary>
    /// <remarks>
    /// Safe to call while the consumer thread is running. When it is, the actual clear is marshaled
    /// onto the consumer thread — the buffer reset and stream drain happen there — so this never
    /// mutates <see cref="_messageBuffer"/> concurrently with the reader and never issues a second
    /// <see cref="Stream.Read(byte[], int, int)"/> that would overlap the reader's own read and
    /// corrupt message framing. In that case the clear takes effect on the next consumer-loop
    /// iteration rather than synchronously on return.
    /// <para>
    /// If a consumer thread was just stopped but hasn't fully exited yet (the stop path's Join is
    /// time-bounded), the inline stream drain is deferred until that thread provably exits; if it
    /// doesn't exit in time, only the in-memory buffer is cleared and the stream drain is skipped,
    /// so the caller's <see cref="Stream.Read(byte[], int, int)"/> can never overlap the reader's.
    /// </para>
    /// </remarks>
    public void ClearBuffer()
    {
        ThrowIfDisposed();

        if (_isRunning)
        {
            // The consumer thread owns the stream and the message buffer; hand the work to it.
            _clearRequested = true;
            return;
        }

        // Not running — but a just-stopped reader may still be finishing its final Read during the
        // stop path's time-bounded Join window. Drain the stream ourselves only once that thread
        // has provably exited, so our Read can't overlap its Read.
        //
        // Exception: if we ARE the consumer thread (ClearBuffer called from a MessageReceived
        // callback after another thread requested stop), there is no other reader — joining would
        // just wait on ourselves until the timeout. Skip the join and clear directly in that case.
        var thread = _consumerThread;
        if (thread is { IsAlive: true }
            && !ReferenceEquals(thread, Thread.CurrentThread)
            && !thread.Join(1000))
        {
            // Reader still alive; don't risk an overlapping Stream.Read. Clear just the in-memory
            // buffer (always lock-guarded) and skip the stream drain.
            lock (_bufferLock)
            {
                _messageBuffer.Clear();
            }
            return;
        }

        PerformClear();
    }

    /// <summary>
    /// Resets the message buffer and drains any residual bytes from the stream. Must only run on
    /// the thread that currently owns the stream/buffer (the consumer thread while running, or the
    /// caller of <see cref="ClearBuffer"/> when it is not).
    /// </summary>
    private void PerformClear()
    {
        lock (_bufferLock)
        {
            _messageBuffer.Clear();
        }

        // Drain any available data from the stream (if it's a NetworkStream)
        try
        {
            if (_stream.CanRead && _stream is System.Net.Sockets.NetworkStream networkStream)
            {
                var tempBuffer = new byte[_buffer.Length];
                while (networkStream.DataAvailable)
                {
                    _ = _stream.Read(tempBuffer, 0, tempBuffer.Length);
                }
            }
        }
        catch
        {
            // Ignore errors during buffer clearing
        }
    }

    /// <summary>
    /// Background thread method that continuously reads and processes messages.
    /// </summary>
    private void ProcessMessages()
    {
        while (_isRunning)
        {
            try
            {
                // Honor a pending ClearBuffer() request on this thread, so the buffer reset and the
                // stream drain never race the reader below.
                if (_clearRequested)
                {
                    _clearRequested = false;
                    PerformClear();
                }

                // Probing readability is itself I/O against a handle that may be coming apart, and
                // a CanRead getter is under no obligation not to throw on one. A throwing probe is
                // a stream fault and is handled exactly like a failing read; letting it reach the
                // outer catch instead would neither tell the transport nor back off.
                bool canRead;
                try
                {
                    canRead = _stream.CanRead;
                }
                catch (Exception ex)
                {
                    if (!ReportStreamFault(ex))
                    {
                        break;
                    }

                    continue;
                }

                // A stream that reports itself unreadable never becomes readable again — that is a
                // closed or disposed stream, not a momentary lull. Report it instead of spinning
                // here forever producing no data, no error and no status change, which is the
                // failure mode issue #377 was filed for. Backed off at the same cadence as a
                // failing read so the escalation timing matches.
                if (!canRead)
                {
                    var unreadable = new IOException(
                        "The stream is no longer readable; the underlying connection has been closed.");
                    if (!ReportStreamFault(unreadable))
                    {
                        break;
                    }

                    continue;
                }

                // Try to read data from stream
                int bytesRead = 0;
                try
                {
                    bytesRead = _stream.Read(_buffer, 0, _buffer.Length);
                }
                catch (TimeoutException)
                {
                    // Expected when no data is available within ReadTimeout; just loop
                    continue;
                }
                catch (IOException ex) when (IsReadTimeout(ex))
                {
                    // A socket read that hits SO_RCVTIMEO surfaces as IOException wrapping a
                    // SocketException rather than TimeoutException, so it needs the same benign
                    // "no data yet" treatment — otherwise every idle interval on a TCP transport
                    // would raise ErrorOccurred (issue #383).
                    continue;
                }
                catch (Exception ex)
                {
                    // Tell the transport before raising the error event: a read that keeps
                    // throwing is how a physically disconnected device announces itself, and the
                    // transport is the only thing that can turn a run of them into a lost
                    // connection (issue #382). One failure escalates nothing.
                    if (!ReportStreamFault(ex))
                    {
                        break;
                    }

                    continue;
                }

                if (bytesRead == 0)
                {
                    // A NetworkStream returns 0 only after the peer has performed an orderly
                    // shutdown — the connection is over, and every subsequent read will also
                    // return 0. Report it so the transport can escalate; other stream types
                    // legitimately return 0 for "nothing right now" and must not be escalated.
                    if (_stream is NetworkStream)
                    {
                        // Same teardown rule as every other fault path, and the short back-off this
                        // path has always used — an orderly peer shutdown is detected in tens of
                        // milliseconds rather than half a second (issue #382).
                        if (!ReportStreamFault(
                                new EndOfStreamException(
                                    "The remote endpoint closed the connection (a socket read returned 0 bytes)."),
                                backoffMs: NoDataBackoffMs))
                        {
                            break;
                        }

                        continue;
                    }

                    Thread.Sleep(NoDataBackoffMs); // No data available, wait briefly
                    continue;
                }

                // A successful read clears any run of failures the transport has accumulated, so
                // a stream that glitches and recovers is never mistaken for a disconnected device.
                SafeReportIoSuccess();

                // Add received data to message buffer (guarded: a caller may be reading
                // QueuedMessageCount and ClearBuffer's drain runs on this same thread).
                // Bulk-appended: the byte-at-a-time loop this replaced re-checked the list's
                // capacity once per byte, on every read, for the life of the connection (#490).
                lock (_bufferLock)
                {
                    _messageBuffer.AddRange(new ReadOnlySpan<byte>(_buffer, 0, bytesRead));
                }

                // Try to parse complete messages from buffer
                ProcessMessageBuffer();
            }
            catch (Exception ex)
            {
                // Caught unconditionally, on purpose. This is the top of a background thread, so an
                // exception that escapes here does not merely end the loop — it terminates the
                // whole process. The previous `when (_isRunning)` filter left exactly that hole: a
                // concurrent Stop() clears the flag while the try body is mid-flight, the filter
                // then declines to match, and a parse failure that would have been a logged
                // diagnostic during normal running takes the host down instead. Only *reporting*
                // was ever meant to be conditional.
                if (!_isRunning)
                {
                    // Teardown noise: a failure seen while stopping says nothing about the device,
                    // and the loop is about to exit anyway.
                    break;
                }

                // Isolated: this is the last catch above a background thread's entry point, so a
                // throwing subscriber here has nothing left to stop it (see SafeRaiseError).
                SafeRaiseError(ex);

                // Back off before the next iteration. What reaches here is a failure in the
                // parse/dispatch half of the loop, and that half is deterministic with respect to
                // the current buffer: a parser that throws on the bytes it holds will throw on
                // exactly the same bytes next time round. Retrying at full speed is a hot spin that
                // burns a core and raises errors as fast as the thread can go, which is the same
                // no-progress-and-no-signal shape this class is being fixed for.
                //
                // Deliberately NOT reported to the health sink: a parse or dispatch failure is not
                // evidence that the link is gone, and escalating it would disconnect a perfectly
                // healthy device over malformed data. Only I/O against the stream does that — which
                // is why this path does not go through ReportStreamFault.
                Thread.Sleep(ErrorBackoffMs);
            }
        }
    }

    /// <summary>
    /// Back-off applied after a reported failure, in milliseconds.
    /// </summary>
    /// <remarks>
    /// Whatever failed will almost certainly fail again immediately — a closed handle stays closed,
    /// and a parser that rejects the bytes it holds rejects the same bytes next time — so retrying
    /// at full speed makes no progress and burns a core. Also sets the escalation cadence: with the
    /// transport's five-consecutive-failure threshold, a persistently failing stream is declared
    /// lost after roughly half a second.
    /// </remarks>
    private const int ErrorBackoffMs = 100;

    /// <summary>
    /// Pause applied when a read returns no data, in milliseconds. Shorter than
    /// <see cref="ErrorBackoffMs"/> because it is the idle path on most stream types.
    /// </summary>
    private const int NoDataBackoffMs = 10;

    /// <summary>
    /// Reports a stream-level failure to the transport and to subscribers, then backs off.
    /// </summary>
    /// <param name="error">The failure that was observed.</param>
    /// <param name="backoffMs">How long to pause before the next iteration.</param>
    /// <returns>
    /// <c>true</c> to keep reading; <c>false</c> when a stop has already been requested, in which
    /// case nothing was reported and the caller must leave the loop immediately.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Every fault path routes through here so the teardown rule is stated once. A failure observed
    /// after <see cref="_isRunning"/> has been cleared is teardown, not a device problem: closing a
    /// handle is <em>supposed</em> to make the in-flight read fail, and the stream going unreadable
    /// is what a deliberate <c>Disconnect</c> looks like from in here. Reporting it would put a
    /// phantom "the connection died" into a consumer's diagnostics and into the transport's health
    /// sink on every intentional disconnect, and the back-off would delay this thread's exit —
    /// making the stop's bounded join more likely to time out, which has its own knock-on effects.
    /// </para>
    /// <para>
    /// This is the same rule the loop's outer catch follows; keeping the two consistent is the
    /// point. Note it was never enough on its own to let a deliberate disconnect be reported as a
    /// lost connection: a <c>continue</c> re-tests the loop condition, so at most one failure can
    /// ever be reported after a stop, against an escalation threshold of five consecutive — and the
    /// transports disarm their watchdog before they touch the handle. This closes the diagnostic
    /// noise, not a hole in that guarantee.
    /// </para>
    /// </remarks>
    private bool ReportStreamFault(Exception error, int backoffMs = ErrorBackoffMs)
    {
        if (!_isRunning)
        {
            return false;
        }

        SafeReportIoFault(error);
        SafeRaiseError(error);
        Thread.Sleep(backoffMs);
        return true;
    }

    /// <summary>
    /// Reports a failed transfer to the transport, absorbing anything the sink throws.
    /// </summary>
    private void SafeReportIoFault(Exception error)
    {
        try
        {
            _healthSink?.ReportIoFault(error);
        }
        catch
        {
            // See SafeRaiseError.
        }
    }

    /// <summary>
    /// Reports a successful transfer to the transport, absorbing anything the sink throws.
    /// </summary>
    /// <remarks>
    /// Runs once per successful read, so this is deliberately a plain method rather than a lambda
    /// helper — no closure is allocated on the hot path.
    /// </remarks>
    private void SafeReportIoSuccess()
    {
        try
        {
            _healthSink?.ReportIoSuccess();
        }
        catch
        {
            // See SafeRaiseError.
        }
    }

    /// <summary>
    /// Raises <see cref="ErrorOccurred"/>, absorbing anything a subscriber throws.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every callback out of the reader loop — subscriber or transport health sink — is isolated,
    /// because this loop is the top of a background thread: an exception that escapes it does not
    /// merely stop message consumption, it terminates the process. The route is short and real. A
    /// throwing handler here propagates into the loop's outer catch, which reports the failure by
    /// calling this same handler; it throws again, and that second throw is inside a <c>catch</c>
    /// block with nothing above it. Isolating the callback closes both hops at once.
    /// </para>
    /// <para>
    /// Swallowed rather than logged, per the convention this class already follows for a throwing
    /// <see cref="MessageReceived"/> subscriber: a consumer that breaks its own diagnostics is not
    /// permitted to affect anyone else's data.
    /// </para>
    /// </remarks>
    private void SafeRaiseError(Exception error, byte[]? rawData = null)
    {
        try
        {
            OnErrorOccurred(error, rawData);
        }
        catch
        {
            // A misbehaving subscriber must not stop message consumption or take down the process.
        }
    }

    /// <summary>
    /// Determines whether an <see cref="IOException"/> raised by a read is just the stream's
    /// configured read timeout expiring with no data, rather than a real I/O fault.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow: only the exact shape <see cref="NetworkStream"/> produces when a
    /// synchronous read exceeds the socket's receive timeout. Searching deeper into the cause
    /// chain would risk classifying a genuine fault that merely happens to wrap a timeout as
    /// "no data yet", which would silently spin instead of reporting the failure.
    /// </remarks>
    private static bool IsReadTimeout(IOException exception)
    {
        return exception.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut };
    }

    /// <summary>
    /// Processes the message buffer and extracts complete messages.
    /// </summary>
    private void ProcessMessageBuffer()
    {
        byte[] bufferData;
        IEnumerable<IInboundMessage<T>> messages;

        // Snapshot, parse, and drain the buffer under the lock; dispatch events outside it so a
        // subscriber callback never runs while the lock is held.
        lock (_bufferLock)
        {
            // Parse from a view over the accumulation buffer rather than a copy of it. The copy
            // this replaced was the wire throughput duplicated as garbage on every single read
            // (issue #490); it existed only because the parser entry point demanded an array.
            var buffered = CollectionsMarshal.AsSpan(_messageBuffer);

            // The snapshot MessageReceivedEventArgs<T>.RawData carries is taken only when someone
            // is actually listening for it, and before the drain below, so it keeps its documented
            // meaning: everything that was buffered when this batch was parsed. MessageParsed
            // subscribers — which is all of Core — pay nothing.
            bufferData = MessageReceived is null ? Array.Empty<byte>() : buffered.ToArray();

            messages = _messageParser.ParseMessages(buffered, out var consumedBytes);

            // Remove consumed bytes from buffer
            if (consumedBytes > 0)
            {
                _messageBuffer.RemoveRange(0, Math.Min(consumedBytes, _messageBuffer.Count));
            }
        }

        // Fire events for parsed messages
        foreach (var message in messages)
        {
            try
            {
                OnMessageReceived(message, bufferData);
            }
            catch (Exception ex)
            {
                // A throwing MessageReceived subscriber is reported rather than propagated, so one
                // bad handler cannot starve the others of this batch. The report itself is isolated
                // too: raising it from inside a catch block leaves nowhere for a second throw to go
                // (see SafeRaiseError).
                SafeRaiseError(ex);
            }
        }
    }

    /// <summary>
    /// Raises the <see cref="MessageParsed"/> and <see cref="MessageReceived"/> events, in that
    /// order.
    /// </summary>
    /// <param name="message">The received message.</param>
    /// <param name="rawData">
    /// The raw data that was parsed. Empty when <see cref="MessageReceived"/> has no subscribers:
    /// the snapshot is only taken for that event, so nothing observable is lost, but an override
    /// that reads it must attach a <see cref="MessageReceived"/> handler (or call
    /// <c>base</c>) rather than assume it is always populated.
    /// </param>
    protected virtual void OnMessageReceived(IInboundMessage<T> message, byte[] rawData)
    {
        try
        {
            MessageParsed?.Invoke(message);
        }
        catch (Exception ex)
        {
            // Isolated from the public event on purpose. MessageParsed is what Core's own
            // subscribers ride, so without this an internal handler that threw would silently
            // withhold the message from an external MessageReceived subscriber that had nothing to
            // do with the failure. Reported through the same channel as any other dispatch fault.
            SafeRaiseError(ex);
        }

        // Deliberately not wrapped: a throwing MessageReceived subscriber propagates to
        // ProcessMessageBuffer's per-message catch exactly as it did before this event existed,
        // which is where it has always been reported from.
        MessageReceived?.Invoke(this, new MessageReceivedEventArgs<T>(message, rawData));
    }

    /// <summary>
    /// Raises the ErrorOccurred event.
    /// </summary>
    /// <param name="error">The error that occurred.</param>
    /// <param name="rawData">The raw data being processed when the error occurred.</param>
    protected virtual void OnErrorOccurred(Exception error, byte[]? rawData = null)
    {
        ErrorOccurred?.Invoke(this, new MessageConsumerErrorEventArgs(error, rawData));
    }

    /// <summary>
    /// Throws ObjectDisposedException if this instance has been disposed.
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(StreamMessageConsumer<T>));
    }

    /// <summary>
    /// Disposes the message consumer and releases resources.
    /// </summary>
    /// <remarks>
    /// Marks disposal under <see cref="_startLock"/> and <em>before</em> stopping, so a concurrent
    /// <see cref="Start"/> cannot spawn a reader that outlives this call: either it already holds
    /// the lock (and we wait out its grace, then stop the reader it started), or it acquires the
    /// lock afterwards and fails its disposal check. Only the flag is set under the lock — the
    /// stop itself runs outside it, so teardown never holds the lock while joining a reader.
    /// </remarks>
    public void Dispose()
    {
        lock (_startLock)
        {
            if (_disposed)
                return;

            _disposed = true;
        }

        // StopSafely short-circuits — returning true without joining — when the consumer is already
        // stopped. That is exactly the state a timed-out stop leaves behind: _isRunning false, the
        // reader still alive in an in-flight read. Left as-is, Dispose would return without ever
        // waiting on that thread. Note whether it was running so the extra wait applies only to the
        // short-circuit case, rather than stacking a second grace on top of StopSafely's own join.
        var wasRunning = _isRunning;
        StopSafely();

        if (!wasRunning)
        {
            JoinStaleReader();
        }
    }

    /// <summary>
    /// Waits a bounded time for a stopped-but-not-yet-exited reader to finish its in-flight read
    /// and exit.
    /// </summary>
    /// <remarks>
    /// Skips the wait when called from the reader itself (for example a <see cref="Dispose"/> from
    /// inside a <see cref="MessageReceived"/> handler), where joining would only stall that thread
    /// until the timeout — the same self-join guard <see cref="Start"/> and <see cref="ClearBuffer"/>
    /// carry.
    /// </remarks>
    private void JoinStaleReader()
    {
        var thread = _consumerThread;
        if (thread is { IsAlive: true } && !ReferenceEquals(thread, Thread.CurrentThread))
        {
            thread.Join(StaleReaderGraceMs);
        }
    }
}