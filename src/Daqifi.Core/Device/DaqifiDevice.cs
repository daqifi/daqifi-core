using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device.Capabilities;
using Daqifi.Core.Device.Protocol;
using Daqifi.Core.Firmware;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
        /// Gets a value indicating whether this device supports <paramref name="feature"/>, per the
        /// requirement table behind ADR 0001 (docs/adr/0001-firmware-feature-gating.md). Consumers
        /// branch on this instead of comparing firmware version strings themselves.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Evaluated against the current <see cref="Metadata"/> on every call — never cached —
        /// because the board variant and the firmware version arrive in separate status-message
        /// branches, so any precomputed flag would be snapshotted before one of them and go stale.
        /// </para>
        /// <para>
        /// The two axes fail differently, deliberately:
        /// </para>
        /// <list type="bullet">
        /// <item><description>
        /// <b>Firmware version fails closed.</b> A version that is absent or does not parse yields
        /// <c>false</c> for any version-gated feature. Dispatching a command the firmware may not
        /// implement is the more expensive mistake (over WiFi, an SD command on pre-v3.7.0 firmware
        /// stalls on the shared SPI bus), so an unknown version is not treated as permission.
        /// </description></item>
        /// <item><description>
        /// <b>Board and hardware requirements are evaluated only once the board is known.</b> While
        /// <see cref="DeviceMetadata.DeviceType"/> is <see cref="DeviceType.Unknown"/>,
        /// <see cref="DeviceCapabilities.FromDeviceType"/> has not run and
        /// <see cref="DeviceMetadata.Capabilities"/> holds all-<c>false</c> defaults that mean "not
        /// yet known", not "hardware absent" — so those requirements are skipped rather than read
        /// as violated, which would otherwise refuse features on a device that simply has not
        /// reported its part number yet. The firmware's wire-level <c>-113</c> reply remains the
        /// authoritative backstop for that window.
        /// </description></item>
        /// </list>
        /// </remarks>
        /// <param name="feature">The feature to test.</param>
        /// <returns><c>true</c> if the device meets every requirement for <paramref name="feature"/>.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="feature"/> has no entry in the requirement table.
        /// </exception>
        public bool Supports(DeviceFeature feature) =>
            EvaluateSupport(feature) == SupportFailure.None;

        /// <summary>
        /// Which requirement axis a device failed for a feature. Determines what the typed
        /// exception is allowed to claim: only a <see cref="FirmwareVersion"/> failure may report a
        /// required firmware version, since attributing a board or hardware shortfall to the
        /// firmware would tell the caller to perform an upgrade that cannot fix it.
        /// </summary>
        private enum SupportFailure
        {
            /// <summary>Every requirement is met.</summary>
            None,

            /// <summary>The reported firmware is absent, unparseable, or older than the minimum.</summary>
            FirmwareVersion,

            /// <summary>The board variant is not on the feature's allow-list.</summary>
            Board,

            /// <summary>The device lacks hardware the feature drives.</summary>
            Hardware
        }

        /// <summary>
        /// Evaluates every requirement for <paramref name="feature"/> against the current
        /// <see cref="Metadata"/> and reports the first axis that failed. The single evaluation
        /// path behind both <see cref="Supports"/> and <see cref="EnsureSupported"/>, so the two
        /// can never disagree about whether — or why — a feature is unavailable.
        /// </summary>
        private SupportFailure EvaluateSupport(DeviceFeature feature)
        {
            var requirement = DeviceFeatureRequirements.For(feature);

            if (requirement.MinVersion.HasValue
                && (!FirmwareVersion.TryParse(Metadata.FirmwareVersion, out var firmware)
                    || firmware < requirement.MinVersion.Value))
            {
                return SupportFailure.FirmwareVersion;
            }

            var board = Metadata.DeviceType;
            if (board == DeviceType.Unknown)
            {
                return SupportFailure.None;
            }

            if (requirement.Boards.HasValue && !requirement.Boards.Value.Contains(board))
            {
                return SupportFailure.Board;
            }

            var capabilities = Metadata.Capabilities;
            if (requirement.Hardware.HasFlag(HardwareRequirement.SdCard) && !capabilities.HasSdCard)
            {
                return SupportFailure.Hardware;
            }

            if (requirement.Hardware.HasFlag(HardwareRequirement.WiFi) && !capabilities.HasWiFi)
            {
                return SupportFailure.Hardware;
            }

            return SupportFailure.None;
        }

        /// <summary>
        /// Throws a typed <see cref="FeatureNotSupportedException"/> unless this device supports
        /// <paramref name="feature"/>. The up-front counterpart to the firmware's wire-level
        /// <c>-113</c> "Undefined header" backstop: it pre-empts the round-trip for a feature the
        /// device is known to lack.
        /// </summary>
        /// <param name="feature">The feature the caller is about to use.</param>
        /// <exception cref="FeatureNotSupportedException">
        /// Thrown when <see cref="Supports"/> returns <c>false</c> for <paramref name="feature"/>.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="feature"/> has no entry in the requirement table.
        /// </exception>
        public void EnsureSupported(DeviceFeature feature)
        {
            var failure = EvaluateSupport(feature);
            if (failure == SupportFailure.None)
            {
                return;
            }

            // Report a required firmware version only when the version is what failed. A board or
            // hardware shortfall on a device whose firmware already meets the minimum would
            // otherwise produce a self-contradictory "Requires firmware >= 3.7.0; the device
            // reports '3.7.0'" — pointing the caller at an upgrade that cannot help.
            throw BuildFeatureNotSupportedException(
                feature,
                failure == SupportFailure.FirmwareVersion
                    ? DeviceFeatureRequirements.For(feature).MinVersion
                    : null);
        }

        /// <summary>
        /// Builds the typed <see cref="FeatureNotSupportedException"/> for a feature the firmware
        /// rejected on the wire, attributing the failure to the firmware version.
        /// </summary>
        /// <remarks>
        /// For the <c>-113</c> "Undefined header" backstop, where the device itself is the
        /// authority: the firmware does not recognize the command at all, so the required version
        /// from the table is the actionable answer and no table re-evaluation is wanted. Up-front
        /// checks go through <see cref="EnsureSupported"/> instead, which reports a required
        /// version only when the version is the axis that actually failed.
        /// </remarks>
        /// <param name="feature">The feature the device does not support.</param>
        /// <returns>The exception to throw.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="feature"/> has no entry in the requirement table.
        /// </exception>
        protected FeatureNotSupportedException CreateFeatureNotSupportedException(DeviceFeature feature) =>
            BuildFeatureNotSupportedException(feature, DeviceFeatureRequirements.For(feature).MinVersion);

        /// <summary>
        /// Assembles a <see cref="FeatureNotSupportedException"/> from the current
        /// <see cref="Metadata"/>, with the required version supplied by the caller so each entry
        /// point controls whether a firmware version is claimed as the cause.
        /// </summary>
        private FeatureNotSupportedException BuildFeatureNotSupportedException(
            DeviceFeature feature,
            FirmwareVersion? requiredVersion)
        {
            return new FeatureNotSupportedException(
                feature,
                requiredVersion,
                Metadata.FirmwareVersion,
                Metadata.DeviceType == DeviceType.Unknown ? null : Metadata.DeviceType);
        }

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
        /// Runs <paramref name="action"/> under the same lock that guards structural access to
        /// <see cref="_channels"/> and the status-driven <c>IsEnabled</c> resync in
        /// <see cref="PopulateChannelsFromStatus"/>. A subclass's channel-management API (e.g.
        /// enable/disable) should mutate <see cref="IChannel.IsEnabled"/> and compute any derived
        /// outbound state (an ADC/DIO enable mask) inside this critical section, so a concurrent
        /// status frame on the consumer thread cannot interleave between the mutation and the
        /// read that derives the mask — which would send a mask computed from a value the status
        /// frame is about to overwrite (#409). Callers must perform blocking I/O (e.g. <c>Send</c>)
        /// outside this method; the lock is reentrant, so calling <see cref="SnapshotChannels"/>
        /// from within <paramref name="action"/> is safe.
        /// </summary>
        protected T WithChannelsLock<T>(Func<T> action)
        {
            lock (_channelsLock)
            {
                return action();
            }
        }

        /// <inheritdoc cref="WithChannelsLock{T}"/>
        protected void WithChannelsLock(Action action)
        {
            lock (_channelsLock)
            {
                action();
            }
        }

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

        /// <summary>
        /// Sink for this device's diagnostics. Defaults to <see cref="NullLogger.Instance"/> when the
        /// caller opts out — the safety-relevant warnings (bad calibration/resolution → wrong scaled
        /// samples) are then simply discarded, as they were effectively invisible via the previous
        /// <c>Trace.WriteLine</c> path to consumers on Microsoft.Extensions.Logging. Never null.
        /// </summary>
        private readonly ILogger _logger;

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
        /// Occurs when a message queued via <see cref="Send{T}"/> fails to write to the device.
        /// </summary>
        /// <remarks>
        /// <see cref="Send{T}"/> is fire-and-forget, so this is the only way a caller can learn
        /// that a specific command never reached the device (issue #408) — a warning is also
        /// logged, but subscribing here lets a caller react (e.g. retry, surface a UI warning)
        /// instead of only reading it from logs. Raised on the producer's background thread and
        /// purely observational: it does not change <see cref="Status"/> or stop the queue.
        /// </remarks>
        public event EventHandler<MessageSendFailedEventArgs<string>>? SendFailed;

        /// <summary>
        /// Occurs when something fails on one of the device's background threads: a read from the
        /// transport stream, a parse, a dispatch to a subscriber, or the decode of a streaming frame.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Silence used to be the only symptom of these failures (issue #378) — a stream that cannot
        /// be read and a stream that cannot be decoded both looked exactly like a device sending
        /// nothing. This is the one place to answer "why am I getting no samples".
        /// </para>
        /// <para>
        /// <b>Observational only.</b> Raising this never changes device behaviour: no tear-down, no
        /// retry, no <see cref="Status"/> change, and a single bad frame is still isolated so the
        /// stream survives it. Deciding that a link is actually dead is separate, and arrives as
        /// <see cref="ConnectionStatus.Lost"/> on <see cref="StatusChanged"/> (issue #377). Every
        /// error raised here is also written to the device's <c>ILogger</c>, so it stays visible with
        /// no subscriber attached.
        /// </para>
        /// <para>
        /// <b>Throttle policy.</b> A systematic failure repeats at the frame rate, so raises are
        /// collapsed per bucket, where a bucket is
        /// (<see cref="DeviceErrorEventArgs.Source"/>, exception type):
        /// </para>
        /// <list type="bullet">
        /// <item><description>The first occurrence in a bucket is always raised, immediately.</description></item>
        /// <item><description>
        /// After that, a bucket raises at most once every five seconds. Occurrences in between are
        /// counted and reported as <see cref="DeviceErrorEventArgs.SuppressedCount"/> on the next
        /// raise, so a storm is visible as a number rather than as thousands of events.
        /// </description></item>
        /// <item><description>
        /// Buckets are independent: a new kind of failure is raised at once even while another kind
        /// is being collapsed.
        /// </description></item>
        /// </list>
        /// <para>
        /// Raised on a background thread (the reader loop, or whichever thread decoded the frame), so
        /// handlers should do the minimum and push real work elsewhere. A handler that throws is
        /// caught and ignored — it can never disturb reading or streaming.
        /// </para>
        /// </remarks>
        public event EventHandler<DeviceErrorEventArgs>? ErrorOccurred;

        /// <summary>
        /// Collapses repeated background failures so a systematic fault stays visible without
        /// storming <see cref="ErrorOccurred"/>. See that event for the documented policy.
        /// </summary>
        private readonly DeviceErrorThrottle _errorThrottle = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="DaqifiDevice"/> class.
        /// </summary>
        /// <param name="name">The name of the device.</param>
        /// <param name="ipAddress">The IP address of the device, if known.</param>
        /// <param name="logger">Optional logger for device diagnostics; a no-op logger is used when null.</param>
        public DaqifiDevice(string name, IPAddress? ipAddress = null, ILogger? logger = null)
        {
            Name = name;
            IpAddress = ipAddress;
            _status = ConnectionStatus.Disconnected;
            _logger = logger ?? NullLogger.Instance;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DaqifiDevice"/> class with a message producer.
        /// </summary>
        /// <param name="name">The name of the device.</param>
        /// <param name="stream">The stream for device communication.</param>
        /// <param name="ipAddress">The IP address of the device, if known.</param>
        /// <param name="logger">Optional logger for device diagnostics; a no-op logger is used when null.</param>
        public DaqifiDevice(string name, Stream stream, IPAddress? ipAddress = null, ILogger? logger = null)
        {
            Name = name;
            IpAddress = ipAddress;
            _status = ConnectionStatus.Disconnected;
            _logger = logger ?? NullLogger.Instance;
            _messageProducer = new MessageProducer<string>(stream);
            _messageProducer.SendFailed += OnMessageSendFailed;
            _directStream = stream;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DaqifiDevice"/> class with a transport.
        /// </summary>
        /// <param name="name">The name of the device.</param>
        /// <param name="transport">The transport for device communication.</param>
        /// <param name="logger">Optional logger for device diagnostics; a no-op logger is used when null.</param>
        public DaqifiDevice(string name, IStreamTransport transport, ILogger? logger = null)
        {
            Name = name;
            _status = ConnectionStatus.Disconnected;
            _logger = logger ?? NullLogger.Instance;
            _transport = transport;

            // Subscribe to transport status changes
            _transport.StatusChanged += OnTransportStatusChanged;
        }

        /// <summary>
        /// Serializes <see cref="ConnectCore"/> against <see cref="DisconnectCore"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Automatic reconnection (issue #379) introduced a second thread that opens and closes the
        /// transport, and cancellation is not synchronization: <see cref="SupersedeReconnect"/>
        /// asks the loop to stop and returns immediately, but a loop already inside a blocking
        /// <c>Connect()</c> cannot be interrupted and will run to completion. Without this, a
        /// caller's <see cref="Disconnect"/> could be opening and closing the same serial port
        /// concurrently, and both threads could build and start a message consumer — leaving two
        /// readers on one stream, the framing corruption this class refuses to risk anywhere else.
        /// </para>
        /// <para>
        /// Narrow on purpose. This is an internal lifecycle invariant — the device never drives its
        /// own transport from two threads at once — and deliberately <b>not</b> the general
        /// per-device operation serialization of issue #342, which has to decide ordering across
        /// the whole public API and interacts with <c>_textExchangeLock</c>. Nothing here changes
        /// what any public method does when uncontended.
        /// </para>
        /// <para>
        /// A <see cref="Monitor"/> rather than a semaphore because it is reentrant: both methods
        /// raise <see cref="StatusChanged"/> from inside their critical section, and a consumer
        /// handler calling <see cref="Disconnect"/> from there is re-entry on the same thread. That
        /// runs nested today with no lock at all, and must keep working rather than deadlocking.
        /// </para>
        /// </remarks>
        private readonly object _lifecycleLock = new();

        /// <summary>
        /// How long <see cref="Connect"/> waits for a lifecycle operation already in flight before
        /// giving up. Overridable for tests, mirroring <c>SdCardDownloadTimeout</c>.
        /// </summary>
        internal virtual TimeSpan LifecycleLockTimeout => TimeSpan.FromSeconds(10);

        /// <summary>
        /// How long <see cref="Disconnect"/> waits for a lifecycle operation already in flight
        /// before abandoning the wait. Far more generous than
        /// <see cref="LifecycleLockTimeout"/> because a teardown that gives up early is a teardown
        /// that did not happen. Overridable for tests, mirroring <c>SdCardDownloadTimeout</c>.
        /// </summary>
        internal virtual TimeSpan TeardownLockTimeout => TimeSpan.FromSeconds(30);

        /// <summary>
        /// What a lifecycle operation does when it cannot have <see cref="_lifecycleLock"/> to
        /// itself. Running anyway is deliberately not an option: the whole point of the lock is
        /// that two threads must never drive the transport at once, and a guarantee with a
        /// "proceed regardless" branch is not a guarantee.
        /// </summary>
        private enum LifecycleContention
        {
            /// <summary>
            /// Give up and throw rather than run alongside. For <see cref="Connect"/>: nothing has
            /// been opened, so failing costs the caller a retry and nothing else.
            /// </summary>
            Fail,

            /// <summary>
            /// Give up and report it, leaving the operation in flight alone. For
            /// <see cref="Disconnect"/>: the caller is told nothing was torn down rather than being
            /// blocked forever behind a holder that may never return.
            /// </summary>
            Abandon
        }

        /// <summary>
        /// Runs a lifecycle operation under <see cref="_lifecycleLock"/>, never alongside another.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The two callers want opposite things from contention, so neither a shared timeout nor a
        /// shared fallback would suit both.
        /// </para>
        /// <para>
        /// <b><see cref="Connect"/> fails.</b> It waits <see cref="LifecycleLockTimeout"/> and then
        /// throws. Opening a second connection alongside one already in flight is exactly what this
        /// lock exists to prevent — both threads would find no message consumer, both would build
        /// and start one, and the loser's reader would be left running on the same stream, silently
        /// corrupting frame boundaries for the rest of the session. A caller who gets a
        /// <see cref="TimeoutException"/> instead has lost nothing: no handle was opened, no state
        /// changed, and they can try again.
        /// </para>
        /// <para>
        /// <b><see cref="Disconnect"/> abandons.</b> It waits <see cref="TeardownLockTimeout"/> —
        /// far longer, because a teardown that gives up early is a teardown that did not happen —
        /// and then returns <c>false</c> without running, leaving the holder alone. It must not
        /// throw (<c>Dispose</c> depends on it) and must not run alongside (that is the corruption
        /// above), so reporting that nothing was torn down is what is left.
        /// </para>
        /// <para>
        /// An earlier revision waited here <i>without</i> a bound, on the reasoning that every
        /// possible holder is itself a bounded lifecycle operation. That reasoning was wrong:
        /// <c>SerialPort.Open</c> is called synchronously with no timeout and can wedge in
        /// uncancellable native I/O — a hazard this codebase already knows well enough to have
        /// built a process-wide port quarantine around it in
        /// <see cref="Discovery.SerialDeviceFinder"/>. An unbounded wait inherits that hang and
        /// turns <c>Dispose</c> into a permanent block. The house answer to uncancellable native
        /// I/O here is to abandon the stuck operation rather than wait on it, which is what this
        /// now does; the abandoned holder still cleans up after itself, because
        /// <see cref="_callerWantsDisconnected"/> is set before the wait begins and
        /// <see cref="AbandonIfSuperseded"/> tears down whatever it eventually built.
        /// </para>
        /// <para>
        /// Neither policy can deadlock: nothing that holds another lock in this class ever waits on
        /// a lifecycle operation (<c>_textExchangeLock</c> is taken <i>inside</i> this one, never
        /// the other way round), and <see cref="Monitor"/> grants re-entry immediately to a thread
        /// that already holds the lock — which is what keeps a handler calling <c>Disconnect</c>
        /// from inside a <see cref="StatusChanged"/> raise working rather than deadlocking against
        /// itself.
        /// </para>
        /// <para>
        /// An even earlier revision logged a warning and ran the operation anyway on timeout,
        /// copying the bargain <c>_textExchangeLock</c> strikes. That was the wrong precedent to
        /// borrow: a text-exchange timeout degrades a single command, whereas this one corrupts the
        /// stream for the whole session, which is not something to log and continue into.
        /// </para>
        /// </remarks>
        /// <returns><c>true</c> if the operation ran; <c>false</c> if the wait was abandoned.</returns>
        /// <exception cref="TimeoutException">
        /// Thrown when <paramref name="onContention"/> is <see cref="LifecycleContention.Fail"/> and
        /// another lifecycle operation held the lock for the whole timeout.
        /// </exception>
        private bool RunLifecycleExclusive(Action operation, LifecycleContention onContention)
        {
            var isTeardown = onContention == LifecycleContention.Abandon;
            var timeout = isTeardown ? TeardownLockTimeout : LifecycleLockTimeout;
            var acquired = false;

            try
            {
                // The ref overload is the documented-safe pattern: it sets the flag as part of
                // taking the lock, so the finally below can never miss a release.
                Monitor.TryEnter(_lifecycleLock, timeout, ref acquired);

                if (!acquired)
                {
                    if (isTeardown)
                    {
                        SafeLog(() => _logger.LogError(
                            "[Lifecycle] Device '{DeviceName}' could not take the connect/disconnect lock "
                            + "within {TimeoutSeconds}s, so nothing was torn down. A connect is most likely "
                            + "wedged in uncancellable native I/O; it will release its own session when it "
                            + "returns.",
                            Name,
                            timeout.TotalSeconds));

                        return false;
                    }

                    SafeLog(() => _logger.LogError(
                        "[Lifecycle] Device '{DeviceName}' could not take the connect/disconnect lock "
                        + "within {TimeoutSeconds}s; refusing to connect alongside the operation in flight.",
                        Name,
                        timeout.TotalSeconds));

                    throw new TimeoutException(
                        $"Device '{Name}' could not start connecting within "
                        + $"{timeout.TotalSeconds:0.#}s because another connect or disconnect "
                        + "was still in progress. Nothing was opened; retry once it has finished.");
                }

                operation();
                return true;
            }
            finally
            {
                if (acquired)
                {
                    Monitor.Exit(_lifecycleLock);
                }
            }
        }

        /// <summary>
        /// Connects to the device.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A caller-issued connect supersedes any automatic reconnect in progress: the loop is
        /// cancelled and unwinds without touching the session this call establishes.
        /// </para>
        /// <para>
        /// Cancelling the loop does not stop it instantly — an attempt already inside a blocking
        /// transport connect runs to completion — so this waits for any connect or disconnect in
        /// flight rather than running alongside it. If one is still in flight after
        /// <see cref="LifecycleLockTimeout"/>, this throws rather than opening a connection
        /// alongside it: nothing has been opened and no state has changed, so the call is safe to
        /// retry. See <see cref="_lifecycleLock"/> for why running anyway is not an option.
        /// </para>
        /// </remarks>
        /// <exception cref="TimeoutException">
        /// Thrown when another connect or disconnect was still in progress after
        /// <see cref="LifecycleLockTimeout"/>. Nothing was opened.
        /// </exception>
        public void Connect()
        {
            _callerWantsDisconnected = false;
            SupersedeReconnect();
            ConnectCore();
        }

        /// <summary>
        /// The body of <see cref="Connect"/>, without the reconnect-supersede step — the reconnect
        /// loop calls this so it does not cancel itself. Serialized against
        /// <see cref="DisconnectCore"/> so the two can never drive the transport at once.
        /// </summary>
        /// <remarks>
        /// Ends by honouring a teardown that landed while the connect was in flight. That matters
        /// most in the one case where the caller's <see cref="Disconnect"/> could not do it itself:
        /// a connect wedged in uncancellable native I/O holds the lifecycle lock long enough for
        /// the teardown to abandon its wait, so the teardown returns having deliberately left the
        /// transport alone — and whatever this connect goes on to build would otherwise be live,
        /// with a reader running, after the caller was told the device was disconnected.
        /// <para>
        /// The check lives here rather than in <see cref="Connect"/> because both entry points need
        /// it. <see cref="AbandonIfSuperseded"/> covers the same ground for the reconnect loop, but
        /// it is part of the loop and never runs for a caller's own connect.
        /// </para>
        /// <para>
        /// The ordering is not a race: <see cref="Disconnect"/> sets the flag <i>before</i> it
        /// contends for the lock, and this reads it <i>after</i> releasing it. A teardown that
        /// abandoned must therefore have been waiting while this still held the lock, so its write
        /// always happens-before this read.
        /// </para>
        /// </remarks>
        private void ConnectCore()
        {
            RunLifecycleExclusive(ConnectCoreUnsynchronized, LifecycleContention.Fail);

            if (_callerWantsDisconnected || _disposed)
            {
                SafeLog(() => _logger.LogWarning(
                    "[Lifecycle] Device '{DeviceName}' was disconnected while this connect was in flight; "
                    + "closing the connection it established.",
                    Name));

                DisconnectCore(ConnectionStatus.Disconnected);
            }
        }

        private void ConnectCoreUnsynchronized()
        {
            Status = ConnectionStatus.Connecting;
            State = DeviceState.Connecting;

            // A reconnect is a new session: its first background failure should be reported
            // immediately rather than collapsed into a throttle window the previous session opened.
            _errorThrottle.Reset();

            try
            {
                // Connect transport if available
                _transport?.Connect();

                // Create message producer and consumer from transport if needed
                if (_transport != null)
                {
                    // The reader/writer loops are the first thing to notice a device that has
                    // gone away; a transport that can act on that gets told (issue #382).
                    var healthSink = _transport as ITransportHealthSink;

                    if (_messageProducer == null)
                    {
                        _messageProducer = new MessageProducer<string>(_transport.Stream, healthSink: healthSink);
                        _messageProducer.SendFailed += OnMessageSendFailed;
                    }

                    if (_messageConsumer == null)
                    {
                        _messageConsumer = new StreamMessageConsumer<DaqifiOutMessage>(
                            _transport.Stream,
                            new ProtobufMessageParser(),
                            healthSink: healthSink);
                    }

                    // Read/parse/dispatch failures used to be raised into an event with no
                    // subscribers (issue #378). Subscribe here rather than alongside
                    // MessageReceived: that one is attached and detached around every consumer
                    // swap, and error visibility must not have holes in it. '-=' first keeps a
                    // reconnect on the same consumer instance from double-subscribing.
                    _messageConsumer.ErrorOccurred -= OnConsumerErrorOccurred;
                    _messageConsumer.ErrorOccurred += OnConsumerErrorOccurred;
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
        /// a race where an in-flight <see cref="ExecuteTextCommandAsync(Action, int, int, CancellationToken, Func{CancellationToken, Task})"/>
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
        /// <para>
        /// Also waits for any connect or disconnect already in flight — an automatic reconnect
        /// attempt parked inside a blocking transport connect, typically — rather than tearing down
        /// alongside it. That wait is bounded by <see cref="TeardownLockTimeout"/>, generously,
        /// because <c>SerialPort.Open</c> is uncancellable and can wedge indefinitely; waiting on
        /// it without a bound would make this method — and therefore <see cref="Dispose"/> — hang
        /// forever. If the wait is abandoned, the device still reports
        /// <see cref="ConnectionStatus.Disconnected"/>, but the transport is deliberately left to
        /// the stuck operation, which releases it when it finally returns. That outcome is logged
        /// at <b>error</b> level: it is a rare safety fallback rather than routine, it means a port
        /// is wedged and the transport was not released on the caller's schedule, and it is exactly
        /// the line an operator needs to still be there after log filtering. This never throws on
        /// contention.
        /// </para>
        /// </remarks>
        public void Disconnect()
        {
            // A user-issued teardown always beats an automatic reconnect: record the intent and
            // stop the loop before tearing anything down. Setting the flag first matters — a
            // reconnect already inside a blocking Connect() will finish it regardless, and this is
            // what tells it to put the session straight back down instead of leaving the device
            // quietly alive behind the caller's back.
            _callerWantsDisconnected = true;
            SupersedeReconnect();
            DisconnectCore(ConnectionStatus.Disconnected);
        }

        /// <summary>
        /// The body of <see cref="Disconnect"/>, parameterized by the status the device settles on.
        /// </summary>
        /// <param name="finalStatus">
        /// The status to report once teardown is done. <see cref="ConnectionStatus.Disconnected"/>
        /// for a caller-issued disconnect; <see cref="ConnectionStatus.Retrying"/> for the teardown
        /// the reconnect loop performs between attempts, which must not look to consumers like the
        /// session ended on purpose.
        /// </param>
        private void DisconnectCore(ConnectionStatus finalStatus)
        {
            if (RunLifecycleExclusive(
                    () => DisconnectCoreUnsynchronized(finalStatus),
                    LifecycleContention.Abandon))
            {
                return;
            }

            // The wait was abandoned: a lifecycle operation is stuck, most likely a
            // SerialPort.Open wedged in uncancellable native I/O. Racing it would be the
            // stream corruption this lock exists to prevent, so the transport is left to the
            // holder — which releases it once it unwedges, because _callerWantsDisconnected was
            // set before this wait began and every connect path re-reads it after dropping the
            // lock: ConnectCore for a caller's own connect, AbandonIfSuperseded for a reconnect
            // attempt. Both are needed — AbandonIfSuperseded belongs to the reconnect loop and
            // never runs for a caller's connect, which is how a wedged caller connect could
            // previously come back to life after Disconnect had already returned.
            //
            // What can still be done safely is record the caller's intent at the device level.
            // These are this class's own fields, not the transport, so setting them cannot
            // corrupt anything the stuck operation is doing — and without them the device would
            // keep reporting itself connected after the caller had asked it not to.
            State = DeviceState.Disconnected;
            _isInitialized = false;
            Status = finalStatus;
        }

        private void DisconnectCoreUnsynchronized(ConnectionStatus finalStatus)
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
                // Unsubscribe from message consumer/producer events
                if (_messageConsumer != null)
                {
                    _messageConsumer.MessageReceived -= OnInboundMessageReceived;
                    _messageConsumer.ErrorOccurred -= OnConsumerErrorOccurred;
                }

                if (_messageProducer != null)
                {
                    _messageProducer.SendFailed -= OnMessageSendFailed;
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
                Status = finalStatus;
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
        /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the device is connected but has no transport or stream to send on
        /// (e.g. the producer-less <see cref="DaqifiDevice(string, IPAddress, ILogger)"/> constructor).
        /// </exception>
        public virtual void Send<T>(IOutboundMessage<T> message)
        {
            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
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
        /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the device has no transport-based connection.</exception>
        protected virtual async Task ExecuteRawCaptureAsync(
            Func<Stream, CancellationToken, Task> rawAction,
            CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
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
                RestartMessageConsumerAfterSwap();
            }
        }

        /// <summary>
        /// Restarts the protobuf consumer after a swap (raw capture or text exchange) has stopped it.
        /// </summary>
        /// <remarks>
        /// The stop paths join the reader thread with a bounded timeout, so a reader parked in a slow
        /// blocking <see cref="Stream.Read(byte[], int, int)"/> can still be alive here.
        /// <see cref="StreamMessageConsumer{T}.Start"/> absorbs that case by waiting a grace period
        /// for the stopped reader to exit, which is what keeps a normal connect from failing with
        /// "a previous consumer thread has not yet exited" (issue #383).
        /// <para>
        /// If it still refuses, the reader's read is not returning at all — the stream is stuck.
        /// Deliberately do <b>not</b> recover by binding a fresh consumer to that same stream: a new
        /// instance would be a second concurrent reader on it, which is exactly the framing
        /// corruption the guard exists to prevent, and it would block on the stuck stream anyway.
        /// The consumer is left stopped; the operation's own failure (or the next
        /// <see cref="Connect"/>) surfaces the problem honestly.
        /// </para>
        /// <para>
        /// Never throws: it runs from <c>finally</c> blocks, where an exception would mask the real
        /// failure already unwinding. The consumer is also snapshotted once up front — the
        /// text-exchange path holds <c>_textExchangeLock</c> (which <see cref="Disconnect"/> waits
        /// on), but the raw-capture path does not, so a concurrent teardown could otherwise null the
        /// field between reads.
        /// </para>
        /// </remarks>
        private void RestartMessageConsumerAfterSwap()
        {
            var consumer = _messageConsumer;
            if (consumer == null)
            {
                return;
            }

            try
            {
                consumer.Start();
                consumer.MessageReceived += OnInboundMessageReceived;
            }
            catch (ConsumerThreadNotExitedException ex)
            {
                SafeLog(() => _logger.LogError(
                    ex,
                    "The previous message consumer thread did not exit, so the consumer was left stopped. "
                    + "The device stream appears stuck; a reconnect is required to resume inbound messages."));
            }
            catch (Exception ex)
            {
                // e.g. ObjectDisposedException from a concurrent Dispose(). Swallow rather than let
                // it escape a finally block and replace the operation's real exception.
                SafeLog(() => _logger.LogError(ex, "Failed to restart the message consumer after a stream swap."));
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
        /// <param name="prepareAsync">
        /// Optional phase that puts the device into the state the commands require, for callers that
        /// need one — the SD card operations use it to switch the shared SPI bus over to the card and
        /// wait for the firmware to settle.
        /// <para>
        /// It runs inside the device-wide text-exchange lock, so no competing exchange can interleave
        /// between it and <paramref name="setupAction"/> and undo what it established. It also runs
        /// before the consumer swap, and therefore before the stale-line boundary below: a settle
        /// wait placed inside <paramref name="setupAction"/> would widen that boundary into a window
        /// where a late reply to an earlier command could be captured as part of this response
        /// (#396). Anything the device emits in reply to it goes to the protobuf consumer, exactly as
        /// it did before this exchange began.
        /// </para>
        /// </param>
        /// <returns>
        /// A list of text lines received from the device. Lines that were already in flight when the
        /// exchange opened — late replies to earlier commands — are excluded: only what arrived once
        /// <paramref name="setupAction"/> had begun sending is returned.
        /// </returns>
        /// <exception cref="DeviceNotConnectedException">
        /// Thrown when the device is not connected, or — with
        /// <see cref="DeviceNotConnectedException.IsShuttingDown"/> set — when the device is
        /// disposed, disposing, or disconnecting.
        /// </exception>
        /// <exception cref="InvalidOperationException">Thrown when the device has no transport-based connection.</exception>
        /// <exception cref="TransportNotConnectedException">Thrown when the underlying transport has dropped.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
        // prepareAsync is added AFTER cancellationToken (technically violating CA1068
        // "CancellationToken should be last") to keep existing positional callers working, matching
        // the convention established in IFirmwareUpdateService for the same reason. It is a
        // parameter on this seam rather than a second virtual method deliberately: a parallel
        // method would be bypassed silently by any subclass that overrides only this one, which for
        // an instrumented device or a test double means the override quietly stops intercepting SD
        // operations with nothing to indicate it. Overriders must widen their signature — a compile
        // error, which is the point.
#pragma warning disable CA1068
        protected virtual Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Action setupAction,
            int responseTimeoutMs = 1000,
            int completionTimeoutMs = 250,
            CancellationToken cancellationToken = default,
            Func<CancellationToken, Task>? prepareAsync = null)
        {
            return ExecuteTextCommandCoreAsync(
                prepareAsync,
                _ => { setupAction(); return Task.CompletedTask; },
                responseTimeoutMs,
                completionTimeoutMs,
                cancellationToken);
        }
#pragma warning restore CA1068

        /// <summary>
        /// Async overload of <see cref="ExecuteTextCommandAsync(Action, int, int, CancellationToken, Func{CancellationToken, Task})"/>
        /// that accepts an async setup action so callers can <c>await</c> cancellable operations
        /// (e.g. <see cref="Task.Delay(int, CancellationToken)"/>) between SCPI commands without
        /// blocking the thread-pool thread.
        /// </summary>
        /// <param name="setupActionAsync">An async function that sends SCPI commands to the device while the text consumer is active. Receives the operation's cancellation token.</param>
        /// <param name="responseTimeoutMs">The time in milliseconds to wait for the first text response after sending commands.</param>
        /// <param name="completionTimeoutMs">The time in milliseconds of inactivity after the first response before considering the response complete. Defaults to 250ms.</param>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>A list of text lines received from the device.</returns>
        /// <exception cref="DeviceNotConnectedException">
        /// Thrown when the device is not connected, or — with
        /// <see cref="DeviceNotConnectedException.IsShuttingDown"/> set — when the device is
        /// disposed, disposing, or disconnecting.
        /// </exception>
        /// <exception cref="InvalidOperationException">Thrown when the device has no transport-based connection.</exception>
        /// <exception cref="TransportNotConnectedException">Thrown when the underlying transport has dropped.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
        protected virtual Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Func<CancellationToken, Task> setupActionAsync,
            int responseTimeoutMs = 1000,
            int completionTimeoutMs = 250,
            CancellationToken cancellationToken = default)
        {
            return ExecuteTextCommandCoreAsync(
                prepareAsync: null,
                setupActionAsync,
                responseTimeoutMs,
                completionTimeoutMs,
                cancellationToken);
        }

        private async Task<IReadOnlyList<string>> ExecuteTextCommandCoreAsync(
            Func<CancellationToken, Task>? prepareAsync,
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
            catch (ObjectDisposedException ex)
            {
                // Dispose() raced ahead of us and disposed the semaphore.
                // Surface the same clean failure as the post-acquisition
                // _disposed check below, instead of leaking a low-level
                // teardown exception to callers. The original is kept as
                // InnerException so this rare race stays diagnosable.
                throw new DeviceNotConnectedException(
                    "ExecuteTextCommandAsync cannot run because the device is disposed.",
                    ex,
                    isShuttingDown: true);
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
                    throw new DeviceNotConnectedException(
                        "ExecuteTextCommandAsync cannot run while the device is "
                        + "disposing or disconnecting.",
                        isShuttingDown: true);
                }

                if (!IsConnected)
                {
                    throw new DeviceNotConnectedException();
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

                // Prepare phase, if any. Deliberately here: inside the lock, so no competing text
                // exchange can interleave between it and the setup action below and undo the state
                // it establishes; and before the consumer swap, so the wait it typically needs
                // cannot widen the stale-line boundary taken further down. Any device output it
                // provokes goes to the protobuf consumer, which is still running at this point.
                if (prepareAsync != null)
                {
                    await prepareAsync(cancellationToken).ConfigureAwait(false);

                    SafeLog(() => _logger.LogDebug("[ExecuteTextCommandAsync] Prepare phase completed at {ElapsedMs}ms", sw.ElapsedMilliseconds));
                }

                var collectedLines = new List<string>();
                var stream = _transport.Stream;
                int? originalReadTimeout = null;

                // Number of lines that were already in flight when this exchange opened — see the
                // note at the point it is captured, below.
                var staleLineCount = 0;

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

                    SafeLog(() => _logger.LogDebug("[ExecuteTextCommandAsync] Protobuf consumer stopped at {ElapsedMs}ms", sw.ElapsedMilliseconds));

                    // Create a temporary text consumer on the same stream
                    using var textConsumer = new StreamMessageConsumer<string>(
                        _transport.Stream,
                        new LineBasedMessageParser(),
                        healthSink: _transport as ITransportHealthSink);

                    textConsumer.MessageReceived += (_, e) =>
                    {
                        collectedLines.Add(e.Message.Data);
                    };

                    // The protobuf consumer is stopped for the duration of this exchange, so
                    // without this a read failure during a text command (an unplug mid-SD-listing,
                    // say) would be the one background failure with nowhere to go (issue #378).
                    //
                    // Scoped rather than a bare '+=' because this consumer can outlive the block:
                    // its stop and dispose are both time-bounded and may return with the reader
                    // thread still parked in an un-returning read. A live thread roots the consumer,
                    // which would root this device through the handler — retaining the whole object
                    // graph and, worse, letting a zombie reader keep raising errors on a device that
                    // has since been disconnected. 'using' disposes in reverse declaration order, so
                    // this detaches before textConsumer itself is disposed, on every exit path
                    // including a cancellation or a throwing setup action.
                    using var textConsumerErrors = new ConsumerErrorSubscription(this, textConsumer);

                    textConsumer.Start();
                    // ConfigureAwait(false): the lock is held, so resuming on a captured
                    // sync context (e.g. UI thread) would deadlock if that thread calls Disconnect().
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);

                    SafeLog(() => _logger.LogDebug("[ExecuteTextCommandAsync] Text consumer started at {ElapsedMs}ms", sw.ElapsedMilliseconds));

                    // Mark the boundary between "already in flight" and "answers to this exchange".
                    // Anything captured before the setup action has sent anything is a late reply to
                    // an EARLIER command, or line noise — never a response to a command this exchange
                    // has yet to send. Those lines are dropped from the result below.
                    //
                    // Position matters as much as content: a caller that keys off response content —
                    // e.g. the SD listing's end-of-listing terminator (#396) — would otherwise accept
                    // a stale line as proof that the device answered a query it never even received,
                    // and report a complete listing for a device that has gone silent.
                    staleLineCount = collectedLines.Count;
                    if (staleLineCount > 0)
                    {
                        SafeLog(() => _logger.LogDebug(
                            "[ExecuteTextCommandAsync] Discarding {StaleLineCount} line(s) received before this exchange sent anything",
                            staleLineCount));
                    }

                    // Execute the setup action (sends SCPI commands). ConfigureAwait(false)
                    // matches the surrounding lock-protected awaits.
                    await setupActionAsync(cancellationToken).ConfigureAwait(false);

                    SafeLog(() => _logger.LogDebug("[ExecuteTextCommandAsync] Setup action completed at {ElapsedMs}ms", sw.ElapsedMilliseconds));

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
                                SafeLog(() => _logger.LogDebug("[ExecuteTextCommandAsync] First response at {ElapsedMs}ms", sw.ElapsedMilliseconds));
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

                    SafeLog(() => _logger.LogDebug("[ExecuteTextCommandAsync] Collection complete at {ElapsedMs}ms, {LineCount} lines", sw.ElapsedMilliseconds, collectedLines.Count));

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
                    RestartMessageConsumerAfterSwap();

                    SafeLog(() => _logger.LogDebug("[ExecuteTextCommandAsync] Total elapsed: {ElapsedMs}ms", sw.ElapsedMilliseconds));
                }

                // The text consumer is stopped by this point, so the list is no longer being
                // appended to concurrently and can be re-projected safely.
                return staleLineCount > 0
                    ? collectedLines.Skip(staleLineCount).ToList()
                    : collectedLines;
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
        /// Each iteration uses <see cref="ExecuteTextCommandAsync(Action, int, int, CancellationToken, Func{CancellationToken, Task})"/>, which
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
        /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the device has no transport-based connection.</exception>
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
                    SafeLog(() => _logger.LogDebug("[DrainErrorQueueAsync] Empty reply on iteration {Iteration}; terminating after {PoppedCount} popped entries.", i, popped.Count));
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

            SafeLog(() => _logger.LogWarning("[DrainErrorQueueAsync] Did not converge after {MaxIterations} iterations; queue may still contain entries.", maxIterations));
            return popped;
        }

        /// <summary>
        /// Lowest capability-document schema version daqifi-core will parse.
        /// </summary>
        public const int MinimumCapabilityDocumentApiVersion = 1;

        /// <summary>
        /// Highest capability-document schema version daqifi-core has been written against.
        /// </summary>
        /// <remarks>
        /// The firmware bumps its schema version only on a <i>breaking</i> change — a field
        /// renamed, removed, retyped, given new semantics, or the layout reshaped — while additive
        /// fields ship without a bump. A version above this one is therefore a document whose
        /// existing fields may no longer mean what this parser assumes, and trusting it could hand
        /// a consumer a plausible but wrong number. Such a device keeps its board-derived
        /// capabilities instead, which is the same safe outcome as a device that cannot answer at
        /// all. Raise this constant together with the parser when adopting a newer schema.
        /// </remarks>
        public const int MaximumCapabilityDocumentApiVersion = 2;

        /// <summary>
        /// Gap between the two capability queries sent in one exchange, so the firmware's SCPI
        /// parser sees two commands rather than one write it has to split.
        /// </summary>
        private const int CapabilityQuerySpacingMs = 50;

        /// <summary>Time allowed for the first line of the capability response.</summary>
        private const int CapabilityDocumentResponseTimeoutMs = 3000;

        /// <summary>
        /// Inactivity window that ends capability-document collection. The document is a single
        /// line of several kilobytes, so the window has to outlast the gaps <i>within</i> the
        /// transfer — otherwise, on a device that echoes commands, the echo would start the
        /// completion clock and cut the response off before its only useful line. Measured on the
        /// bench NQ1 over USB CDC: 8 KB delivered in ~25 ms end to end, longest inter-chunk gap
        /// 8 ms. 250 ms is an order of magnitude of headroom over that while keeping the cost to
        /// the connect sequence small.
        /// </summary>
        private const int CapabilityDocumentCompletionTimeoutMs = 250;

        /// <summary>
        /// Reads the device's capability document (<c>CONFigure:CAPabilities:JSON?</c>) and
        /// overlays it onto <see cref="DeviceMetadata.Capabilities"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Best-effort and non-destructive to what is already known. The read is skipped unless
        /// <see cref="Supports"/> reports <see cref="DeviceFeature.CapabilityDocument"/> (firmware
        /// v3.5.0 and newer), and the document is only trusted after
        /// <c>CONFigure:CAPabilities:APIVersion?</c> reports a schema version this parser
        /// understands. Any other outcome — an unanswered query, an out-of-range schema version,
        /// an unparseable reply — returns <c>null</c> and leaves the board-derived capabilities
        /// exactly as they were.
        /// </para>
        /// <para>
        /// The overlay is a merge: <see cref="DeviceCapabilities.FromDeviceType"/> remains the
        /// bootstrap and the fallback for every field the document does not state
        /// (<see cref="CapabilityDocument.MergeInto"/>). <see cref="InitializeAsync"/> calls this
        /// once, after the device reports its board and firmware version. Call it again whenever a
        /// fresh <see cref="CapabilityStreaming.CurrentMaximumRateHz"/> is needed — that figure is
        /// computed from the channel set enabled at the moment of the read, so it goes stale as
        /// soon as the enabled set changes.
        /// </para>
        /// <para>
        /// This runs a text-mode exchange, which pauses the protobuf consumer for its duration.
        /// Do not call it while streaming.
        /// </para>
        /// </remarks>
        /// <param name="cancellationToken">A cancellation token to observe while reading.</param>
        /// <returns>
        /// The parsed document, which has already been applied to <see cref="Metadata"/>; or
        /// <c>null</c> when the device did not supply one this parser can trust.
        /// </returns>
        /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
        public virtual async Task<CapabilityDocument?> ReadCapabilityDocumentAsync(
            CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            if (!Supports(DeviceFeature.CapabilityDocument))
            {
                SafeLog(() => _logger.LogDebug(
                    "[ReadCapabilityDocumentAsync] Skipped: the device does not report support for the capability document."));
                return null;
            }

            // Both queries go out in one text exchange. Swapping the protobuf consumer out and back
            // costs far more than either reply does — the reader thread has to time out of its
            // blocking read first — so a second exchange would roughly double what this adds to
            // the connect sequence, to fetch a document that measures 8 KB in ~25 ms. The version
            // is still checked before the document is *trusted*, which is what the gate is for;
            // asking for both up front only means a device on an unreadable schema transferred a
            // document that is then discarded.
            var lines = await ExecuteTextCommandAsync(
                async token =>
                {
                    Send(ScpiMessageProducer.GetCapabilitiesApiVersion);
                    await Task.Delay(CapabilityQuerySpacingMs, token).ConfigureAwait(false);
                    Send(ScpiMessageProducer.GetCapabilitiesJson);
                },
                responseTimeoutMs: CapabilityDocumentResponseTimeoutMs,
                completionTimeoutMs: CapabilityDocumentCompletionTimeoutMs,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!CapabilityDocumentParser.TryParseApiVersion(lines, out var apiVersion))
            {
                SafeLog(() => _logger.LogDebug(
                    "[ReadCapabilityDocumentAsync] The device did not report a capability schema version; keeping board-derived capabilities."));
                return null;
            }

            if (apiVersion < MinimumCapabilityDocumentApiVersion || apiVersion > MaximumCapabilityDocumentApiVersion)
            {
                SafeLog(() => _logger.LogDebug(
                    "[ReadCapabilityDocumentAsync] Capability schema version {ApiVersion} is outside the supported range {MinVersion}..{MaxVersion}; keeping board-derived capabilities.",
                    apiVersion,
                    MinimumCapabilityDocumentApiVersion,
                    MaximumCapabilityDocumentApiVersion));
                return null;
            }

            if (!CapabilityDocumentParser.TryParseLines(lines, out var document))
            {
                SafeLog(() => _logger.LogDebug(
                    "[ReadCapabilityDocumentAsync] The capability document did not parse; keeping board-derived capabilities."));
                return null;
            }

            // The document body carries the same schema version the query reports — the firmware
            // emits both from one macro. If they disagree, the two halves of this exchange did not
            // come from one coherent response (a stale or interleaved line), so the version that
            // was vetted above is not the version of the document in hand. Fail closed rather than
            // apply a document nothing actually vouched for.
            if (document.SchemaVersion != apiVersion)
            {
                SafeLog(() => _logger.LogDebug(
                    "[ReadCapabilityDocumentAsync] The document reports schema version {DocumentVersion} but the device reported {ApiVersion}; keeping board-derived capabilities.",
                    document.SchemaVersion,
                    apiVersion));
                return null;
            }

            Metadata.ApplyCapabilityDocument(document);
            return document;
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
                    // Read before raising Lost: a handler is free to call Connect or Disconnect
                    // synchronously from inside it, and if it does, this drop must not start a
                    // reconnect for a session the caller has already moved on from.
                    var epochAtDrop = Volatile.Read(ref _sessionEpoch);

                    // Snapshot the session BEFORE anything observes the drop. Raising Lost runs
                    // consumer handlers synchronously on this thread, and a handler is entirely
                    // entitled to start tearing the device down — by which point what the session
                    // looked like is no longer recoverable. No-op unless reconnect is enabled, so
                    // the default path does exactly what it did before (issue #379).
                    //
                    // Skipped while a reconnect is already running: a drop during an attempt would
                    // otherwise overwrite the snapshot of the session being restored with the empty
                    // half-built one, and the loop would go on to restore nothing.
                    if (ReconnectOptions.Enabled && !IsReconnecting)
                    {
                        try
                        {
                            CaptureSessionSnapshot();
                        }
                        catch (Exception ex)
                        {
                            SafeLog(() => _logger.LogWarning(
                                ex, "[Reconnect] Capturing the session state after a drop failed; the session cannot be restored."));
                        }
                    }

                    Status = ConnectionStatus.Lost;

                    BeginReconnectIfEnabled(epochAtDrop);
                }
            }
        }

        #region Automatic reconnection (issue #379)

        private ReconnectOptions _reconnectOptions = new();

        /// <summary>
        /// Gets or sets the policy for re-establishing a session after
        /// <see cref="ConnectionStatus.Lost"/>. Disabled by default: a drop is reported and nothing
        /// else happens, exactly as it always has been.
        /// </summary>
        /// <remarks>
        /// <para>
        /// With <see cref="ReconnectOptions.Enabled"/> set, a detected drop starts a background
        /// loop that reconnects the <em>same</em> endpoint, re-runs <see cref="InitializeAsync"/>,
        /// and restores the session state Core owns — the channel enable set, the streaming
        /// frequency, and an interrupted stream. Progress arrives on <see cref="ReconnectAttempt"/>
        /// / <see cref="Reconnected"/> / <see cref="ReconnectFailed"/> and as
        /// <see cref="ConnectionStatus.Retrying"/> between attempts.
        /// </para>
        /// <para>
        /// Deliberately <b>not</b> restored: anything the device owns rather than Core (DIO
        /// directions and output levels, PWM state, analog outputs, calibration written to RAM), an
        /// SD logging session, and any in-flight operation — an SD download interrupted by a drop
        /// fails, and is not resumed or retried.
        /// </para>
        /// <para>
        /// Same-endpoint only. A serial device that comes back on a different port path, or a
        /// device whose IP address changed, is a new endpoint and needs a fresh
        /// <c>DaqifiDeviceFactory</c> connect. Failing over to a different transport is out of
        /// scope entirely.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentNullException">Thrown when set to null.</exception>
        public ReconnectOptions ReconnectOptions
        {
            get => _reconnectOptions;
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                _reconnectOptions = value;
            }
        }

        /// <summary>
        /// Occurs before each automatic reconnect attempt, carrying the attempt number and the
        /// backoff wait that precedes it.
        /// </summary>
        /// <remarks>
        /// Raised on a background thread, like every event in this group. Handlers should do the
        /// minimum; one that throws is caught and ignored.
        /// </remarks>
        public event EventHandler<ReconnectAttemptEventArgs>? ReconnectAttempt;

        /// <summary>
        /// Occurs once an automatic reconnect has restored the session — the device is connected,
        /// re-initialized, its channel configuration re-applied, and an interrupted stream running
        /// again.
        /// </summary>
        public event EventHandler<ReconnectedEventArgs>? Reconnected;

        /// <summary>
        /// Occurs when automatic reconnection stops without restoring the session, because it ran
        /// out of attempts or was cancelled.
        /// </summary>
        /// <remarks>
        /// Running out of attempts is also raised on <see cref="ErrorOccurred"/> with
        /// <see cref="DeviceErrorSource.Reconnect"/> and leaves the device on
        /// <see cref="ConnectionStatus.Failed"/>, so giving up is impossible to miss even with
        /// nothing subscribed here. Cancellation is not an error and does neither.
        /// </remarks>
        public event EventHandler<ReconnectFailedEventArgs>? ReconnectFailed;

        // 0 = idle, 1 = a reconnect loop is running. Guards against a second loop being started by
        // the Lost that a failing attempt's own teardown can produce.
        private int _reconnectRunning;

        // Volatile: written by the thread that starts a loop and by the loop's own cleanup, read by
        // CancelReconnect from any thread. A stale read only ever delays a cancellation, never
        // corrupts one — the epoch check is what actually stops the loop — but there is no reason
        // to leave even that on the table.
        private volatile CancellationTokenSource? _reconnectCts;

        // Bumped by every caller-issued Connect/Disconnect/Dispose. The reconnect loop captures it
        // when it starts and re-checks before each step that touches device state; a change means
        // the caller has moved on and the loop must unwind. This is what keeps the loop from
        // re-opening a transport the caller just closed, without Disconnect() having to block on a
        // loop that may be calling back into it.
        private int _sessionEpoch;

        // Which way the caller last pointed the device. The epoch says *that* a caller superseded
        // the loop; this says *what they wanted*, which is what decides how the loop unwinds.
        // Connect() is a blocking, multi-second operation, so a caller can always land in the
        // middle of one — no amount of checking beforehand avoids that, and by the time the loop
        // looks again it may be holding a session it has just brought up. If the caller wants the
        // device down, that session is the loop's own doing and has to go back down with it;
        // if the caller wants it up, it is theirs and must be left strictly alone.
        private volatile bool _callerWantsDisconnected;

        /// <summary>
        /// Gets a value indicating whether an automatic reconnect is currently in progress.
        /// </summary>
        public bool IsReconnecting => Volatile.Read(ref _reconnectRunning) != 0;

        /// <summary>
        /// Stops any automatic reconnect in progress. Safe to call at any time, including when
        /// nothing is reconnecting.
        /// </summary>
        /// <remarks>
        /// The loop unwinds at its next checkpoint — it does not interrupt a connect attempt
        /// already in flight — and reports <see cref="ReconnectFailed"/> with
        /// <see cref="ReconnectFailedEventArgs.WasCanceled"/> set. The device is left on
        /// <see cref="ConnectionStatus.Lost"/>: the connection really is gone, and nothing is
        /// trying to bring it back. This returns immediately rather than waiting for the loop, so
        /// it is safe to call from a device event handler.
        /// </remarks>
        public void CancelReconnect()
        {
            try
            {
                _reconnectCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The loop finished and disposed its own token source. Nothing to cancel.
            }
        }

        /// <summary>
        /// Cancels any reconnect in progress <i>and</i> declares the session it was rebuilding
        /// obsolete, so the unwinding loop leaves the caller's new session strictly alone.
        /// </summary>
        private void SupersedeReconnect()
        {
            Interlocked.Increment(ref _sessionEpoch);
            CancelReconnect();
        }

        /// <summary>
        /// Starts the reconnect loop on a background thread, if the policy allows one and none is
        /// already running.
        /// </summary>
        /// <remarks>
        /// Called from the transport's status callback, which runs on the reader loop or the
        /// liveness timer — so the work is handed to the thread pool rather than done inline.
        /// </remarks>
        /// <param name="expectedEpoch">
        /// The session epoch observed before the drop was announced. A mismatch means a caller
        /// connected or disconnected in the meantime — including from inside their own
        /// <see cref="StatusChanged"/> handler — and this drop is no longer theirs to recover from.
        /// </param>
        private void BeginReconnectIfEnabled(int expectedEpoch)
        {
            if (!ReconnectOptions.Enabled || _transport == null || _disposed || _isDisconnecting)
            {
                return;
            }

            // Only ever start from a device that is actually sitting on a lost connection with the
            // session it was lost from still current.
            if (Status != ConnectionStatus.Lost || Volatile.Read(ref _sessionEpoch) != expectedEpoch)
            {
                return;
            }

            // One loop at a time. A failing attempt tears the transport down again, which can
            // produce another Lost; without this that would fork a second loop racing the first.
            if (Interlocked.CompareExchange(ref _reconnectRunning, 1, 0) != 0)
            {
                return;
            }

            var cts = new CancellationTokenSource();
            _reconnectCts = cts;

            var epoch = Volatile.Read(ref _sessionEpoch);
            var options = ReconnectOptions;

            _ = Task.Run(async () =>
            {
                var wasCanceled = false;

                try
                {
                    await RunReconnectLoopAsync(options, epoch, cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // The loop handles its own failures; anything escaping is a bug in it, and must
                    // not become an unobserved task exception.
                    SafeLog(() => _logger.LogError(ex, "[Reconnect] The reconnect loop terminated unexpectedly."));
                }
                finally
                {
                    wasCanceled = cts.IsCancellationRequested;

                    // Clear the token source before releasing the running flag, so a loop started
                    // by the next drop cannot have its own source nulled out from under it.
                    _reconnectCts = null;
                    cts.Dispose();
                    Interlocked.Exchange(ref _reconnectRunning, 0);
                }

                // A drop that landed in the moments between this loop finishing its work and
                // releasing the running flag would have been skipped by the single-flight guard,
                // stranding the device on Lost with nothing trying to bring it back. Pick it up.
                // Exhausting the attempts settles on Failed, and cancellation is excluded here, so
                // neither can retrigger.
                if (!wasCanceled)
                {
                    BeginReconnectIfEnabled(Volatile.Read(ref _sessionEpoch));
                }
            });
        }

        /// <summary>
        /// Attempts, with backoff, to rebuild the session that was just lost.
        /// </summary>
        /// <param name="options">
        /// The policy as it stood when the drop was detected. Assigning a new
        /// <see cref="ReconnectOptions"/> mid-flight therefore leaves this loop on the terms it
        /// started under and applies from the next drop; mutating the instance already assigned
        /// does reach it, since the loop holds that same object.
        /// </param>
        /// <param name="epoch">The session epoch at the time of the drop.</param>
        /// <param name="cancellationToken">Cancelled by <see cref="CancelReconnect"/>.</param>
        private async Task RunReconnectLoopAsync(
            ReconnectOptions options,
            int epoch,
            CancellationToken cancellationToken)
        {
            var startedAt = DateTime.UtcNow;
            Exception? lastError = null;
            var attempt = 0;

            SafeLog(() => _logger.LogInformation(
                "[Reconnect] Device '{DeviceName}' lost its connection; reconnecting (up to {MaxAttempts} attempt(s)).",
                Name,
                options.MaxAttempts));

            while (attempt < options.MaxAttempts)
            {
                attempt++;

                if (IsSessionStale(epoch) || cancellationToken.IsCancellationRequested)
                {
                    ReportReconnectStopped(epoch, attempt - 1, lastError, wasCanceled: true);
                    return;
                }

                var delay = options.CalculateDelay(attempt);
                RaiseReconnectEvent(ReconnectAttempt, new ReconnectAttemptEventArgs(
                    attempt, options.MaxAttempts, delay, lastError), nameof(ReconnectAttempt));

                try
                {
                    // Tear down what is left of the dead session first: the producer and consumer
                    // are still bound to a stream that is gone, and Connect() only rebuilds them
                    // once they have been nulled. Reported as Retrying, not Disconnected — nobody
                    // asked for this teardown.
                    DisconnectCore(ConnectionStatus.Retrying);

                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }

                    if (IsSessionStale(epoch))
                    {
                        ReportReconnectStopped(epoch, attempt, lastError, wasCanceled: true);
                        return;
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    ConnectCore();

                    // Connect() blocks for as long as opening the port takes, so a caller can — and
                    // on a slow serial open, will — land squarely in the middle of it. There is now
                    // a live transport and a running reader that this loop built, so bailing out
                    // here is not enough on its own: if the caller wants the device down, the
                    // session has to be taken back down with it.
                    if (AbandonIfSuperseded(epoch, attempt, lastError))
                    {
                        return;
                    }

                    await InitializeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

                    var streamingResumed = await RestoreSessionSnapshotAsync(
                        options, cancellationToken).ConfigureAwait(false);

                    // Same again before declaring victory: initialization and restore are several
                    // seconds of SCPI round-trips, and a session the caller has since disowned must
                    // not be handed to them as a successful reconnect.
                    if (AbandonIfSuperseded(epoch, attempt, lastError))
                    {
                        return;
                    }

                    SafeLog(() => _logger.LogInformation(
                        "[Reconnect] Device '{DeviceName}' reconnected on attempt {Attempt}.", Name, attempt));

                    RaiseReconnectEvent(Reconnected, new ReconnectedEventArgs(
                        attempt, DateTime.UtcNow - startedAt, streamingResumed), nameof(Reconnected));
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    ReportReconnectStopped(epoch, attempt, lastError, wasCanceled: true);
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    SafeLog(() => _logger.LogWarning(
                        ex,
                        "[Reconnect] Attempt {Attempt} of {MaxAttempts} to reconnect device '{DeviceName}' failed.",
                        attempt,
                        options.MaxAttempts,
                        Name));
                }
            }

            ReportReconnectStopped(epoch, attempt, lastError, wasCanceled: false);
        }

        /// <summary>
        /// Checks whether a caller has superseded the session this loop is rebuilding and, if so,
        /// unwinds whatever the loop has already brought up before reporting that it stopped.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Called at the points where the loop is holding a live session of its own making — after
        /// <see cref="ConnectCore"/>, and again once initialization and restore are done. Both are
        /// preceded by long blocking work, which is exactly when a caller's
        /// <see cref="Disconnect"/> lands, so a check beforehand cannot substitute for this one.
        /// </para>
        /// <para>
        /// What "unwind" means depends on what the caller wanted, which is why the epoch alone is
        /// not enough to decide it. After a <see cref="Disconnect"/> or a disposal the live session
        /// is the loop's own doing and is torn straight back down — leaving it up is a device the
        /// caller closed quietly coming back to life. After a caller's own <see cref="Connect"/>
        /// the session belongs to them, and tearing it down would be the same bug in reverse, so it
        /// is left alone and only logged.
        /// </para>
        /// </remarks>
        /// <returns><c>true</c> if the loop must stop.</returns>
        private bool AbandonIfSuperseded(int epoch, int attempt, Exception? lastError)
        {
            if (!IsSessionStale(epoch))
            {
                return false;
            }

            if (_callerWantsDisconnected || _disposed)
            {
                SafeLog(() => _logger.LogInformation(
                    "[Reconnect] Device '{DeviceName}' was disconnected while a reconnect attempt was in flight; "
                    + "closing the connection the attempt had established.",
                    Name));

                try
                {
                    DisconnectCore(ConnectionStatus.Disconnected);
                }
                catch (Exception ex)
                {
                    // Best-effort unwind of an abandoned attempt. A transport already disposed out
                    // from under us can throw here, and there is nothing left to salvage by
                    // letting that escape into the loop's retry logic.
                    SafeLog(() => _logger.LogDebug(
                        ex, "[Reconnect] Closing the abandoned reconnect attempt's connection failed."));
                }
            }
            else
            {
                SafeLog(() => _logger.LogWarning(
                    "[Reconnect] Device '{DeviceName}' was reconnected by its caller while an automatic "
                    + "reconnect attempt was in flight; leaving the caller's connection alone.",
                    Name));
            }

            ReportReconnectStopped(epoch, attempt, lastError, wasCanceled: true);
            return true;
        }

        /// <summary>
        /// True once a caller-issued connect, disconnect or disposal has superseded the session the
        /// reconnect loop was started for.
        /// </summary>
        /// <remarks>
        /// Deliberately does not consult <c>_isDisconnecting</c>: the loop's own teardown between
        /// attempts sets that flag, so reading it here would make the loop consider itself stale.
        /// A caller-issued <see cref="Disconnect"/> bumps the epoch before it sets the flag, which
        /// is what this actually needs to see.
        /// </remarks>
        private bool IsSessionStale(int epoch) =>
            _disposed || Volatile.Read(ref _sessionEpoch) != epoch;

        /// <summary>
        /// Settles the device after a reconnect loop that did not restore the session, and reports
        /// why.
        /// </summary>
        /// <remarks>
        /// Exhausting the attempts is terminal and loud: <see cref="ConnectionStatus.Failed"/>, a
        /// logged error, and an <see cref="ErrorOccurred"/> raise. Being cancelled is neither — the
        /// caller asked for it — so the device is simply left reporting
        /// <see cref="ConnectionStatus.Lost"/>, which is the truth. Neither touches
        /// <see cref="Status"/> at all once the session is stale, since by then the status belongs
        /// to whatever the caller did next.
        /// </remarks>
        private void ReportReconnectStopped(int epoch, int attemptsMade, Exception? lastError, bool wasCanceled)
        {
            if (!IsSessionStale(epoch))
            {
                // An attempt can fail after the transport is back up (a re-initialization that
                // times out, say), so tear the half-built session down rather than leaving a live
                // handle and a running reader behind a terminal status.
                DisconnectCore(wasCanceled ? ConnectionStatus.Lost : ConnectionStatus.Failed);
            }

            if (wasCanceled)
            {
                SafeLog(() => _logger.LogInformation(
                    "[Reconnect] Reconnection of device '{DeviceName}' was cancelled after {AttemptsMade} attempt(s).",
                    Name,
                    attemptsMade));
            }
            else
            {
                SafeLog(() => _logger.LogError(
                    lastError,
                    "[Reconnect] Device '{DeviceName}' could not be reconnected after {AttemptsMade} attempt(s); giving up.",
                    Name,
                    attemptsMade));

                // Terminal failure has to be impossible to miss, so it goes to the device error
                // surface as well as to this group's own event (issue #379 / #378).
                RaiseDeviceError(
                    DeviceErrorSource.Reconnect,
                    new DeviceReconnectFailedException(Name, attemptsMade, lastError));
            }

            RaiseReconnectEvent(
                ReconnectFailed,
                new ReconnectFailedEventArgs(attemptsMade, lastError, wasCanceled),
                nameof(ReconnectFailed));
        }

        /// <summary>
        /// Raises one of the reconnect events, isolating the loop from a subscriber that throws —
        /// the same guarantee <see cref="RaiseDeviceError"/> and <c>SendFailed</c> give.
        /// </summary>
        private void RaiseReconnectEvent<TArgs>(
            EventHandler<TArgs>? handler,
            TArgs args,
            string eventName)
            where TArgs : EventArgs
        {
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(this, args);
            }
            catch (Exception ex)
            {
                SafeLog(() => _logger.LogWarning(ex, "[{EventName}] subscriber threw", eventName));
            }
        }

        /// <summary>
        /// When overridden in a derived class, records the session state that a reconnect should
        /// restore. Called on the thread that detected the drop, before anything else observes it,
        /// and only when reconnect is enabled.
        /// </summary>
        /// <remarks>
        /// The base device owns nothing session-shaped — its channel collection is repopulated from
        /// the device's own status message on every connect — so this does nothing here.
        /// <see cref="DaqifiStreamingDevice"/> overrides it to record which channels were enabled
        /// and whether a stream was running.
        /// </remarks>
        protected virtual void CaptureSessionSnapshot()
        {
        }

        /// <summary>
        /// When overridden in a derived class, re-applies the state recorded by
        /// <see cref="CaptureSessionSnapshot"/> to a device that has just been reconnected and
        /// re-initialized.
        /// </summary>
        /// <param name="options">The policy governing this reconnect.</param>
        /// <param name="cancellationToken">Cancelled if reconnection is cancelled.</param>
        /// <returns><c>true</c> if an interrupted stream was restarted.</returns>
        protected virtual Task<bool> RestoreSessionSnapshotAsync(
            ReconnectOptions options,
            CancellationToken cancellationToken) => Task.FromResult(false);

        #endregion

        /// <summary>
        /// Logs a message that the producer's background thread could not deliver.
        /// </summary>
        /// <remarks>
        /// <see cref="IMessageProducer{T}.Send"/> is fire-and-forget, so this is the device's only
        /// visibility into a command that never reached the wire (issue #408). Purely observational:
        /// it does not change <see cref="Status"/> or retry the message — the producer already keeps
        /// draining the rest of the queue on its own.
        /// </remarks>
        private void OnMessageSendFailed(object? sender, MessageSendFailedEventArgs<string> e)
        {
            SafeLog(() => _logger.LogWarning(
                e.Error,
                "A queued message was not delivered to the device (timeout: {IsTimeout}).",
                e.IsTimeout));

            // A throwing subscriber must not be allowed to take down the producer's
            // background thread, so this goes through the same SafeLog guard as the logger above.
            SafeLog(() => SendFailed?.Invoke(this, e));
        }

        /// <summary>
        /// Forwards a message-consumer failure (a failed read, a parse error, or a subscriber that
        /// threw) to <see cref="ErrorOccurred"/>.
        /// </summary>
        /// <remarks>
        /// Nothing in Core subscribed to <see cref="IMessageConsumer{T}.ErrorOccurred"/> before
        /// issue #378, so these failures were raised into an empty event and lost. Forwarding them
        /// is purely additive — the consumer's own back-off and the transport's drop escalation are
        /// unchanged by whether anyone is listening here.
        /// </remarks>
        private void OnConsumerErrorOccurred(object? sender, MessageConsumerErrorEventArgs e)
        {
            RaiseDeviceError(DeviceErrorSource.MessageConsumer, e.Error, e.RawData);
        }

        /// <summary>
        /// Attaches a device's consumer-error forwarding to a short-lived
        /// <see cref="IMessageConsumer{T}"/> for the duration of a scope, and detaches it again on
        /// dispose.
        /// </summary>
        /// <remarks>
        /// Exists so a temporary consumer can never end up permanently subscribed. Stopping and
        /// disposing a consumer are both time-bounded and may return while its reader thread is
        /// still alive; that thread roots the consumer, and a still-attached handler would root this
        /// device through it. Detaching in a <c>finally</c> (which is what <c>using</c> compiles to)
        /// makes the subscription's lifetime exactly the scope's, whatever way control leaves it.
        /// </remarks>
        private sealed class ConsumerErrorSubscription : IDisposable
        {
            private readonly DaqifiDevice _device;
            private readonly IMessageConsumer<string> _consumer;

            public ConsumerErrorSubscription(DaqifiDevice device, IMessageConsumer<string> consumer)
            {
                _device = device;
                _consumer = consumer;
                _consumer.ErrorOccurred += _device.OnConsumerErrorOccurred;
            }

            public void Dispose()
            {
                _consumer.ErrorOccurred -= _device.OnConsumerErrorOccurred;
            }
        }

        /// <summary>
        /// Logs a background failure and raises <see cref="ErrorOccurred"/> for it, subject to the
        /// throttle policy documented on that event.
        /// </summary>
        /// <param name="source">The pipeline stage that failed.</param>
        /// <param name="error">The exception that was caught.</param>
        /// <param name="rawData">The bytes being processed at the time, if the stage had any.</param>
        /// <remarks>
        /// Never throws. It runs on background threads inside catch blocks whose entire purpose is
        /// to keep reading and decoding alive, so neither a throwing logger nor a throwing
        /// subscriber may escape — the same isolation <c>SendFailed</c> and the classified-event
        /// raisers use.
        /// </remarks>
        protected void RaiseDeviceError(DeviceErrorSource source, Exception error, byte[]? rawData = null)
        {
            if (error == null)
            {
                return;
            }

            if (!_errorThrottle.ShouldRaise(source, error, out var suppressedCount))
            {
                return;
            }

            SafeLog(() => _logger.LogWarning(
                error,
                "[{Source}] Device '{DeviceName}' background failure ({SuppressedCount} like failure(s) suppressed since the last report).",
                source,
                Name,
                suppressedCount));

            var handler = ErrorOccurred;
            if (handler == null)
            {
                return;
            }

            // Same guard as the logger above: a subscriber that throws must not take down the
            // reader loop or the decode path this was raised from.
            SafeLog(() => handler(this, new DeviceErrorEventArgs(source, error, suppressedCount, rawData)));
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
        /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
        /// <exception cref="ScpiInitializationErrorException">Thrown when the device returns a SCPI error during initialization that persists after an internal retry.</exception>
        /// <exception cref="TimeoutException">Thrown when the device does not report its channel configuration within <paramref name="channelPopulationTimeout"/>.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
        public virtual async Task InitializeAsync(
            TimeSpan? channelPopulationTimeout = null,
            CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new DeviceNotConnectedException("Device must be connected before initialization.");
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

                // Ask the device to describe itself, now that it has reported its board and
                // firmware version — Supports(CapabilityDocument) fails closed on an unknown
                // firmware version, so an earlier read would skip on every device. This is its own
                // SCPI round-trip rather than something folded into status processing: the
                // document does not ride along in the protobuf status message, and a text exchange
                // pauses the protobuf consumer, which is only safe at a quiescent point like this
                // one. It runs before OnDeviceInitializingAsync so derived-class initialization
                // already sees the device's own capabilities.
                await TryReadCapabilityDocumentAsync(cancellationToken).ConfigureAwait(false);

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
        /// Runs the capability-document read during initialization, absorbing any failure.
        /// </summary>
        /// <remarks>
        /// The document is an enrichment, never a requirement: a device that cannot supply one is
        /// fully usable on its board-derived capabilities, so letting a failed read fail the whole
        /// initialization would newly refuse devices that work today. Cancellation still
        /// propagates — that is the caller's own request, not a device fault.
        /// </remarks>
        private async Task TryReadCapabilityDocumentAsync(CancellationToken cancellationToken)
        {
            try
            {
                await ReadCapabilityDocumentAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                SafeLog(() => _logger.LogDebug(
                    ex,
                    "[InitializeAsync] Reading the capability document failed; keeping board-derived capabilities."));
            }
        }

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
        private void RaiseClassifiedEvent(Action<DaqifiOutMessage>? handler, DaqifiOutMessage message, string eventName)
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
                SafeLog(() => _logger.LogWarning(ex, "[{EventName}] classified-event subscriber threw", eventName));
            }
        }

        /// <summary>
        /// Runs a logging call, swallowing any exception a misbehaving <see cref="ILogger"/> throws.
        /// A consumer-supplied logger must never affect device operation — least of all in
        /// <see cref="RaiseClassifiedEvent"/>, whose whole purpose is to isolate frame processing
        /// from faults. Mirrors <c>MessageProducer.SafeLog</c>.
        /// </summary>
        private static void SafeLog(Action logAction)
        {
            try
            {
                logAction();
            }
            catch
            {
                // A logger that throws is not permitted to take down device operation.
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
        /// Analog channel <c>IsEnabled</c> is likewise taken from the device-reported enabled mask
        /// (field 22, <c>analog_in_port_enabled</c>) when the message carries one, so it reflects
        /// the device's own view rather than only what Core previously commanded.
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
                // IChannel references — and the configuration on them (direction/output/PWM
                // state) — survive a routine status re-population untouched. IsEnabled is the
                // exception: analog channels resync it from the device-reported enabled mask
                // (field 22) whenever the device sends one, so Core's view cannot silently drift
                // from the device's (#409).
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
            var analogInPortEnabled = message.AnalogInPortEnabled;

            // Firmware before v3.5.0 never populates this field, so an empty byte string is
            // ambiguous between "no channels enabled" and "not reported". Only trust it as the
            // source of truth for IsEnabled when the device actually sent something.
            var enabledIsReported = analogInPortEnabled.Length > 0;

            var count = (int)message.AnalogInPortNum;

            // Treat both a missing (0) and a physically-implausible out-of-range resolution as
            // "assumed": the AnalogChannel constructor/setters now reject anything outside
            // [MinResolution, MaxResolution], so passing a corrupt non-zero value straight through
            // would throw and abort channel population mid-stream. Fall back to a safe default and
            // log instead, so a corrupted status frame can neither crash population nor silently
            // corrupt every scaled sample on the reuse path (UpdateScalingFromStatus below).
            var resolutionIsAssumed = analogInResolution is < AnalogChannel.MinResolution or > AnalogChannel.MaxResolution;
            var resolution = resolutionIsAssumed ? 65535u : analogInResolution;

            if (resolutionIsAssumed && count > 0)
            {
                SafeLog(() => _logger.LogWarning("[PopulateAnalogChannels] Device '{DeviceName}' reported no usable ADC resolution (analog_in_res={Resolution}) for {ChannelCount} analog channel(s); assuming {AssumedResolution}. Scaled samples on this device may be systematically wrong.", Name, analogInResolution, count, resolution));
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
                    if (enabledIsReported)
                    {
                        existingAnalog.IsEnabled = IsChannelBitSet(analogInPortEnabled, i);
                    }
                    destination.Add(existingAnalog);
                    continue;
                }

                var channel = new AnalogChannel(i, resolution, resolutionIsAssumed)
                {
                    Name = $"AI{i}",
                    Direction = ChannelDirection.Input,
                    IsEnabled = enabledIsReported && IsChannelBitSet(analogInPortEnabled, i),
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
                SafeLog(() => _logger.LogWarning("[PopulateAnalogChannels] Device '{DeviceName}' reported invalid {FieldName}={Value} for analog channel {ChannelIndex}; substituting {Fallback}. Scaled samples on this channel may be affected.", Name, fieldName, value, channelIndex, fallback));
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
                SafeLog(() => _logger.LogWarning("[PopulateAnalogChannels] Device '{DeviceName}' reported invalid portRange={Value} for analog channel {ChannelIndex}; substituting 1.0. Scaled samples on this channel may be affected.", Name, value, channelIndex));
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
        /// Reads bit <paramref name="channelNumber"/> from a device-reported per-channel enable
        /// bitmask (analog_in_port_enabled, field 22 — confirmed bit-packed on the bench: 2 bytes
        /// for 16 channels, little-endian, bit <c>n</c> = channel <c>n</c> — the same layout Core
        /// sends outbound via <see cref="Communication.Producers.ScpiMessageProducer.EnableAdcChannels"/>).
        /// Returns false when the channel number falls outside the bytes actually sent.
        /// </summary>
        private static bool IsChannelBitSet(Google.Protobuf.ByteString mask, int channelNumber)
        {
            var byteIndex = channelNumber / 8;
            return byteIndex < mask.Length && (mask[byteIndex] & (1 << (channelNumber % 8))) != 0;
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
