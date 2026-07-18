using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device.Protocol;
using Daqifi.Core.Firmware;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace Daqifi.Core.Device
{
    /// <summary>
    /// Represents a DAQiFi device that can be connected to and communicated with.
    /// This is the base implementation of the IDevice interface.
    /// </summary>
    public class DaqifiDevice : IDevice, IDisposable
    {
        /// <summary>
        /// Gets the name of the device.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the IP address of the device, if known.
        /// </summary>
        public IPAddress? IpAddress { get; }

        /// <summary>
        /// Gets a value indicating whether the device is currently connected.
        /// </summary>
        public bool IsConnected => Status == ConnectionStatus.Connected;

        /// <summary>
        /// Gets the device metadata containing part number, firmware version, etc.
        /// </summary>
        public DeviceMetadata Metadata { get; } = new DeviceMetadata();

        /// <summary>
        /// Minimum firmware version daqifi-core is built and tested against (ADR 0001,
        /// docs/adr/0001-firmware-feature-gating.md). Every SCPI command daqifi-core issues today
        /// exists on all firmware at or above this floor, so a device below it gets best-effort
        /// behavior: an individual command may still work, but any that don't are surfaced as a
        /// typed <see cref="FeatureNotSupportedException"/> via the wire-level <c>-113</c>
        /// "Undefined header" backstop, rather than a guarantee up front.
        /// </summary>
        public static readonly FirmwareVersion MinSupportedFirmware = new(3, 5, 0, null, 0);

        /// <summary>
        /// Gets a value indicating whether the connected device's reported firmware version meets
        /// <see cref="MinSupportedFirmware"/>. Evaluated live against <see cref="Metadata"/> on every
        /// access — not cached — so it always reflects the most recently reported version rather than
        /// a stale snapshot. Returns <c>false</c> if the firmware version has not yet been reported or
        /// does not parse; callers should treat that as "unknown", not "confirmed unsupported".
        /// </summary>
        public bool IsFirmwareVersionSupported =>
            FirmwareVersion.TryParse(Metadata.FirmwareVersion, out var parsed) && parsed >= MinSupportedFirmware;

        /// <summary>
        /// Gets the collection of channels populated from device status messages.
        /// </summary>
        /// <remarks>
        /// This collection is populated when <see cref="PopulateChannelsFromStatus"/> is called
        /// with a valid protobuf status message from the device.
        /// </remarks>
        public IReadOnlyList<IChannel> Channels => _channels.AsReadOnly();

        /// <summary>
        /// Returns a point-in-time snapshot of the channel collection, taken under the
        /// channels lock so it is safe to enumerate even when a status message repopulates
        /// the collection concurrently on the consumer thread.
        /// </summary>
        /// <remarks>
        /// The public <see cref="Channels"/> property exposes a live view over the backing list;
        /// callers that fold over channels off the consumer thread (e.g. the device-level
        /// channel-management API, or an out-of-process control surface) should use this snapshot
        /// instead to avoid a concurrent-mutation <see cref="InvalidOperationException"/> or a torn read.
        /// </remarks>
        /// <returns>A lock-protected copy of the current channel collection.</returns>
        public IReadOnlyList<IChannel> GetChannelsSnapshot()
        {
            lock (_channelsLock)
            {
                return _channels.ToArray();
            }
        }

        /// <inheritdoc cref="GetChannelsSnapshot"/>
        protected IReadOnlyList<IChannel> SnapshotChannels() => GetChannelsSnapshot();

        /// <summary>
        /// Gets the device's timestamp clock frequency in Hz.
        /// Populated from the <c>TimestampFreq</c> field of the status message.
        /// Used as the fallback frequency for SD card log file parsing when no
        /// per-message timestamp frequency is available.
        /// </summary>
        public uint TimestampFrequency { get; private set; }

        /// <summary>
        /// Gets or sets the current operational state of the device.
        /// </summary>
        public DeviceState State { get; private set; } = DeviceState.Disconnected;

        private ConnectionStatus _status;
        private IMessageProducer<string>? _messageProducer;
        private IMessageConsumer<DaqifiOutMessage>? _messageConsumer;
        private readonly IStreamTransport? _transport;
        // Set only by the Stream-based constructor, so Send<T> can write non-string
        // payloads directly when there's no IStreamTransport to fall back to.
        private readonly Stream? _directStream;

        /// <summary>
        /// Gets the transport used for device communication, if available.
        /// </summary>
        protected IStreamTransport? Transport => _transport;

        private IProtocolHandler? _protocolHandler;
        private bool _disposed;
        private bool _isDisconnecting;
        private bool _isInitialized;
        private readonly List<IChannel> _channels = new();

        // Guards structural access to _channels: the consumer thread repopulates it
        // (Clear/Add in PopulateChannelsFromStatus) while caller threads fold over a
        // snapshot via SnapshotChannels for the device-level channel-management API.
        private readonly object _channelsLock = new();

        /// <summary>
        /// Default time <see cref="InitializeAsync"/> waits for the device to report its
        /// channel configuration (via the <see cref="ChannelsPopulated"/> event) before
        /// failing with a <see cref="TimeoutException"/>.
        /// </summary>
        private static readonly TimeSpan DefaultChannelPopulationTimeout = TimeSpan.FromSeconds(8);

        /// <summary>
        /// Interval at which <see cref="InitializeAsync"/> re-sends <c>GetDeviceInfo</c> while
        /// waiting for the first status message. Serial/CDC devices can miss the initial
        /// request while the port is still settling, so the request is repeated until
        /// channels populate or the timeout elapses.
        /// </summary>
        private static readonly TimeSpan ChannelPopulationPollInterval = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Maximum number of retry attempts for the <see cref="InitializeAsync"/> SCPI setup
        /// sequence when the device returns a transient SCPI error (e.g. -200 "Execution error").
        /// A common trigger is the firmware rejecting a command tied to a persisted prior-session
        /// state (e.g. stream interface) within the tight response window right after connect.
        /// </summary>
        private const int InitScpiErrorMaxRetries = 1;

        /// <summary>
        /// Delay in milliseconds before retrying the <see cref="InitializeAsync"/> SCPI setup
        /// sequence after a transient SCPI error.
        /// </summary>
        private const int InitScpiErrorRetryDelayMs = 150;

        // Serializes ExecuteTextCommandAsync calls device-wide (closes #186).
        // Multiple callers — e.g. concurrent GetSdCardFilesAsync /
        // DrainErrorQueueAsync / GetSystemInfoAsync — would otherwise race the
        // protobuf-consumer pause/swap/restart sequence on the same stream and
        // either intermix SCPI bytes on the wire or interleave reply lines
        // between callers' returned lists. SemaphoreSlim chosen over Lock
        // because the method is async; counter is (1, 1) for mutual exclusion.
        private readonly SemaphoreSlim _textExchangeLock = new(1, 1);

        // Async-context flag that tracks whether the current logical flow
        // already holds _textExchangeLock. AsyncLocal flows across await
        // resumptions on different threads, so a setupAction that re-enters
        // ExecuteTextCommandAsync after a ConfigureAwait(false) hop is still
        // detected and surfaced as InvalidOperationException — instead of
        // wedging on _textExchangeLock.WaitAsync() (the re-entrant call
        // would corrupt the consumer swap mid-flight). Plain
        // Environment.CurrentManagedThreadId capture wouldn't work — the
        // value seen before await may not match the value seen after.
        private readonly AsyncLocal<bool> _isInsideTextExchange = new();
        
        /// <summary>
        /// Gets the current connection status of the device.
        /// </summary>
        public ConnectionStatus Status
        {
            get => _status;
            private set
            {
                if (_status == value) return;
                _status = value;
                StatusChanged?.Invoke(this, new DeviceStatusEventArgs(_status));
            }
        }

        /// <summary>
        /// Occurs when the device status changes.
        /// </summary>
        public event EventHandler<DeviceStatusEventArgs>? StatusChanged;
        
        /// <summary>
        /// Occurs when a message is received from the device.
        /// </summary>
        public event EventHandler<MessageReceivedEventArgs>? MessageReceived;

        /// <summary>
        /// Occurs when an inbound protobuf message is classified as a status message by the
        /// internal <see cref="ProtobufProtocolHandler"/>. Raised in addition to the
        /// undifferentiated <see cref="MessageReceived"/> event, so consumers that only need
        /// the status/stream classification don't have to re-run <c>CanHandle</c> /
        /// <c>DetectMessageType</c> over the same frame themselves.
        /// </summary>
        public event Action<DaqifiOutMessage>? StatusMessageReceived;

        /// <summary>
        /// Occurs when an inbound protobuf message is classified as a streaming data message by
        /// the internal <see cref="ProtobufProtocolHandler"/>. Raised in addition to the
        /// undifferentiated <see cref="MessageReceived"/> event, so consumers that only need
        /// the status/stream classification don't have to re-run <c>CanHandle</c> /
        /// <c>DetectMessageType</c> over the same frame themselves.
        /// </summary>
        public event Action<DaqifiOutMessage>? StreamMessageReceived;

        /// <summary>
        /// Occurs when channels have been populated from a device status message.
        /// </summary>
        public event EventHandler<ChannelsPopulatedEventArgs>? ChannelsPopulated;

        /// <summary>
        /// Initializes a new instance of the <see cref="DaqifiDevice"/> class.
        /// </summary>
        /// <param name="name">The name of the device.</param>
        /// <param name="ipAddress">The IP address of the device, if known.</param>
        public DaqifiDevice(string name, IPAddress? ipAddress = null)
        {
            Name = name;
            IpAddress = ipAddress;
            _status = ConnectionStatus.Disconnected;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DaqifiDevice"/> class with a message producer.
        /// </summary>
        /// <param name="name">The name of the device.</param>
        /// <param name="stream">The stream for device communication.</param>
        /// <param name="ipAddress">The IP address of the device, if known.</param>
        public DaqifiDevice(string name, Stream stream, IPAddress? ipAddress = null)
        {
            Name = name;
            IpAddress = ipAddress;
            _status = ConnectionStatus.Disconnected;
            _messageProducer = new MessageProducer<string>(stream);
            _directStream = stream;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DaqifiDevice"/> class with a transport.
        /// </summary>
        /// <param name="name">The name of the device.</param>
        /// <param name="transport">The transport for device communication.</param>
        public DaqifiDevice(string name, IStreamTransport transport)
        {
            Name = name;
            _status = ConnectionStatus.Disconnected;
            _transport = transport;
            
            // Subscribe to transport status changes
            _transport.StatusChanged += OnTransportStatusChanged;
        }

        /// <summary>
        /// Connects to the device.
        /// </summary>
        public void Connect()
        {
            Status = ConnectionStatus.Connecting;
            State = DeviceState.Connecting;

            try
            {
                // Connect transport if available
                _transport?.Connect();

                // Create message producer and consumer from transport if needed
                if (_transport != null)
                {
                    if (_messageProducer == null)
                    {
                        _messageProducer = new MessageProducer<string>(_transport.Stream);
                    }

                    if (_messageConsumer == null)
                    {
                        _messageConsumer = new StreamMessageConsumer<DaqifiOutMessage>(
                            _transport.Stream,
                            new ProtobufMessageParser());
                    }
                }

                // Start message producer and consumer if available
                _messageProducer?.Start();
                _messageConsumer?.Start();

                Status = ConnectionStatus.Connected;
                State = DeviceState.Connected;
            }
            catch
            {
                Status = ConnectionStatus.Disconnected;
                State = DeviceState.Disconnected;
                throw;
            }
        }

        /// <summary>
        /// Disconnects from the device.
        /// </summary>
        /// <remarks>
        /// Waits up to 10 seconds to acquire <c>_textExchangeLock</c> before
        /// tearing down the consumer / producer / transport. This prevents
        /// a race where an in-flight <see cref="ExecuteTextCommandAsync(Action, int, int, CancellationToken)"/>
        /// is mid-swap (text consumer running on the stream, protobuf
        /// consumer not yet restarted) and Disconnect rips the transport
        /// out from under it. If the wait times out, Disconnect proceeds
        /// anyway — a stuck text exchange must not block teardown forever.
        /// The 10s budget covers the worst-case ExecuteTextCommandAsync
        /// hold time with default timeouts (StopSafely up to 1s + maxWait
        /// of responseTimeoutMs*5 = 5s by default + safety margin) and
        /// most custom-timeout callers; on timeout the in-flight exchange
        /// sees <c>_isDisconnecting == true</c> via the post-acquisition
        /// validation and bails out cleanly. Callers wanting non-blocking
        /// disconnect should drive this off a Task.Run.
        /// </remarks>
        public void Disconnect()
        {
            _isDisconnecting = true;
            // Best-effort coordination with ExecuteTextCommandAsync —
            // acquire the lock so we don't tear the transport out from
            // under an in-flight text exchange. The lock IS released in
            // the finally below when acquired (so a future Connect()
            // followed by ExecuteTextCommandAsync isn't blocked); a
            // stuck exchange that holds past the timeout drops to the
            // _isDisconnecting validation path inside the exchange.
            var lockAcquired = false;
            try
            {
                lockAcquired = _textExchangeLock.Wait(TimeSpan.FromSeconds(10));
            }
            catch (ObjectDisposedException)
            {
                // Disconnect called after Dispose — nothing to coordinate.
            }

            try
            {
                // Unsubscribe from message consumer events
                if (_messageConsumer != null)
                {
                    _messageConsumer.MessageReceived -= OnInboundMessageReceived;
                }

                // Stop message consumer and producer safely if available
                _messageConsumer?.StopSafely();
                _messageProducer?.StopSafely();

                // Null the producer/consumer so a subsequent Connect()
                // rebuilds them against the transport's current Stream.
                // SerialStreamTransport.Stream returns _serialPort.BaseStream,
                // which is a new instance after Disconnect() → Connect()
                // reopens the port; reusing the old producer/consumer would
                // leave them bound to the previous (disposed) BaseStream
                // and any Send() would silently no-op. Surfaced by PR #200's
                // post-reconnect readiness probe (LAN chip-info returning
                // null on every attempt because Send went to a dead stream).
                _messageConsumer = null;
                _messageProducer = null;

                // Disconnect transport if available
                _transport?.Disconnect();
            }
            finally
            {
                Status = ConnectionStatus.Disconnected;
                State = DeviceState.Disconnected;
                _isInitialized = false;
                _isDisconnecting = false;
                if (lockAcquired)
                {
                    try
                    {
                        _textExchangeLock.Release();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            }
        }

        /// <summary>
        /// Sends a message to the device.
        /// </summary>
        /// <typeparam name="T">The type of the message data payload.</typeparam>
        /// <param name="message">The message to send to the device.</param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the device is not connected, or when connected but has no transport or
        /// stream to send on (e.g. the producer-less <see cref="DaqifiDevice(string, IPAddress)"/>
        /// constructor).
        /// </exception>
        public virtual void Send<T>(IOutboundMessage<T> message)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Device is not connected.");
            }

            // Use the queued message producer when available and the message is string-based;
            // this is the common path (SCPI text commands).
            if (_messageProducer != null && message is IOutboundMessage<string> stringMessage)
            {
                _messageProducer.Send(stringMessage);
                return;
            }

            // Non-string payloads (or a string payload with no producer) bypass the queue and
            // write directly to the underlying stream, since IOutboundMessage<T> already knows
            // how to serialize itself regardless of T.
            var stream = _transport?.Stream ?? _directStream;
            if (stream == null)
            {
                throw new InvalidOperationException(
                    "This device has no transport or stream to send on. Use a constructor that accepts a Stream or IStreamTransport.");
            }

            var bytes = message.GetBytes();
            stream.Write(bytes, 0, bytes.Length);
        }

        /// <summary>
        /// Temporarily pauses the protobuf message consumer to allow raw byte access to the
        /// underlying transport stream. The consumer is restored when the returned action completes
        /// or is disposed.
        /// </summary>
        /// <param name="rawAction">
        /// An async function that receives the transport stream and performs raw I/O.
        /// The protobuf consumer will not read from the stream while this action is executing.
        /// </param>
        /// <param name="cancellationToken">A cancellation token to observe.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the device is not connected or has no transport.</exception>
        protected virtual async Task ExecuteRawCaptureAsync(
            Func<Stream, CancellationToken, Task> rawAction,
            CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Device is not connected.");
            }

            if (_transport == null)
            {
                throw new InvalidOperationException("ExecuteRawCaptureAsync requires a transport-based connection.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // Stop the protobuf consumer so it doesn't compete for stream bytes
                if (_messageConsumer != null)
                {
                    _messageConsumer.MessageReceived -= OnInboundMessageReceived;
                    var stopped = _messageConsumer.StopSafely(timeoutMs: 1000);
                    if (!stopped)
                    {
                        _messageConsumer.Stop();
                    }
                }

                // Hand the stream to the caller for raw I/O
                await rawAction(_transport.Stream, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // Restart the protobuf consumer
                if (_messageConsumer != null)
                {
                    _messageConsumer.Start();
                    _messageConsumer.MessageReceived += OnInboundMessageReceived;
                }
            }
        }

        /// <summary>
        /// Executes a text-based command by temporarily switching from the protobuf consumer to a
        /// line-based text consumer, collecting text responses, then restoring the protobuf consumer.
        /// </summary>
        /// <param name="setupAction">An action that sends SCPI commands to the device while the text consumer is active.</param>
        /// <param name="responseTimeoutMs">The time in milliseconds to wait for the first text response after sending commands.</param>
        /// <param name="completionTimeoutMs">The time in milliseconds of inactivity after the first response before considering the response complete. Defaults to 250ms.</param>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>A list of text lines received from the device.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the device is not connected or has no transport.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
        protected virtual Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Action setupAction,
            int responseTimeoutMs = 1000,
            int completionTimeoutMs = 250,
            CancellationToken cancellationToken = default)
        {
            return ExecuteTextCommandCoreAsync(
                _ => { setupAction(); return Task.CompletedTask; },
                responseTimeoutMs,
                completionTimeoutMs,
                cancellationToken);
        }

        /// <summary>
        /// Async overload of <see cref="ExecuteTextCommandAsync(Action, int, int, CancellationToken)"/>
        /// that accepts an async setup action so callers can <c>await</c> cancellable operations
        /// (e.g. <see cref="Task.Delay(int, CancellationToken)"/>) between SCPI commands without
        /// blocking the thread-pool thread.
        /// </summary>
        /// <param name="setupActionAsync">An async function that sends SCPI commands to the device while the text consumer is active. Receives the operation's cancellation token.</param>
        /// <param name="responseTimeoutMs">The time in milliseconds to wait for the first text response after sending commands.</param>
        /// <param name="completionTimeoutMs">The time in milliseconds of inactivity after the first response before considering the response complete. Defaults to 250ms.</param>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>A list of text lines received from the device.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the device is not connected or has no transport.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
        protected virtual Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Func<CancellationToken, Task> setupActionAsync,
            int responseTimeoutMs = 1000,
            int completionTimeoutMs = 250,
            CancellationToken cancellationToken = default)
        {
            return ExecuteTextCommandCoreAsync(
                setupActionAsync,
                responseTimeoutMs,
                completionTimeoutMs,
                cancellationToken);
        }

        private async Task<IReadOnlyList<string>> ExecuteTextCommandCoreAsync(
            Func<CancellationToken, Task> setupActionAsync,
            int responseTimeoutMs,
            int completionTimeoutMs,
            CancellationToken cancellationToken)
        {
            if (responseTimeoutMs <= 0)
                throw new ArgumentOutOfRangeException(nameof(responseTimeoutMs), responseTimeoutMs, "Timeout must be positive.");
            if (completionTimeoutMs <= 0)
                throw new ArgumentOutOfRangeException(nameof(completionTimeoutMs), completionTimeoutMs, "Timeout must be positive.");

            cancellationToken.ThrowIfCancellationRequested();

            // Async-context re-entrancy detection: a setupAction that calls
            // ExecuteTextCommandAsync on the same device would corrupt the
            // consumer swap mid-flight. Surface as a clean exception rather
            // than wedging on _textExchangeLock.WaitAsync() forever.
            // AsyncLocal flows across await thread hops so this catches
            // re-entry even when the inner call resumes on a different
            // thread than the outer call.
            if (_isInsideTextExchange.Value)
            {
                throw new InvalidOperationException(
                    "ExecuteTextCommandAsync is not re-entrant on the same device; "
                    + "do not call it from inside a setupAction callback.");
            }

            try
            {
                await _textExchangeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Dispose() raced ahead of us and disposed the semaphore.
                // Surface the same clean failure as the post-acquisition
                // _disposed check below, instead of leaking a low-level
                // teardown exception to callers.
                throw new InvalidOperationException(
                    "ExecuteTextCommandAsync cannot run because the device is disposed.");
            }

            _isInsideTextExchange.Value = true;
            try
            {
                // All validation runs INSIDE the lock so a competing thread
                // calling DisconnectAsync() / Dispose() while we're blocked
                // on WaitAsync() doesn't leave us with a stale _transport /
                // _messageConsumer reference (closes the TOCTOU window
                // documented in #186).
                if (_disposed || _isDisconnecting)
                {
                    throw new InvalidOperationException(
                        "ExecuteTextCommandAsync cannot run while the device is "
                        + "disposing or disconnecting.");
                }

                if (!IsConnected)
                {
                    throw new InvalidOperationException("Device is not connected.");
                }

                if (_transport == null)
                {
                    throw new InvalidOperationException("ExecuteTextCommandAsync requires a transport-based connection.");
                }

                // The device-level IsConnected check above is status-based and can still report
                // Connected when the underlying transport has dropped (e.g. a serial port closed
                // by an unplug or a DTR-triggered MCU reset mid-connect). Detect that here and
                // fail with the typed transport-disconnected exception, rather than dereferencing
                // Stream below and surfacing the framework's raw "BaseStream is only available
                // when the port is open." message (issue #238).
                if (!_transport.IsConnected)
                {
                    throw new TransportNotConnectedException(
                        "Device transport is no longer connected.");
                }

                var sw = Stopwatch.StartNew();
                var collectedLines = new List<string>();
                var stream = _transport.Stream;
                int? originalReadTimeout = null;

                try
                {
                    if (stream.CanTimeout)
                    {
                        try
                        {
                            originalReadTimeout = stream.ReadTimeout;
                            stream.ReadTimeout = Math.Min(500, Math.Max(100, responseTimeoutMs / 4));
                        }
                        catch
                        {
                            // Some streams may not allow setting read timeout; ignore.
                            originalReadTimeout = null;
                        }
                    }

                    // Stop the protobuf consumer so it doesn't compete for stream bytes.
                    // The serial transport sets ReadTimeout=500ms after connect, so the
                    // consumer thread's blocking Read will unblock within 500ms.
                    if (_messageConsumer != null)
                    {
                        _messageConsumer.MessageReceived -= OnInboundMessageReceived;
                        var stopped = _messageConsumer.StopSafely(timeoutMs: 1000);
                        if (!stopped)
                        {
                            _messageConsumer.Stop();
                        }
                    }

                    Trace.WriteLine($"[ExecuteTextCommandAsync] Protobuf consumer stopped at {sw.ElapsedMilliseconds}ms");

                    // Create a temporary text consumer on the same stream
                    using var textConsumer = new StreamMessageConsumer<string>(
                        _transport.Stream,
                        new LineBasedMessageParser());

                    textConsumer.MessageReceived += (_, e) =>
                    {
                        collectedLines.Add(e.Message.Data);
                    };

                    textConsumer.Start();
                    // ConfigureAwait(false): the lock is held, so resuming on a captured
                    // sync context (e.g. UI thread) would deadlock if that thread calls Disconnect().
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);

                    Trace.WriteLine($"[ExecuteTextCommandAsync] Text consumer started at {sw.ElapsedMilliseconds}ms");

                    // Execute the setup action (sends SCPI commands). ConfigureAwait(false)
                    // matches the surrounding lock-protected awaits.
                    await setupActionAsync(cancellationToken).ConfigureAwait(false);

                    Trace.WriteLine($"[ExecuteTextCommandAsync] Setup action completed at {sw.ElapsedMilliseconds}ms");

                    // Wait for responses using a two-phase inactivity-based timeout:
                    // Phase 1: Wait up to responseTimeoutMs for the first response.
                    // Phase 2: After receiving data, wait completionTimeoutMs of inactivity to finish.
                    var lastMessageTime = DateTime.UtcNow;
                    var maxWait = TimeSpan.FromMilliseconds(responseTimeoutMs * 5);
                    var startTime = DateTime.UtcNow;
                    var hasReceivedAny = false;

                    while (DateTime.UtcNow - startTime < maxWait)
                    {
                        var previousCount = collectedLines.Count;
                        await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                        if (collectedLines.Count > previousCount)
                        {
                            lastMessageTime = DateTime.UtcNow;
                            if (!hasReceivedAny)
                            {
                                hasReceivedAny = true;
                                Trace.WriteLine($"[ExecuteTextCommandAsync] First response at {sw.ElapsedMilliseconds}ms");
                            }
                        }

                        var elapsed = DateTime.UtcNow - lastMessageTime;

                        if (hasReceivedAny)
                        {
                            // Phase 2: short completion timeout after first data
                            if (elapsed >= TimeSpan.FromMilliseconds(completionTimeoutMs))
                            {
                                break;
                            }
                        }
                        else
                        {
                            // Phase 1: full initial timeout waiting for first data
                            if (elapsed >= TimeSpan.FromMilliseconds(responseTimeoutMs))
                            {
                                break;
                            }
                        }
                    }

                    Trace.WriteLine($"[ExecuteTextCommandAsync] Collection complete at {sw.ElapsedMilliseconds}ms, {collectedLines.Count} lines");

                    // Stop the text consumer
                    textConsumer.StopSafely();
                }
                finally
                {
                    if (originalReadTimeout.HasValue && stream.CanTimeout)
                    {
                        try
                        {
                            stream.ReadTimeout = originalReadTimeout.Value;
                        }
                        catch
                        {
                            // Ignore failures when restoring timeout.
                        }
                    }

                    // Restart the protobuf consumer
                    if (_messageConsumer != null)
                    {
                        _messageConsumer.Start();
                        _messageConsumer.MessageReceived += OnInboundMessageReceived;
                    }

                    Trace.WriteLine($"[ExecuteTextCommandAsync] Total elapsed: {sw.ElapsedMilliseconds}ms");
                }

                return collectedLines;
            }
            finally
            {
                _isInsideTextExchange.Value = false;
                // Release can race with Dispose() — Dispose acquires the lock
                // before disposing it, but if that acquisition timed out and
                // Dispose proceeded anyway, our SemaphoreSlim handle is now
                // gone. Treat that as a benign teardown signal rather than
                // surfacing it from the finally and masking the original
                // exception (if any) from the try body.
                try
                {
                    _textExchangeLock.Release();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        /// <summary>
        /// Pops <c>SYSTem:ERRor?</c> entries from the device until the queue reports
        /// <c>"No error"</c> and returns the popped entries to the caller.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the queue-inspection counterpart to the inline last-command
        /// error check used elsewhere in the codebase (e.g. <c>ContainsScpiError</c>
        /// in <see cref="DaqifiStreamingDevice"/>): that helper tells you whether
        /// the captured response from a single command contained an error,
        /// while this method tells you what is currently queued on the
        /// device — including stale errors from prior commands or sessions.
        /// </para>
        /// <para>
        /// Ownership of the popped entries is transferred to the caller so
        /// they can log them, surface them in a health-check report, throw
        /// on hardware faults, or discard them if known-stale.
        /// </para>
        /// <para>
        /// Each iteration uses <see cref="ExecuteTextCommandAsync(Action, int, int, CancellationToken)"/>, which
        /// pauses the protobuf consumer for the duration of the text exchange.
        /// Avoid calling this during active streaming or concurrently with
        /// other text commands.
        /// </para>
        /// </remarks>
        /// <param name="maxIterations">
        /// Safety cap on the number of <c>SYSTem:ERRor?</c> queries. Defaults to 256
        /// — large enough to drain a deeply queued device, small enough that a
        /// runaway loop is bounded. If the cap is reached without seeing
        /// <c>"No error"</c>, a warning is traced and the popped entries
        /// collected so far are returned; callers that want to treat this as a
        /// failure can compare <c>Count</c> to <paramref name="maxIterations"/>.
        /// </param>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>The list of error strings popped from the queue (empty if the queue was already clean).</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxIterations"/> is not positive.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the device is not connected or has no transport.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
        public virtual async Task<IReadOnlyList<string>> DrainErrorQueueAsync(
            int maxIterations = 256,
            CancellationToken cancellationToken = default)
        {
            if (maxIterations <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxIterations), maxIterations, "Must be positive.");

            var popped = new List<string>();
            for (int i = 0; i < maxIterations; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var lines = await ExecuteTextCommandAsync(
                    () => Send(ScpiMessageProducer.GetSystemError),
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                var reply = lines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();
                if (reply == null)
                {
                    // Empty reply means timeout or unresponsive device, not a
                    // queued error — terminate rather than spin to maxIterations.
                    Trace.WriteLine($"[DrainErrorQueueAsync] Empty reply on iteration {i}; terminating after {popped.Count} popped entries.");
                    return popped;
                }

                // SCPI error replies are formatted as <code>,"<message>". Code 0
                // (or +0) indicates an empty queue; anything else is a real
                // error to capture. Parse the numeric prefix rather than
                // substring-matching "No error" so a hypothetical error message
                // containing that phrase can't be mistaken for the terminator.
                var commaIndex = reply.IndexOf(',');
                var codeSpan = commaIndex >= 0 ? reply.AsSpan(0, commaIndex).Trim() : reply.AsSpan().Trim();
                if (int.TryParse(codeSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) && code == 0)
                {
                    return popped;
                }

                popped.Add(reply);
            }

            Trace.WriteLine($"[DrainErrorQueueAsync] Did not converge after {maxIterations} iterations; queue may still contain entries.");
            return popped;
        }

        /// <summary>
        /// Raises the <see cref="MessageReceived"/> event when a message is received from the device.
        /// </summary>
        /// <param name="message">The message received from the device.</param>
        protected virtual void OnMessageReceived(IInboundMessage<object> message)
        {
            MessageReceived?.Invoke(this, new MessageReceivedEventArgs(message));
        }

        /// <summary>
        /// Handles transport status changes and updates device connection status accordingly.
        /// </summary>
        /// <param name="sender">The transport that raised the event.</param>
        /// <param name="e">The transport status event arguments.</param>
        private void OnTransportStatusChanged(object? sender, TransportStatusEventArgs e)
        {
            if (e.IsConnected)
            {
                // Transport connected, but device status is managed by Connect() method
            }
            else
            {
                // Transport disconnected — only report Lost for unexpected drops,
                // not during an intentional Disconnect() call
                if (Status == ConnectionStatus.Connected && !_isDisconnecting)
                {
                    Status = ConnectionStatus.Lost;
                }
            }
        }

        /// <summary>
        /// Disposes the device and releases resources.
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                Disconnect();
                _messageConsumer?.Dispose();
                _messageProducer?.Dispose();
                _transport?.Dispose();
                _textExchangeLock.Dispose();
                _disposed = true;
            }
        }

        /// <summary>
        /// Initializes the device by running the standard initialization sequence.
        /// </summary>
        /// <param name="channelPopulationTimeout">
        /// Maximum time to wait for the device to report its channel configuration (via the
        /// <see cref="ChannelsPopulated"/> event) before failing. If <c>null</c>, a default of
        /// 8 seconds is used.
        /// </param>
        /// <param name="cancellationToken">A cancellation token to observe while initializing.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        /// <remarks>
        /// The initialization sequence includes:
        /// 1. Disable device echo
        /// 2. Stop any running streaming
        /// 3. Turn device on (if needed)
        /// 4. Set protobuf message format
        /// 5. Query device info and block until the device reports its channel configuration
        ///
        /// Rather than returning after a fixed delay, the method awaits the first
        /// <see cref="ChannelsPopulated"/> event so callers receive a fully populated device.
        /// Serial/CDC devices can take noticeably longer than the previous fixed wait to send
        /// their first status message, so <c>GetDeviceInfo</c> is re-sent periodically until
        /// channels populate or <paramref name="channelPopulationTimeout"/> elapses (which
        /// surfaces a <see cref="TimeoutException"/>).
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="channelPopulationTimeout"/> is not positive.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the device is not connected.</exception>
        /// <exception cref="ScpiInitializationErrorException">Thrown when the device returns a SCPI error during initialization that persists after an internal retry.</exception>
        /// <exception cref="TimeoutException">Thrown when the device does not report its channel configuration within <paramref name="channelPopulationTimeout"/>.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
        public virtual async Task InitializeAsync(
            TimeSpan? channelPopulationTimeout = null,
            CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Device must be connected before initialization.");
            }

            if (_isInitialized)
            {
                return; // Already initialized
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Validate the effective timeout up front (outside the try) so an invalid
            // configuration surfaces as ArgumentOutOfRangeException rather than a misleading
            // TimeoutException that blames the device, and without flipping device state.
            var effectiveChannelPopulationTimeout = channelPopulationTimeout ?? DefaultChannelPopulationTimeout;
            if (effectiveChannelPopulationTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(channelPopulationTimeout),
                    effectiveChannelPopulationTimeout,
                    "Channel population timeout must be positive.");
            }

            State = DeviceState.Initializing;

            try
            {
                // Set up protocol handler for status messages
                _protocolHandler = new ProtobufProtocolHandler(
                    statusMessageHandler: OnStatusMessageReceived,
                    streamMessageHandler: OnStreamMessageReceived
                );

                // Wire up message consumer to route messages through protocol handler.
                // Remove first so a retried initialization (e.g. after a prior timeout or
                // cancellation that left the device connected) does not double-subscribe and
                // process every inbound message twice; '-=' is a no-op when not subscribed.
                if (_messageConsumer != null)
                {
                    _messageConsumer.MessageReceived -= OnInboundMessageReceived;
                    _messageConsumer.MessageReceived += OnInboundMessageReceived;
                }

                // Send the text-mode SCPI setup commands via ExecuteTextCommandAsync so that
                // any -200 execution error response is captured rather than silently discarded
                // by the protobuf consumer.  The protobuf consumer is stopped for the duration
                // of this call and restarted afterward, leaving the device in protobuf mode
                // and ready to receive the SYSInfoPB? response.
                //
                // A SCPI error here is often transient — e.g. the firmware rejecting a command
                // tied to a persisted prior-session state within the tight response window right
                // after connect — so retry the whole sequence with a settle delay before treating
                // it as a hard failure (mirrors the retry already used for SD card operations).
                IReadOnlyList<string> initLines = Array.Empty<string>();
                string? errorLine = null;
                for (var attempt = 0; attempt <= InitScpiErrorMaxRetries; attempt++)
                {
                    if (attempt > 0)
                    {
                        await Task.Delay(InitScpiErrorRetryDelayMs, cancellationToken).ConfigureAwait(false);
                    }

                    initLines = await ExecuteTextCommandAsync(() =>
                    {
                        Send(ScpiMessageProducer.DisableDeviceEcho);
                        Thread.Sleep(100);

                        Send(ScpiMessageProducer.StopStreaming);
                        Thread.Sleep(100);

                        Send(ScpiMessageProducer.TurnDeviceOn);
                        Thread.Sleep(100);

                        Send(ScpiMessageProducer.SetProtobufStreamFormat);
                    }, responseTimeoutMs: 1000, cancellationToken: cancellationToken).ConfigureAwait(false);

                    // Shared with DaqifiStreamingDevice's SCPI error detection so both sites
                    // recognize the same set of delimiter-separated error formats — a bare
                    // "**ERROR"-prefix check would miss "ERROR: ..." and space/tab-delimited
                    // variants like "ERROR -200,..." or "ERROR\t-200,...".
                    errorLine = initLines.FirstOrDefault(ScpiResponseClassifier.IsScpiErrorLine);
                    if (errorLine == null)
                    {
                        break;
                    }
                }

                // Surface any SCPI error that survived the retry so callers know the device
                // is not in the expected state, via a typed exception so it can be classified
                // without matching on the message.
                if (errorLine != null)
                {
                    var trimmedErrorLine = errorLine.Trim();
                    throw new ScpiInitializationErrorException(
                        $"Device returned a SCPI error during initialization: {trimmedErrorLine}",
                        initLines,
                        trimmedErrorLine);
                }

                // Query device info and block until the device reports its channel
                // configuration. This replaces the previous fixed delay, which returned an
                // unpopulated device on serial/CDC connections whose first status message
                // had not yet arrived.
                await WaitForChannelsPopulatedAsync(
                    effectiveChannelPopulationTimeout,
                    cancellationToken).ConfigureAwait(false);

                // Run any derived-class initialization (e.g. routing the stream to USB) as part of
                // this try/catch so a failure there leaves the device in a consistent terminal state
                // rather than a falsely-ready device. _isInitialized is only set after it succeeds,
                // so a failed init can be safely retried.
                await OnDeviceInitializingAsync(cancellationToken).ConfigureAwait(false);

                _isInitialized = true;
                State = DeviceState.Ready;
            }
            catch (OperationCanceledException)
            {
                // Caller-initiated cancellation is not a device fault. Revert to a
                // non-error state so upstream logic that treats Error as a hardware or
                // connection failure isn't misled into reporting a phantom failure.
                State = IsConnected ? DeviceState.Connected : DeviceState.Disconnected;
                throw;
            }
            catch (Exception)
            {
                State = DeviceState.Error;
                throw;
            }
        }

        /// <summary>
        /// When overridden in a derived class, performs additional device-specific initialization
        /// after the device reports its channel configuration but before it is marked initialized
        /// and ready.
        /// </summary>
        /// <remarks>
        /// This runs inside <see cref="InitializeAsync"/>'s exception handling, so a failure here
        /// leaves the device in a consistent terminal state — cancellation reverts to the connection
        /// state and other faults set <see cref="DeviceState.Error"/> — rather than a falsely-ready
        /// device, and the failed initialization can be retried. The base implementation does nothing.
        /// </remarks>
        /// <param name="cancellationToken">A cancellation token to observe.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        protected virtual Task OnDeviceInitializingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <summary>
        /// Sends <c>GetDeviceInfo</c> and waits for the device to report its channel
        /// configuration via the <see cref="ChannelsPopulated"/> event, re-sending the request
        /// periodically until channels populate or the timeout elapses.
        /// </summary>
        /// <param name="timeout">Maximum time to wait before failing.</param>
        /// <param name="cancellationToken">A cancellation token to observe.</param>
        /// <returns>A task that completes once channels are populated.</returns>
        /// <exception cref="TimeoutException">Thrown when no channels are populated within <paramref name="timeout"/>.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
        private async Task WaitForChannelsPopulatedAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            var populatedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnChannelsPopulated(object? sender, ChannelsPopulatedEventArgs e)
            {
                // Only complete on a status that actually reported channels. A status with zero
                // channels would otherwise satisfy the wait with an empty device — the exact
                // outcome this method exists to prevent.
                if (e.AnalogChannelCount + e.DigitalChannelCount > 0)
                {
                    populatedTcs.TrySetResult(true);
                }
            }

            // Subscribe before sending so a fast response cannot fire the event in the
            // window between Send() and subscription.
            ChannelsPopulated += OnChannelsPopulated;
            try
            {
                var stopwatch = Stopwatch.StartNew();

                // Query device info – expects a protobuf response, so use plain Send()
                // now that the protobuf consumer is running again.
                Send(ScpiMessageProducer.GetDeviceInfo);

                // If the response arrived synchronously (e.g. the consumer thread fired the
                // event before we reached the wait loop), short-circuit. We gate on the
                // completion source rather than _channels.Count so we only react to a status
                // received after this init began — a prior session may have left stale
                // channels behind (Disconnect does not clear them), and a fresh SYSInfoPB?
                // response always repopulates them regardless.
                if (populatedTcs.Task.IsCompleted)
                {
                    return;
                }

                while (true)
                {
                    var remaining = timeout - stopwatch.Elapsed;
                    if (remaining <= TimeSpan.Zero)
                    {
                        break;
                    }

                    var pollDelay = remaining < ChannelPopulationPollInterval
                        ? remaining
                        : ChannelPopulationPollInterval;

                    var completed = await Task.WhenAny(
                        populatedTcs.Task,
                        Task.Delay(pollDelay, cancellationToken)).ConfigureAwait(false);

                    if (completed == populatedTcs.Task)
                    {
                        return;
                    }

                    // The delay elapsed (or was canceled). Honor a result that arrived in the same
                    // window as cancellation rather than discarding it, then surface cancellation
                    // before re-requesting.
                    if (populatedTcs.Task.IsCompleted)
                    {
                        return;
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    // Re-request device info: serial/CDC devices can miss the first request
                    // while the port is still settling.
                    Send(ScpiMessageProducer.GetDeviceInfo);
                }

                // The event may have fired right at the timeout boundary.
                if (populatedTcs.Task.IsCompleted)
                {
                    return;
                }

                throw new TimeoutException(
                    $"Device '{Name}' did not report its channel configuration within {timeout.TotalSeconds:0.#}s. "
                    + "The device may be unresponsive or still initializing.");
            }
            finally
            {
                ChannelsPopulated -= OnChannelsPopulated;
            }
        }

        /// <summary>
        /// Handles status messages received from the device during initialization.
        /// </summary>
        /// <param name="message">The status message from the device.</param>
        protected virtual void OnStatusMessageReceived(DaqifiOutMessage message)
        {
            // Update device metadata
            Metadata.UpdateFromProtobuf(message);

            // Populate channels from the status message
            PopulateChannelsFromStatus(message);

            // Raise the classified event first so consumers that only care about status
            // messages can react before the undifferentiated MessageReceived below. A
            // misbehaving subscriber must not prevent MessageReceived from firing for this
            // frame — the consumer loop that calls in here does not retry a failed frame,
            // so an uncaught exception here would silently drop it for every other consumer.
            RaiseClassifiedEvent(StatusMessageReceived, message, nameof(StatusMessageReceived));

            // Raise event for external consumers
            var inboundMessage = new ProtobufMessage(message);
            OnMessageReceived(inboundMessage);
        }

        /// <summary>
        /// Invokes a classified message event, isolating the caller from a subscriber exception.
        /// </summary>
        /// <remarks>
        /// <see cref="OnStatusMessageReceived"/> and <see cref="OnStreamMessageReceived"/> still have
        /// work to do after raising their classified event (the undifferentiated <see cref="MessageReceived"/>
        /// event, and for <see cref="DaqifiStreamingDevice"/> the per-channel sample decode) — an exception
        /// escaping a classified-event subscriber must not skip that remaining work for the frame.
        /// </remarks>
        /// <param name="handler">The event delegate to invoke, or <c>null</c> if unsubscribed.</param>
        /// <param name="message">The message to pass to subscribers.</param>
        /// <param name="eventName">The event name, for the trace log if a subscriber throws.</param>
        private static void RaiseClassifiedEvent(Action<DaqifiOutMessage>? handler, DaqifiOutMessage message, string eventName)
        {
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(message);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[{eventName}] Subscriber threw: {ex}");
            }
        }

        /// <summary>
        /// Populates the device channels from a protobuf status message.
        /// </summary>
        /// <param name="message">The protobuf status message containing channel configuration.</param>
        /// <remarks>
        /// This method creates channel instances based on the channel counts and calibration
        /// parameters in the status message. Existing channels are cleared before repopulating
        /// to handle device reconnection scenarios.
        ///
        /// For analog channels, calibration parameters (CalM, CalB, InternalScaleM, PortRange)
        /// are extracted from the message. If there's a mismatch between the declared channel
        /// count and the available calibration data, default values are used for missing parameters.
        ///
        /// For digital channels, only the channel count is used to create instances.
        /// </remarks>
        public virtual void PopulateChannelsFromStatus(DaqifiOutMessage message)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            // Update timestamp frequency if present
            if (message.TimestampFreq != 0)
            {
                TimestampFrequency = message.TimestampFreq;
            }

            var analogCount = 0;
            var digitalCount = 0;
            IChannel[] channelsSnapshot;

            // Repopulate under the channels lock so a caller folding over a snapshot on
            // another thread (the device-level channel-management API) never observes a
            // half-cleared or torn list.
            lock (_channelsLock)
            {
                // Index existing channels by identity (type, number). Channels whose identity
                // is unchanged are updated in place rather than replaced below, so consumer-held
                // IChannel references — and the configuration on them (enable/direction/output/
                // PWM state) — survive a routine status re-population untouched.
                var existingByKey = new Dictionary<(ChannelType, int), IChannel>();
                foreach (var existing in _channels)
                {
                    existingByKey[(existing.Type, existing.ChannelNumber)] = existing;
                }

                var updatedChannels = new List<IChannel>();

                // Populate analog input channels
                if (message.AnalogInPortNum > 0)
                {
                    analogCount = PopulateAnalogChannels(message, existingByKey, updatedChannels);
                }

                // Populate digital channels
                if (message.DigitalPortNum > 0)
                {
                    digitalCount = PopulateDigitalChannels(message, existingByKey, updatedChannels);
                }

                _channels.Clear();
                _channels.AddRange(updatedChannels);

                channelsSnapshot = _channels.ToArray();
            }

            // Raise the ChannelsPopulated event with a snapshot to prevent mutations affecting
            // handlers — and outside the lock so a handler that calls back into a channel method
            // (which takes the same lock) cannot deadlock.
            ChannelsPopulated?.Invoke(this, new ChannelsPopulatedEventArgs(
                Array.AsReadOnly(channelsSnapshot),
                analogCount,
                digitalCount));
        }

        /// <summary>
        /// Populates analog channels from the protobuf message, updating existing channel
        /// instances in place where their identity (type, number) is unchanged.
        /// </summary>
        /// <param name="message">The protobuf message containing analog channel data.</param>
        /// <param name="existingByKey">Existing channels from the prior population, keyed by (type, number).</param>
        /// <param name="destination">The list to append the resulting channel instances to, in order.</param>
        /// <returns>The number of analog channels populated.</returns>
        private int PopulateAnalogChannels(DaqifiOutMessage message, Dictionary<(ChannelType, int), IChannel> existingByKey, List<IChannel> destination)
        {
            var analogInPortRanges = message.AnalogInPortRange;
            var analogInCalibrationBValues = message.AnalogInCalB;
            var analogInCalibrationMValues = message.AnalogInCalM;
            var analogInInternalScaleMValues = message.AnalogInIntScaleM;
            var analogInResolution = message.AnalogInRes;

            var count = (int)message.AnalogInPortNum;
            var resolutionIsAssumed = analogInResolution == 0;
            var resolution = analogInResolution > 0 ? analogInResolution : 65535;

            if (resolutionIsAssumed && count > 0)
            {
                Trace.WriteLine($"[PopulateAnalogChannels] Device '{Name}' reported no ADC resolution (analog_in_res=0) for {count} analog channel(s); assuming {resolution}. Scaled samples on this device may be systematically wrong.");
            }

            for (var i = 0; i < count; i++)
            {
                var calibrationB = GetWithDefault(analogInCalibrationBValues, i, 0.0f);
                var calibrationM = GetWithDefault(analogInCalibrationMValues, i, 1.0f);
                var internalScaleM = GetWithDefault(analogInInternalScaleMValues, i, 1.0f);
                var portRange = GetWithDefault(analogInPortRanges, i, 1.0f);

                // A corrupted device response can carry NaN/Infinity or physically nonsensical
                // scaling coefficients. Feeding those into AnalogChannel would either throw from its
                // validating setters (killing channel population mid-stream) or silently propagate
                // garbage into every scaled sample. Fall back to safe defaults and log instead —
                // mirroring the analog_in_res=0 handling above.
                calibrationB = (float)SanitizeScalingValue(calibrationB, 0.0, AnalogChannel.MaxCalibrationMagnitude, requireNonZero: false, i, nameof(calibrationB));
                calibrationM = (float)SanitizeScalingValue(calibrationM, 1.0, AnalogChannel.MaxCalibrationMagnitude, requireNonZero: true, i, nameof(calibrationM));
                internalScaleM = (float)SanitizeScalingValue(internalScaleM, 1.0, AnalogChannel.MaxCalibrationMagnitude, requireNonZero: true, i, nameof(internalScaleM));
                portRange = (float)SanitizePortRange(portRange, i);

                if (existingByKey.TryGetValue((ChannelType.Analog, i), out var existing) && existing is AnalogChannel existingAnalog)
                {
                    existingAnalog.UpdateScalingFromStatus(resolution, calibrationB, calibrationM, internalScaleM, portRange, resolutionIsAssumed);
                    destination.Add(existingAnalog);
                    continue;
                }

                var channel = new AnalogChannel(i, resolution, resolutionIsAssumed)
                {
                    Name = $"AI{i}",
                    Direction = ChannelDirection.Input,
                    IsEnabled = false,
                    CalibrationB = calibrationB,
                    CalibrationM = calibrationM,
                    InternalScaleM = internalScaleM,
                    PortRange = portRange
                };

                destination.Add(channel);
            }

            return count;
        }

        /// <summary>
        /// Clamps a device-reported calibration/scale coefficient to a value <see cref="AnalogChannel"/>
        /// will accept, substituting <paramref name="fallback"/> and logging when the reported value is
        /// non-finite, out of magnitude range, or (when <paramref name="requireNonZero"/>) zero.
        /// </summary>
        private double SanitizeScalingValue(double value, double fallback, double maxMagnitude, bool requireNonZero, int channelIndex, string fieldName)
        {
            var invalid = !double.IsFinite(value)
                || Math.Abs(value) > maxMagnitude
                || (requireNonZero && value == 0.0);

            if (invalid)
            {
                Trace.WriteLine($"[PopulateAnalogChannels] Device '{Name}' reported invalid {fieldName}={value} for analog channel {channelIndex}; substituting {fallback}. Scaled samples on this channel may be affected.");
                return fallback;
            }

            return value;
        }

        /// <summary>
        /// Clamps a device-reported port range to a value <see cref="AnalogChannel"/> will accept,
        /// substituting the 1.0 default and logging when the reported value is non-finite, non-positive,
        /// or beyond <see cref="AnalogChannel.MaxPortRangeVolts"/>.
        /// </summary>
        private double SanitizePortRange(double value, int channelIndex)
        {
            if (!double.IsFinite(value) || value <= 0.0 || value > AnalogChannel.MaxPortRangeVolts)
            {
                Trace.WriteLine($"[PopulateAnalogChannels] Device '{Name}' reported invalid portRange={value} for analog channel {channelIndex}; substituting 1.0. Scaled samples on this channel may be affected.");
                return 1.0;
            }

            return value;
        }

        /// <summary>
        /// Bitmask of digital channels whose hardware supports PWM output (bit n = channel n).
        /// Channels 0, 3, 4, 5, 6 and 7 route to output-compare modules; the mask comes from the
        /// firmware's board configuration and is identical across Nyquist variants.
        /// </summary>
        private const int PwmCapableChannelMask = 0x00F9;

        /// <summary>
        /// Populates digital channels from the protobuf message, updating existing channel
        /// instances in place where their identity (type, number) is unchanged.
        /// </summary>
        /// <param name="message">The protobuf message containing digital channel data.</param>
        /// <param name="existingByKey">Existing channels from the prior population, keyed by (type, number).</param>
        /// <param name="destination">The list to append the resulting channel instances to, in order.</param>
        /// <returns>The number of digital channels populated.</returns>
        private int PopulateDigitalChannels(DaqifiOutMessage message, Dictionary<(ChannelType, int), IChannel> existingByKey, List<IChannel> destination)
        {
            var count = (int)message.DigitalPortNum;

            for (var i = 0; i < count; i++)
            {
                var isPwmCapable = i < 32 && (PwmCapableChannelMask & (1 << i)) != 0;

                if (existingByKey.TryGetValue((ChannelType.Digital, i), out var existing) && existing is DigitalChannel existingDigital)
                {
                    existingDigital.IsPwmCapable = isPwmCapable;
                    destination.Add(existingDigital);
                    continue;
                }

                var channel = new DigitalChannel(i, isPwmCapable)
                {
                    Name = $"DIO{i}",
                    Direction = ChannelDirection.Input,
                    IsEnabled = false
                };

                destination.Add(channel);
            }

            return count;
        }

        /// <summary>
        /// Gets a value from a list with a default fallback if the index is out of range.
        /// </summary>
        /// <param name="list">The list to get the value from.</param>
        /// <param name="index">The index to retrieve.</param>
        /// <param name="defaultValue">The default value if the index is out of range.</param>
        /// <returns>The value at the index or the default value.</returns>
        private static T GetWithDefault<T>(IList<T> list, int index, T defaultValue)
        {
            if (list.Count > index)
            {
                return list[index];
            }
            return defaultValue;
        }

        /// <summary>
        /// Handles streaming data messages received from the device.
        /// </summary>
        /// <param name="message">The streaming message from the device.</param>
        protected virtual void OnStreamMessageReceived(DaqifiOutMessage message)
        {
            // Raise the classified event first so consumers that only care about streaming
            // frames can react before the undifferentiated MessageReceived below. See
            // RaiseClassifiedEvent for why a subscriber exception must not skip that (or, for
            // DaqifiStreamingDevice, the per-channel decode that runs after this base call).
            RaiseClassifiedEvent(StreamMessageReceived, message, nameof(StreamMessageReceived));

            // Raise event for external consumers
            var inboundMessage = new ProtobufMessage(message);
            OnMessageReceived(inboundMessage);
        }

        /// <summary>
        /// Handles inbound messages from the message consumer and routes them through the protocol handler.
        /// </summary>
        /// <param name="sender">The message consumer that raised the event.</param>
        /// <param name="e">The message received event arguments.</param>
        private void OnInboundMessageReceived(object? sender, MessageReceivedEventArgs<DaqifiOutMessage> e)
        {
            // Convert to generic inbound message and route through protocol handler
            var genericMessage = new GenericInboundMessage<object>(e.Message.Data);

            // Route through protocol handler if available
            if (_protocolHandler != null && _protocolHandler.CanHandle(genericMessage))
            {
                // Fire and forget - we don't need to wait for the handler to complete
                _ = _protocolHandler.HandleAsync(genericMessage);
            }
        }
    }
} 
