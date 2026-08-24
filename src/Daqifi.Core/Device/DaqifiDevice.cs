using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device.Capabilities;
using Daqifi.Core.Device.Internal;
using Daqifi.Core.Device.Protocol;
using Daqifi.Core.Firmware;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// <remarks>
    /// <para>
    /// <b>Thread accounting (issue #491).</b> A connected, idle device holds exactly two dedicated
    /// background threads: the <see cref="MessageProducer{T}"/> write loop and the protobuf
    /// <see cref="StreamMessageConsumer{T}"/> read loop started in <c>CompleteConnect</c>. Neither
    /// spins — the producer parks on a sticky wait with no polling timeout, and the consumer blocks
    /// in <c>Stream.Read</c> — so a fleet of N connected devices holds 2N threads regardless of
    /// traffic, not 2N busy loops.
    /// </para>
    /// <para>
    /// A text (SCPI) exchange (<see cref="TextExchangeEngine"/>) temporarily adds a third: the
    /// protobuf consumer is stopped and, on restart, gets a fresh thread rather than resuming its
    /// old one, and the transient line-based text consumer used to collect the reply is itself a
    /// new thread per exchange. That is two thread creations per exchange, not one — but each is
    /// tens of microseconds next to the exchange's own read-timeout and polling waits, so it does
    /// not move measured cost the way the idle-cost fixes in #491 items 1 and 2 did (delivered in
    /// #514). Eliminating it would require the consumer to survive a stop/start cycle by parking
    /// on an event instead of exiting, which roots the device (and its stream/transport) for as
    /// long as the thread is parked — a memory-leak risk for no measurable gain, and it overlaps
    /// the read-loop redesign #485 needs. See #491 for the full triage; this file documents the
    /// remaining known-cost rather than reworking it.
    /// </para>
    /// </remarks>
    public class DaqifiDevice : IDevice, IDisposable, IAsyncDisposable, ITextExchangeHost, IOperationSerializationHost
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
        /// Throws <see cref="DeviceNotConnectedException"/> when this device is not connected.
        /// </summary>
        /// <remarks>
        /// The device-side face of <see cref="ConnectionGuard"/>: the operation collaborators run
        /// the same guard through their host seam (<c>_host.EnsureConnected()</c>), while the few
        /// guards that live on the device itself go through here. Declared as an instance method
        /// rather than another <see cref="ConnectionGuard"/> extension so that a call inside this
        /// class or <see cref="DaqifiStreamingDevice"/> — which also implements the host interfaces
        /// — binds here unambiguously.
        /// </remarks>
        /// <exception cref="DeviceNotConnectedException">The device is not connected.</exception>
        internal void EnsureConnected() => ConnectionGuard.EnsureConnected(IsConnected);

        /// <summary>
        /// Throws <see cref="DeviceNotConnectedException"/> when this device is not connected, then
        /// throws if <paramref name="cancellationToken"/> has already been cancelled.
        /// </summary>
        /// <param name="cancellationToken">The caller's cancellation token.</param>
        /// <exception cref="DeviceNotConnectedException">The device is not connected.</exception>
        internal void EnsureConnected(CancellationToken cancellationToken)
            => ConnectionGuard.EnsureConnected(IsConnected, cancellationToken);

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
        /// A counter that changes whenever this device's set of channels, or any of their enabled
        /// states, changes.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Lets a caller that would otherwise re-derive something from <see cref="Channels"/> on
        /// every frame cache that derivation instead and rebuild it only when this value moves. The
        /// streaming decoder does exactly that with the sorted set of active analog channels, which
        /// it used to snapshot, filter and sort a thousand times a second for a set that changes
        /// when someone configures the device (issue #490).
        /// </para>
        /// <para>
        /// It is a change token, not a count: only inequality with a previously observed value is
        /// meaningful. Read it <em>before</em> taking the snapshot you intend to cache — a change
        /// landing in between then makes the cached value merely stale-by-one-frame rather than
        /// stale forever.
        /// </para>
        /// </remarks>
        internal long ChannelStateVersion => Interlocked.Read(ref _channelStateVersion);

        /// <summary>
        /// Bumps <see cref="ChannelStateVersion"/>. Subscribed to every channel this device owns,
        /// so that a caller writing <c>channel.IsEnabled = true</c> directly — which
        /// <see cref="IChannel"/> permits and no device API observes — invalidates the same caches
        /// that <see cref="PopulateChannelsFromStatus"/> does.
        /// </summary>
        private void OnChannelEnablementChanged() => Interlocked.Increment(ref _channelStateVersion);

        /// <summary>
        /// Whether <paramref name="updated"/> holds a different set of channel instances, in a
        /// different order, from <paramref name="current"/>.
        /// </summary>
        /// <remarks>
        /// Instance identity, not <c>(type, number)</c> identity. Two channels can carry the same
        /// number and type and still be different objects — the populator builds a new one whenever
        /// it cannot reuse the old — and anything caching the old instance would keep writing
        /// samples into a channel the device has replaced.
        /// </remarks>
        private static bool MembershipChanged(List<IChannel> current, List<IChannel> updated)
        {
            if (current.Count != updated.Count)
            {
                return true;
            }

            for (var i = 0; i < current.Count; i++)
            {
                if (!ReferenceEquals(current[i], updated[i]))
                {
                    return true;
                }
            }

            return false;
        }

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

        /// <summary>
        /// When <c>true</c>, <see cref="InitializeAsync"/> omits the initialization commands that
        /// would halt or re-route a stream the device is already running, so connecting does not
        /// disturb a session started elsewhere. Default is <c>false</c> — the historical behavior,
        /// where connecting takes control of the device.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Streaming is a single global device state: one acquisition at one rate, delivered to one
        /// interface. The default initialization sequence stops that stream, sets the device's power
        /// state, fixes the stream format, and (over USB) routes the stream to this connection. That
        /// is correct when this session owns the device — it also clears a stream orphaned by a
        /// previously crashed session — but a <em>second</em> session running it silently ends the
        /// first session's acquisition, with no error surfaced to either side.
        /// </para>
        /// <para>
        /// With this set, initialization sends only <c>SYSTem:ECHO -1</c> (so replies to this
        /// connection can be parsed) followed by the read-only identity and capability queries.
        /// Skipped are <c>SYSTem:StopStreamData</c>, <c>SYSTem:POWer:STATe 1</c>,
        /// <c>SYSTem:STReam:FORmat 0</c>, and the USB <c>SYSTem:STReam:INTerface</c> routing step.
        /// </para>
        /// <para>
        /// The resulting session is fully usable for status, metadata, channel inspection, and any
        /// command the caller chooses to send. It is <em>not</em> configured to stream: the device's
        /// stream format and destination interface are left exactly as the other session left them,
        /// and stream frames continue to go wherever they were already going. A session that later
        /// wants to stream itself must take control of the device, which necessarily stops whatever
        /// the other session was doing.
        /// </para>
        /// <para>
        /// Read once, when <see cref="InitializeAsync"/> runs; changing it afterwards has no effect.
        /// This guards only against this library's own connect sequence — it is not device-side
        /// arbitration, and two processes can still fight over one unit. Within a single process,
        /// prefer <see cref="DaqifiDeviceRegistry"/>, which refuses to open the same physical unit
        /// twice.
        /// </para>
        /// </remarks>
        public bool PreserveActiveStream { get; set; }

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

        // "Teardown has finished" — read by ExecuteTextCommandCoreAsync to reject work on a dead
        // device. Distinct from _disposeClaimed below, which marks teardown as *started*.
        private bool _disposed;

        // Disposal gate, 0 until a caller claims teardown. Interlocked rather than a bool because
        // Dispose() and DisposeAsync() are documented as interchangeable and DisposeAsync spends
        // real awaited time inside the window a plain flag would leave open. See TryClaimDisposal.
        private int _disposeClaimed;
        private bool _isDisconnecting;
        private bool _isInitialized;
        private readonly List<IChannel> _channels = new();

        // Guards structural access to _channels: the consumer thread repopulates it
        // (Clear/Add in PopulateChannelsFromStatus) while caller threads fold over a
        // snapshot via SnapshotChannels for the device-level channel-management API.
        private readonly object _channelsLock = new();

        // Backing counter for ChannelStateVersion. Interlocked because it is bumped from whichever
        // thread changed a channel and read from the message-consumer thread.
        private long _channelStateVersion;

        // Translates a status frame's channel description into channel instances. Stateless, so
        // it is built once per device and reused for every population.
        private readonly StatusChannelPopulator _channelPopulator;

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
        /// Default wait for <see cref="RefreshDeviceStatusAsync"/>. A device that is answering at
        /// all replies in milliseconds; this is a hang detector, not a prediction.
        /// </summary>
        private static readonly TimeSpan DefaultStatusRefreshTimeout = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Serializes <see cref="RefreshDeviceStatusAsync"/>. Two concurrent refreshes would both
        /// subscribe to the same multicast <see cref="StatusMessageReceived"/> event, and a single
        /// incoming frame -- which answers only one of the two requests -- would complete both, so
        /// the loser would return a success not tied to its own request.
        /// </summary>
        private readonly SemaphoreSlim _statusRefreshGate = new(1, 1);

        /// <summary>
        /// The waiter for an in-flight <see cref="RefreshDeviceStatusAsync"/>, invoked directly
        /// rather than through the public <see cref="StatusMessageReceived"/> event.
        /// </summary>
        /// <remarks>
        /// <see cref="RaiseClassifiedEvent"/> wraps the WHOLE multicast delegate in one try/catch,
        /// which isolates the device from a throwing subscriber but not subscribers from each
        /// other: .NET stops invoking an invocation list at the first exception, so a consumer
        /// that subscribed earlier and throws would prevent the refresh from ever seeing its own
        /// reply -- and it would time out while the status it asked for had already been applied.
        /// Only one refresh runs at a time (<see cref="_statusRefreshGate"/>), so a single field
        /// is enough.
        /// </remarks>
        private Action<DaqifiOutMessage>? _statusRefreshWaiter;

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

        /// <summary>
        /// Owns THE device operation lock and the backlog of sends parked behind it. Every text
        /// exchange, every <see cref="RunExclusiveAsync{TResult}"/> block, every deferred
        /// <see cref="Send{T}"/> and both teardown paths coordinate through this one collaborator
        /// (issues #186, #342; extracted by #480).
        /// </summary>
        private readonly OperationSerializer _operations;

        // Async-context flag that tracks whether the current logical flow
        // is inside a consumer swap — ExecuteTextCommandAsync's or ExecuteRawCaptureAsync's; both
        // stop the protobuf consumer and take the stream. AsyncLocal flows across await
        // resumptions on different threads, so a setupAction that re-enters
        // ExecuteTextCommandAsync after a ConfigureAwait(false) hop is still
        // detected and surfaced as InvalidOperationException — instead of
        // corrupting the consumer swap mid-flight. Plain
        // Environment.CurrentManagedThreadId capture wouldn't work — the
        // value seen before await may not match the value seen after.
        //
        // Distinct from OperationSerializer's ownership generation: this one says "a consumer swap
        // is in progress on this flow" (nesting is a bug), that one says "this flow holds the lock"
        // (nesting is fine).
        private readonly AsyncLocal<bool> _isInsideTextExchange = new();

        /// <summary>
        /// How long <see cref="Disconnect"/> / <see cref="DisconnectAsync"/> wait to acquire the
        /// operation lock before tearing down anyway. See the remarks on
        /// <see cref="Disconnect"/> for how the budget is derived.
        /// </summary>
        private static readonly TimeSpan TextExchangeTeardownWait = TimeSpan.FromSeconds(10);


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
                RaiseConnectionStatusChanged(_status);
                RaiseStatusChanged(_status);
            }
        }

        /// <summary>
        /// Tells a derived device inside this library that the connection status has changed, before
        /// consumers are notified on <see cref="StatusChanged"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Internal rather than protected: this is wiring between this class and the collaborators
        /// the library ships, not an extension point. It exists because some of those collaborators
        /// hold work that only makes sense on a live session — the live-sample enumerations behind
        /// <see cref="DaqifiStreamingDevice.StreamSamplesAsync"/>, which used to park forever once
        /// the device went away (issue #496).
        /// </para>
        /// <para>
        /// Runs before <see cref="StatusChanged"/> so that a consumer's handler already sees the
        /// library's own reaction as done, and is isolated by <see cref="RaiseConnectionStatusChanged"/>
        /// exactly as a consumer's handler is. An override still has no business throwing, but a
        /// transition is never allowed to fail — that is the whole of issue #494, and the rule cannot
        /// be weaker for the library's own code than it is for a consumer's.
        /// </para>
        /// </remarks>
        /// <param name="status">The status the device has just moved to.</param>
        internal virtual void OnConnectionStatusChanged(ConnectionStatus status)
        {
        }

        /// <summary>
        /// Calls <see cref="OnConnectionStatusChanged"/>, isolating a throwing override from the
        /// transition it is reacting to.
        /// </summary>
        /// <remarks>
        /// The same guarantee, for the same reason, as <see cref="RaiseStatusChanged"/> one line
        /// further on: this runs on the drop path, where an escaping exception used to skip the
        /// reconnect start and unwind into the transport before it had released the port handle
        /// (issue #494). A failure here is reported on <see cref="ErrorOccurred"/> and the transition
        /// completes regardless.
        /// </remarks>
        private void RaiseConnectionStatusChanged(ConnectionStatus status)
        {
            try
            {
                OnConnectionStatusChanged(status);
            }
            catch (Exception ex)
            {
                RaiseDeviceError(DeviceErrorSource.StatusNotification, ex);
            }
        }

        /// <summary>
        /// Occurs when the device status changes.
        /// </summary>
        /// <remarks>
        /// Raised on whichever thread observed the change — for a drop, a background watchdog or
        /// reader thread. A subscriber that throws (a UI framework's cross-thread
        /// <see cref="InvalidOperationException"/> is the usual one) is isolated: the exception is
        /// reported on <see cref="ErrorOccurred"/> as
        /// <see cref="DeviceErrorSource.StatusNotification"/> and the transition completes
        /// regardless.
        /// </remarks>
        public event EventHandler<DeviceStatusEventArgs>? StatusChanged;

        /// <summary>
        /// Raises <see cref="StatusChanged"/>, isolating everything downstream of the transition
        /// from a subscriber that throws — the same guarantee <see cref="RaiseDeviceError"/>,
        /// <see cref="RaiseReconnectEvent{TArgs}"/> and <c>SendFailed</c> already give.
        /// </summary>
        /// <remarks>
        /// The drop path is what makes this load-bearing (issue #494). A drop runs
        /// <c>transport.HandleConnectionLost</c> → <see cref="OnTransportStatusChanged"/> →
        /// <c>Status = Lost</c> → this event → <c>BeginReconnectIfEnabled</c>, all on one thread.
        /// An escaping subscriber exception used to skip the reconnect start entirely (so
        /// <see cref="ReconnectOptions.Enabled"/> silently did nothing) and unwind back into the
        /// transport before it had released the port handle, leaving the OS port claimed until the
        /// process exited. It then vanished into a background loop's catch, so the consumer saw a
        /// dead, unreconnectable device with no error at all.
        /// </remarks>
        private void RaiseStatusChanged(ConnectionStatus status)
        {
            var handler = StatusChanged;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(this, new DeviceStatusEventArgs(status));
            }
            catch (Exception ex)
            {
                // Logs and raises ErrorOccurred, both isolated: a status change is never allowed to
                // fail, so this must not throw either.
                RaiseDeviceError(DeviceErrorSource.StatusNotification, ex);
            }
        }

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
        private DeviceErrorThrottle _errorThrottle = new();

        /// <summary>
        /// Test seam: replaces the background-error throttle, so a test can widen the collapsing
        /// window far beyond the default instead of racing it. Never called in production.
        /// </summary>
        /// <remarks>
        /// A test that wants to prove a reconnect clears the throttle otherwise has to observe two
        /// errors inside the default five-second window — which makes the result depend on how
        /// quickly the machine happened to run, in both directions: too slow and the second error
        /// is due anyway (the test passes without proving anything), tighten the bound and a loaded
        /// CI box fails a correct implementation. Widening the window removes the clock from the
        /// question entirely. Call before connecting; the field is read from background threads.
        /// </remarks>
        /// <param name="throttle">The throttle to use.</param>
        internal void SetErrorThrottleForTesting(DeviceErrorThrottle throttle)
        {
            _errorThrottle = throttle ?? throw new ArgumentNullException(nameof(throttle));
        }

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
            _channelPopulator = new StatusChannelPopulator(_logger, () => Name);
            _lifecycleGate = new LifecycleGate(
                _logger,
                () => Name,
                () => LifecycleLockTimeout,
                () => TeardownLockTimeout);
            _textExchange = new TextExchangeEngine(this);
            _operations = new OperationSerializer(this);
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
            _channelPopulator = new StatusChannelPopulator(_logger, () => Name);
            _lifecycleGate = new LifecycleGate(
                _logger,
                () => Name,
                () => LifecycleLockTimeout,
                () => TeardownLockTimeout);
            _textExchange = new TextExchangeEngine(this);
            _operations = new OperationSerializer(this);
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
            _channelPopulator = new StatusChannelPopulator(_logger, () => Name);
            _lifecycleGate = new LifecycleGate(
                _logger,
                () => Name,
                () => LifecycleLockTimeout,
                () => TeardownLockTimeout);
            _textExchange = new TextExchangeEngine(this);
            _operations = new OperationSerializer(this);
            _transport = transport;

            // Subscribe to transport status changes
            _transport.StatusChanged += OnTransportStatusChanged;
        }

        #region Lifecycle serialization (issue #379)

        /// <summary>
        /// Serializes connect against disconnect, on both the synchronous and the asynchronous
        /// paths, so the device never drives its transport from two threads at once. See
        /// <see cref="LifecycleGate"/> for why that invariant exists and what each contention
        /// policy does.
        /// </summary>
        /// <remarks>
        /// The timeouts are handed over as delegates so the gate reads them at the moment of
        /// contention: they are <c>virtual</c> below precisely so a test can shorten them, and a
        /// test subclass sets its override through an <c>init</c> property that runs after this
        /// constructor. Reading them here would capture the defaults and ignore every override.
        /// </remarks>
        private readonly LifecycleGate _lifecycleGate;

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

        #endregion

        #region Operation serialization (issue #342)

        /// <summary>
        /// The most messages <see cref="Send{T}"/> will park while another flow owns the device
        /// before it starts discarding the oldest of them.
        /// </summary>
        /// <remarks>
        /// Exclusive operations can be very long — an SD card download is allowed thirty minutes and
        /// a firmware update longer — so an uncapped backlog grows with the sender's rate for as
        /// long as the operation runs, and then fires every one of those stale commands at the
        /// device in a burst when it ends. The cap is far above any backlog an ordinary text query
        /// can build (those are measured in milliseconds), so it only ever engages on the long
        /// exclusive operations it exists for.
        /// </remarks>
        public const int DefaultMaxDeferredSends = 1024;

        /// <summary>
        /// The live backlog cap. A seam for tests, which need a small cap to reach the drop path in
        /// bounded time; production always reads <see cref="DefaultMaxDeferredSends"/>. Values below
        /// one are clamped, so an override can never make the append path throw.
        /// </summary>
        internal virtual int MaxDeferredSends => DefaultMaxDeferredSends;

        /// <summary>
        /// Gets the cumulative number of messages passed to <see cref="Send{T}"/> that were
        /// discarded because the backlog of messages parked during an exclusive operation was full
        /// (drop-oldest policy, capped at <see cref="DefaultMaxDeferredSends"/>). A non-zero and
        /// growing value means commands are being issued faster than a long-running exclusive
        /// operation can let them out.
        /// </summary>
        public long DroppedDeferredSendCount => _operations.DroppedDeferredSendCount;

        /// <summary>
        /// How long the text exchange lets the outbound queue drain before it takes the stream.
        /// </summary>
        /// <remarks>
        /// Short on purpose: it is only covering messages queued microseconds before the exchange
        /// opened, and the exchange has its own stale-line boundary as a backstop.
        /// </remarks>
        internal virtual TimeSpan OutboundDrainWait => TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// Runs <paramref name="operation"/> with exclusive use of the device: no other operation,
        /// text query or command send from another thread runs alongside it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Individual calls are already safe to make from any thread. This exists for the case a
        /// single call cannot express — a <b>sequence</b> that must not be split, such as "set the
        /// direction, then drive the pin" or "set duty, then frequency, then enable". Without it,
        /// two threads each doing that can have their commands interleaved and leave the device in
        /// a state neither asked for.
        /// </para>
        /// <para>
        /// While the operation runs, a <see cref="Send{T}"/> from another thread is <b>deferred</b>,
        /// not blocked: it returns immediately, as fire-and-forget always has, and the message goes
        /// out in order once this operation finishes. Text queries from other threads wait, exactly
        /// as they already waited for each other. A long operation paired with a busy sender can
        /// park more than the backlog holds, in which case the oldest parked messages are dropped —
        /// see <see cref="Send{T}"/> and <see cref="DroppedDeferredSendCount"/>.
        /// </para>
        /// <para>
        /// Reentrant on the same logical flow, so the body is free to call anything on the device,
        /// including the SD card and diagnostic methods that open a text exchange of their own, and
        /// including <see cref="Disconnect"/>. <see cref="Connect"/> is the one exception worth
        /// knowing about: it takes the lifecycle lock, which a concurrent <see cref="Disconnect"/>
        /// takes <i>before</i> this one, so reconnecting from inside an exclusive block can cost
        /// both sides their bounded wait. Reconnect outside the block.
        /// </para>
        /// <para>
        /// Keep the body short and do not fan out inside it: work started with
        /// <c>Task.Run</c>/<c>_ = SomethingAsync()</c> inherits the flow's ownership of the lock, so
        /// it would not be deferred and could still interleave. Teardown does not wait forever
        /// either — <see cref="Disconnect"/> gives an in-flight operation a bounded courtesy wait
        /// and then tears down regardless.
        /// </para>
        /// </remarks>
        /// <param name="operation">The sequence to run exclusively.</param>
        /// <param name="cancellationToken">Observed while waiting for the lock, then handed to the operation.</param>
        /// <returns>A task that completes when the operation has finished.</returns>
        /// <exception cref="DeviceNotConnectedException">Thrown when the device has been disposed.</exception>
        /// <exception cref="OperationCanceledException">Thrown when cancelled while waiting for the lock.</exception>
        public Task RunExclusiveAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(operation);

            return RunExclusiveAsync<object?>(
                async ct =>
                {
                    await operation(ct).ConfigureAwait(false);
                    return null;
                },
                cancellationToken);
        }

        /// <inheritdoc cref="RunExclusiveAsync(Func{CancellationToken, Task}, CancellationToken)"/>
        /// <typeparam name="TResult">The type the operation produces.</typeparam>
        /// <returns>The operation's result.</returns>
        public async Task<TResult> RunExclusiveAsync<TResult>(
            Func<CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(operation);

            // Delegated whole: the ownership flag the body must observe is an AsyncLocal written in
            // the serializer's own frame, and the body is that frame's callee. Awaiting here keeps
            // it that way — see OperationSerializer.RunExclusiveAsync.
            return await _operations.RunExclusiveAsync(operation, cancellationToken).ConfigureAwait(false);
        }

        #endregion

        /// <summary>
        /// Connects to the device.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Opens the transport on the calling thread. <see cref="ConnectAsync"/> is the
        /// non-blocking, cancellable equivalent and is preferred on a UI thread; this overload is
        /// kept for existing callers and behaves exactly as it always has.
        /// </para>
        /// <para>
        /// A caller-issued connect supersedes any automatic reconnect in progress: the loop is
        /// cancelled and unwinds without touching the session this call establishes. Cancelling it
        /// does not stop it instantly — an attempt already inside a blocking transport connect runs
        /// to completion — so this waits for any connect or disconnect in flight rather than
        /// running alongside it, and throws if one is still in flight after
        /// <see cref="LifecycleLockTimeout"/>. Nothing has been opened in that case, so the call is
        /// safe to retry.
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
        /// loop calls this so it does not cancel itself.
        /// </summary>
        private void ConnectCore()
        {
            _lifecycleGate.Run(ConnectCoreUnsynchronized, LifecycleContention.Fail);
            HonourTeardownRaisedDuringConnect();
        }

        /// <inheritdoc cref="ConnectCore"/>
        private async Task ConnectCoreAsync(CancellationToken cancellationToken)
        {
            await _lifecycleGate.RunAsync(
                () => ConnectCoreUnsynchronizedAsync(cancellationToken),
                LifecycleContention.Fail,
                cancellationToken).ConfigureAwait(false);

            HonourTeardownRaisedDuringConnect();
        }

        /// <summary>
        /// Closes a connection this call established if a teardown landed while it was in flight.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Matters in the one case where the caller's <see cref="Disconnect"/> could not do it
        /// itself: a connect wedged in uncancellable native I/O holds the lifecycle lock long enough
        /// for the teardown to abandon its wait, so the teardown returns having deliberately left
        /// the transport alone — and whatever this connect goes on to build would otherwise be live,
        /// with a reader running, after the caller was told the device was disconnected.
        /// </para>
        /// <para>
        /// Shared by both connect entry points rather than living in the reconnect loop:
        /// <see cref="AbandonIfSuperseded"/> covers the same ground for a reconnect attempt, but it
        /// is part of the loop and never runs for a caller's own connect.
        /// </para>
        /// <para>
        /// The ordering is not a race: <see cref="Disconnect"/> sets the flag <i>before</i> it
        /// contends for the lock, and this reads it <i>after</i> releasing it. A teardown that
        /// abandoned must therefore have been waiting while the connect still held the lock, so its
        /// write always happens-before this read.
        /// </para>
        /// </remarks>
        private void HonourTeardownRaisedDuringConnect()
        {
            if (!_callerWantsDisconnected && !_disposed)
            {
                return;
            }

            SafeLog(() => _logger.LogWarning(
                "[Lifecycle] Device '{DeviceName}' was disconnected while this connect was in flight; "
                + "closing the connection it established.",
                Name));

            DisconnectCore(ConnectionStatus.Disconnected);
        }

        private void ConnectCoreUnsynchronized()
        {
            BeginConnect();

            try
            {
                // Connect transport if available
                _transport?.Connect();

                CompleteConnect();
            }
            catch
            {
                FailConnect();
                throw;
            }
        }

        /// <summary>
        /// Connects to the device, abandoning the attempt if <paramref name="cancellationToken"/>
        /// is signalled.
        /// </summary>
        /// <remarks>
        /// The asynchronous counterpart to <see cref="Connect"/>: it never blocks the calling
        /// thread on the transport handshake, and the token is threaded all the way down to
        /// <see cref="IStreamTransport.ConnectAsync(ConnectionRetryOptions?, CancellationToken)"/>,
        /// so an attempt can be given up mid-flight — including between retries, where the retry
        /// loop would otherwise keep dialling. A cancel that lands after the transport has come up
        /// closes it again, so a cancelled attempt never leaves a half-open connection behind.
        /// </remarks>
        /// <param name="cancellationToken">A cancellation token to observe while connecting.</param>
        /// <returns>A task representing the asynchronous connect operation.</returns>
        /// <exception cref="OperationCanceledException">Thrown when the attempt is canceled.</exception>
        /// <exception cref="TimeoutException">
        /// Thrown when another connect or disconnect was still in progress after
        /// <see cref="LifecycleLockTimeout"/>. Nothing was opened.
        /// </exception>
        // Deliberately not virtual, matching Connect(). A virtual async twin of a non-virtual sync
        // method is a trap: a subclass would override this one, leave Connect() unintercepted, and
        // get different behavior depending on which entry point the caller reached for.
        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            _callerWantsDisconnected = false;
            SupersedeReconnect();
            await ConnectCoreAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task ConnectCoreUnsynchronizedAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            BeginConnect();

            var transportConnected = false;
            try
            {
                if (_transport != null)
                {
                    await _transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
                    transportConnected = true;
                }

                cancellationToken.ThrowIfCancellationRequested();

                CompleteConnect();
            }
            catch (OperationCanceledException)
            {
                FailConnect();

                // The transport opened and then the caller gave up — close it rather than leave a
                // live connection owned by a device that reports itself disconnected. Matters most
                // for a reconnect loop (issue #379), where the leaked handle would still be holding
                // the serial port when the next attempt tries to open it.
                if (transportConnected)
                {
                    await SafeDisconnectTransportAsync().ConfigureAwait(false);
                }

                throw;
            }
            catch
            {
                FailConnect();
                throw;
            }
        }

        /// <summary>
        /// Marks the device as connecting and opens a fresh error-reporting session. Shared entry
        /// point for <see cref="Connect"/> and <see cref="ConnectAsync"/>.
        /// </summary>
        private void BeginConnect()
        {
            Status = ConnectionStatus.Connecting;
            State = DeviceState.Connecting;

            // A reconnect is a new session: its first background failure should be reported
            // immediately rather than collapsed into a throttle window the previous session opened.
            // Lives here, not in Connect(), so the async path resets it too — the factory connects
            // through ConnectAsync, so leaving it on the sync path alone would mean the primary
            // connect path silently kept the previous session's throttle state (issue #378).
            _errorThrottle.Reset();
        }

        /// <summary>
        /// Builds (when needed) and starts the message pumps over the now-open transport, then
        /// marks the device connected. Shared by <see cref="Connect"/> and
        /// <see cref="ConnectAsync"/> so the sync and async paths cannot drift apart.
        /// </summary>
        private void CompleteConnect()
        {
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

        /// <summary>
        /// Rolls the device's reported state back to disconnected after a failed or abandoned
        /// connect attempt.
        /// </summary>
        private void FailConnect()
        {
            Status = ConnectionStatus.Disconnected;
            State = DeviceState.Disconnected;
        }

        /// <summary>
        /// Best-effort transport close used on the cancellation path, where an exception must not
        /// replace the <see cref="OperationCanceledException"/> the caller is waiting for.
        /// </summary>
        private async Task SafeDisconnectTransportAsync()
        {
            if (_transport == null)
            {
                return;
            }

            try
            {
                await _transport.DisconnectAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SafeLog(() => _logger.LogDebug(
                    ex,
                    "Failed to close the transport after a cancelled connect attempt."));
            }
        }

        /// <summary>
        /// Disconnects from the device.
        /// </summary>
        /// <remarks>
        /// Waits up to 10 seconds to acquire the device operation lock before
        /// tearing down the consumer / producer / transport. This prevents
        /// a race where an in-flight <see cref="ExecuteTextCommandAsync(Action, int, int, CancellationToken, Func{CancellationToken, Task}, Func{Task}, bool)"/>
        /// is mid-swap (text consumer running on the stream, protobuf
        /// consumer not yet restarted) and Disconnect rips the transport
        /// out from under it. If the wait times out, Disconnect proceeds
        /// anyway — a stuck text exchange must not block teardown forever.
        /// The 10s budget covers the worst-case ExecuteTextCommandAsync
        /// hold time with default timeouts (StopSafely up to 1s + maxWait
        /// of responseTimeoutMs*5 = 5s by default + safety margin) and
        /// most custom-timeout callers; on timeout the in-flight exchange
        /// sees <c>_isDisconnecting == true</c> via the post-acquisition
        /// validation and bails out cleanly. Callers wanting a non-blocking
        /// disconnect should use <see cref="DisconnectAsync"/>.
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
            if (_lifecycleGate.Run(
                    () => DisconnectCoreUnsynchronized(finalStatus),
                    LifecycleContention.Abandon))
            {
                return;
            }

            MarkDisconnectedWithoutTeardown(finalStatus);
        }

        /// <inheritdoc cref="DisconnectCore"/>
        private async Task DisconnectCoreAsync(ConnectionStatus finalStatus, CancellationToken cancellationToken)
        {
            if (await _lifecycleGate.RunAsync(
                    () => DisconnectCoreUnsynchronizedAsync(finalStatus, cancellationToken),
                    LifecycleContention.Abandon,
                    cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            MarkDisconnectedWithoutTeardown(finalStatus);
        }

        /// <summary>
        /// Settles the device's reported state when teardown had to be abandoned.
        /// </summary>
        /// <remarks>
        /// The wait was abandoned because a lifecycle operation is stuck, most likely a
        /// <c>SerialPort.Open</c> wedged in uncancellable native I/O. Racing it would be the stream
        /// corruption the lifecycle lock exists to prevent, so the transport is left to the holder —
        /// which releases it once it unwedges, because <see cref="_callerWantsDisconnected"/> was
        /// set before this wait began and every connect path re-reads it after dropping the lock:
        /// <see cref="HonourTeardownRaisedDuringConnect"/> for a caller's own connect,
        /// <see cref="AbandonIfSuperseded"/> for a reconnect attempt.
        /// <para>
        /// What can still be done safely is record the caller's intent at the device level. These
        /// are this class's own fields, not the transport, so setting them cannot corrupt anything
        /// the stuck operation is doing — and without them the device would keep reporting itself
        /// connected after the caller had asked it not to.
        /// </para>
        /// </remarks>
        private void MarkDisconnectedWithoutTeardown(ConnectionStatus finalStatus)
        {
            State = DeviceState.Disconnected;
            _isInitialized = false;
            Status = finalStatus;
        }

        private void DisconnectCoreUnsynchronized(ConnectionStatus finalStatus)
        {
            _isDisconnecting = true;
            var lockAcquired = AcquireTextExchangeLockForTeardown();

            try
            {
                StopMessagePumps();

                // Disconnect transport if available
                _transport?.Disconnect();
            }
            finally
            {
                FinishDisconnect(lockAcquired, finalStatus);
            }
        }

        /// <summary>
        /// Disconnects from the device without blocking the calling thread.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The asynchronous counterpart to <see cref="Disconnect"/>, and the one to use on a UI
        /// thread: the courtesy wait for an in-flight text exchange (up to
        /// <see cref="TextExchangeTeardownWait"/>) is awaited rather than blocked on, and the
        /// remaining teardown — joining the reader/writer threads and closing the transport — is
        /// pushed off the caller's thread.
        /// </para>
        /// <para>
        /// <b>What the token does.</b> It shortens the courtesy wait: cancelling stops waiting for
        /// the in-flight exchange and proceeds straight to teardown, exactly as the timeout does.
        /// It never aborts the disconnect itself, and this method does not throw
        /// <see cref="OperationCanceledException"/> — a teardown abandoned half-way would leave
        /// producers, consumers and the transport in an indeterminate state, which is strictly
        /// worse than finishing. On return the device is always disconnected.
        /// </para>
        /// <para>
        /// <see cref="StatusChanged"/> is therefore raised on a thread pool thread rather than the
        /// caller's; marshal to your UI thread in the handler if that matters.
        /// </para>
        /// </remarks>
        /// <param name="cancellationToken">A cancellation token to observe while disconnecting.</param>
        /// <returns>A task representing the asynchronous disconnect operation.</returns>
        // Not virtual, for the same reason as ConnectAsync.
        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            // Same intent-first ordering as Disconnect: see the comment there.
            _callerWantsDisconnected = true;
            SupersedeReconnect();
            await DisconnectCoreAsync(ConnectionStatus.Disconnected, cancellationToken).ConfigureAwait(false);
        }

        private async Task DisconnectCoreUnsynchronizedAsync(
            ConnectionStatus finalStatus,
            CancellationToken cancellationToken)
        {
            _isDisconnecting = true;
            var lockAcquired = await AcquireTextExchangeLockForTeardownAsync(cancellationToken)
                .ConfigureAwait(false);

            try
            {
                // StopSafely joins the reader/writer threads with a bounded timeout, and closing a
                // serial port whose device has gone away can stall: neither belongs on a UI thread,
                // so the whole teardown runs on the thread pool. The ConfigureAwait(false) below
                // keeps the transport close and the finally block there too.
                await Task.Run(StopMessagePumps, CancellationToken.None).ConfigureAwait(false);

                if (_transport != null)
                {
                    await _transport.DisconnectAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                FinishDisconnect(lockAcquired, finalStatus);
            }
        }

        /// <summary>
        /// Best-effort coordination with <c>ExecuteTextCommandAsync</c> before teardown: acquire
        /// the lock so the transport is not torn out from under an in-flight text exchange. The
        /// lock IS released by <see cref="FinishDisconnect"/> when acquired (so a future
        /// <see cref="Connect"/> followed by <c>ExecuteTextCommandAsync</c> isn't blocked); a stuck
        /// exchange that holds past the timeout drops to the <c>_isDisconnecting</c> validation
        /// path inside the exchange.
        /// </summary>
        /// <returns><c>true</c> when the lock was acquired and must be released after teardown.</returns>
        private bool AcquireTextExchangeLockForTeardown() =>
            _operations.TryAcquireForTeardown(TextExchangeTeardownWait);

        /// <inheritdoc cref="AcquireTextExchangeLockForTeardown"/>
        /// <param name="cancellationToken">
        /// Shortens the wait. A cancellation is swallowed rather than propagated: teardown must
        /// still run, and the in-flight exchange sees <c>_isDisconnecting == true</c> and bails out
        /// on its own — the same outcome as letting the wait time out.
        /// </param>
        private Task<bool> AcquireTextExchangeLockForTeardownAsync(CancellationToken cancellationToken) =>
            _operations.TryAcquireForTeardownAsync(TextExchangeTeardownWait, cancellationToken);

        /// <summary>
        /// Unsubscribes, stops and drops the message producer/consumer. Shared by
        /// <see cref="Disconnect"/> and <see cref="DisconnectAsync"/>.
        /// </summary>
        private void StopMessagePumps()
        {
            // Unsubscribe from message consumer/producer events
            if (_messageConsumer != null)
            {
                DetachInboundMessages(_messageConsumer);
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
        }

        /// <summary>
        /// Settles the device's reported state after teardown and releases the text-exchange lock
        /// if it was taken. Runs from a <c>finally</c> in both disconnect paths, so the device can
        /// never be left reporting Connected because teardown threw.
        /// </summary>
        /// <param name="lockAcquired">Whether the text-exchange lock was acquired before teardown.</param>
        /// <param name="finalStatus">
        /// The status to settle on. Parameterized for the reconnect loop, whose teardown between
        /// attempts reports <see cref="ConnectionStatus.Retrying"/> rather than
        /// <see cref="ConnectionStatus.Disconnected"/> — nobody asked for that teardown, and it
        /// must not look to consumers like the session ended on purpose (issue #379).
        /// </param>
        private void FinishDisconnect(bool lockAcquired, ConnectionStatus finalStatus)
        {
            Status = finalStatus;
            State = DeviceState.Disconnected;
            _isInitialized = false;
            _isDisconnecting = false;

            // Deferral goes back to its resting state with the session. Both halves — the parked
            // commands and the "an operation owns the device" flag — or the next session defers
            // into a backlog nobody will drain and loses messages silently.
            _operations.ResetDeferralState();
            if (lockAcquired)
            {
                _operations.ReleaseAfterTeardown();
            }
        }

        /// <summary>
        /// Sends a message to the device.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Fire-and-forget, and safe to call from any thread: the message is handed to a background
        /// queue and this returns before the write happens, so delivery is not guaranteed. See
        /// <see cref="SendFailed"/> for the only signal that a specific message was not delivered.
        /// </para>
        /// <para>
        /// If another thread is inside <see cref="RunExclusiveAsync{TResult}"/> or a text query when
        /// this is called, the message is held back and sent, in order, as soon as that finishes
        /// (issue #342). This call still does not block — it returns as immediately as it always
        /// has — but the message can reach the device later than it used to. That is the point: a
        /// command written while a text query owns the stream gets its reply mixed into that
        /// query's answer.
        /// </para>
        /// <para>
        /// That held-back backlog is capped at <see cref="DefaultMaxDeferredSends"/> messages, and
        /// overflows by discarding the <b>oldest</b> of them — so sending continuously through a
        /// long exclusive operation (an SD card download may run for thirty minutes) costs bounded
        /// memory and replays a bounded burst afterwards, keeping the most recent commands rather
        /// than the most stale. Discards are counted by <see cref="DroppedDeferredSendCount"/>.
        /// Nothing is dropped while the device is idle, and nothing is dropped for a backlog under
        /// the cap.
        /// </para>
        /// </remarks>
        /// <typeparam name="T">The type of the message data payload.</typeparam>
        /// <param name="message">The message to send to the device.</param>
        /// <exception cref="DeviceNotConnectedException">
        /// Thrown when the device is not connected. Also thrown, with
        /// <see cref="DeviceNotConnectedException.IsShuttingDown"/> set, when a disconnect, dispose
        /// or auto-reconnect on another thread tears the send path down after this call has passed
        /// its connectivity guard — an ordinary race a long-lived sender should expect, not a defect.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the device is connected but has no transport or stream to send on
        /// (e.g. the producer-less <see cref="DaqifiDevice(string, IPAddress, ILogger)"/> constructor).
        /// </exception>
        public virtual void Send<T>(IOutboundMessage<T> message)
        {
            EnsureConnected();

            // Checked before the message can be parked. A null used to fail loudly at the producer;
            // deferred, it would instead fail on a background flush where the exception is logged
            // and dropped, turning a caller's bug into a silently missing command.
            ArgumentNullException.ThrowIfNull(message);

            if (_operations.TryDeferSend(message))
            {
                return;
            }

            SendNow(message);
        }

        /// <summary>
        /// Serializes writes on the producer-less path, where <see cref="Send{T}"/> writes to the
        /// stream on the caller's own thread. Two threads writing a stream concurrently is the one
        /// place SCPI bytes really can interleave mid-command; the queued path is already safe
        /// because a single producer thread does every write.
        /// </summary>
        private readonly object _directWriteGate = new();

        /// <summary>
        /// Puts a message on its way to the device immediately — the body <see cref="Send{T}"/> runs
        /// once it knows nothing else owns the device, and the body a deferred send is replayed
        /// through afterwards.
        /// </summary>
        private void SendNow<T>(IOutboundMessage<T> message)
        {
            // Snapshotted once, for the reason TextExchangeEngine.SuspendInboundConsumer snapshots
            // the consumer: the field behind it is mutable and teardown nulls it
            // (StopMessagePumps) from another thread, under no lock this path takes. Null-checking
            // the field and then dereferencing it reads it twice, and a teardown landing between
            // the two reads throws NullReferenceException out of a public API. The window is not
            // limited to a user-initiated Disconnect() — every auto-reconnect attempt goes through
            // DisconnectCore(Retrying) and nulls the field the same way (issue #497).
            var producer = _messageProducer;

            // Use the queued message producer when available and the message is string-based;
            // this is the common path (SCPI text commands).
            if (producer != null && message is IOutboundMessage<string> stringMessage)
            {
                SendViaProducer(producer, stringMessage);
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

            // The queued path gets its mutual exclusion from having exactly one writer thread; this
            // path has none, and two callers writing at once is the one case where SCPI bytes can
            // genuinely interleave mid-command. The lock is a leaf — nothing is acquired underneath
            // it — so it cannot participate in a cycle.
            lock (_directWriteGate)
            {
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        /// <summary>
        /// Hands a message to the queued producer, reporting a producer that teardown has already
        /// stopped or disposed as the same typed failure every other guard on this device reports.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Snapshotting the field closes the null-dereference half of the disconnect race; this
        /// closes the other half. Winning the snapshot only means the reference was still there —
        /// teardown can have stopped or disposed that very instance a moment later, and the send
        /// then fails inside the producer. Untranslated, callers would see a bare
        /// <see cref="InvalidOperationException"/> whose message names an internal lifecycle method
        /// ("Call Start() first"), or an <see cref="ObjectDisposedException"/> naming an internal
        /// type — both indistinguishable, without matching on message text, from a genuine
        /// application defect. That is exactly the distinction
        /// <see cref="DeviceNotConnectedException"/> was introduced to make (#395), so this reports
        /// the ordinary, expected condition it is, with
        /// <see cref="DeviceNotConnectedException.IsShuttingDown"/> set.
        /// </para>
        /// <para>
        /// The original is kept as <see cref="Exception.InnerException"/> so the race stays
        /// diagnosable, and a <see cref="DeviceNotConnectedException"/> from the producer itself is
        /// let through unwrapped — it already says what this would say.
        /// </para>
        /// <para>
        /// Internal rather than private so the translation can be tested against a producer that
        /// throws on demand. Hitting these branches through the public API means winning a race
        /// against teardown, which no test can schedule reliably; the stress test that exercises the
        /// real race can only assert the negative (no untyped exception escaped).
        /// </para>
        /// </remarks>
        internal static void SendViaProducer(IMessageProducer<string> producer, IOutboundMessage<string> message)
        {
            try
            {
                producer.Send(message);
            }
            catch (DeviceNotConnectedException)
            {
                // Already the failure this method would translate to. Rethrown unchanged so the
                // producer's own wording and IsShuttingDown value survive.
                throw;
            }
            catch (ObjectDisposedException ex)
            {
                // Checked before InvalidOperationException: ObjectDisposedException derives from it,
                // so the broader filter below would otherwise swallow this case first.
                throw new DeviceNotConnectedException(
                    "The message could not be sent because the device's message producer has been "
                    + "disposed; a disconnect or dispose completed while this send was in flight.",
                    ex,
                    isShuttingDown: true);
            }
            catch (InvalidOperationException ex)
            {
                throw new DeviceNotConnectedException(
                    "The message could not be sent because the device's message producer is no "
                    + "longer running; a disconnect or reconnect is in flight.",
                    ex,
                    isShuttingDown: true);
            }
        }

        /// <summary>
        /// Takes exclusive use of the device and hands the transport stream to <paramref name="rawAction"/>
        /// for raw byte access, with the protobuf consumer paused for the duration. Everything is
        /// restored when the action completes, however it completes.
        /// </summary>
        /// <remarks>
        /// Runs under the same operation lock as <see cref="RunExclusiveAsync{TResult}"/> and the
        /// text exchange, so a text query from another thread waits for the capture and a
        /// <see cref="Send{T}"/> from another thread is deferred and replayed afterwards (#493).
        /// Reentrant on the flow that already holds the lock. Captures can be long — an SD
        /// download's budget is 30 minutes — so pass a cancellation token if the caller cannot wait
        /// that long for the lock.
        /// </remarks>
        /// <param name="rawAction">
        /// An async function that receives the transport stream and performs raw I/O.
        /// The protobuf consumer will not read from the stream while this action is executing.
        /// </param>
        /// <param name="cancellationToken">A cancellation token to observe.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected, disconnecting or disposed.</exception>
        /// <exception cref="InvalidOperationException">Thrown when the device has no transport-based connection.</exception>
        protected virtual Task ExecuteRawCaptureAsync(
            Func<Stream, CancellationToken, Task> rawAction,
            CancellationToken cancellationToken = default)
        {
            return _textExchange.ExecuteRawCaptureAsync(rawAction, cancellationToken);
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
        /// <param name="finalizeAsync">
        /// Optional phase that undoes what <paramref name="prepareAsync"/> established — the SD card
        /// operations use it to hand the shared SPI bus back to the LAN interface.
        /// <para>
        /// It is the mirror of the prepare phase and runs under the <b>same</b> lock acquisition, so
        /// nothing can interleave between this exchange's commands and the state it restores (#407).
        /// It runs after the protobuf consumer has been restarted, mirroring the prepare phase
        /// running before the consumer was swapped out.
        /// </para>
        /// <para>
        /// It runs whether the exchange succeeds or fails, so the device is never left in the
        /// prepared state. It takes no cancellation token on purpose: it is cleanup, and a cancelled
        /// or timed-out exchange still has to put the device back. Keep it short and non-blocking —
        /// it holds the lock while it runs.
        /// </para>
        /// <para>
        /// If the exchange failed and the finalize phase then fails too, the finalize failure is
        /// logged and dropped and the exchange's original failure is what the caller sees — a
        /// cleanup failure must never hide the failure that caused the cleanup. If the exchange
        /// succeeded, a finalize failure is the only failure there is, and it propagates.
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
        /// <param name="keepBlankLines">
        /// When <c>true</c>, blank lines are returned instead of filtered out. Only the system-log
        /// read wants this: <c>SYSTem:LOG?</c> terminates its dump with a blank line
        /// unconditionally, so seeing it is what separates "the device answered and its log is
        /// empty" from "the device never answered" (issue #543). Every other caller parses content
        /// and has never seen a blank line, so the default is <c>false</c>.
        /// </param>
        // prepareAsync and finalizeAsync are added AFTER cancellationToken (technically violating
        // CA1068 "CancellationToken should be last") to keep existing positional callers working,
        // matching the convention established in IFirmwareUpdateService for the same reason. They
        // are parameters on this seam rather than separate virtual methods deliberately: a parallel
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
            Func<CancellationToken, Task>? prepareAsync = null,
            Func<Task>? finalizeAsync = null,
            bool keepBlankLines = false)
        {
            return ExecuteTextCommandCoreAsync(
                prepareAsync,
                finalizeAsync,
                _ => { setupAction(); return Task.CompletedTask; },
                responseTimeoutMs,
                completionTimeoutMs,
                cancellationToken,
                keepBlankLines);
        }
#pragma warning restore CA1068

        /// <summary>
        /// Async overload of <see cref="ExecuteTextCommandAsync(Action, int, int, CancellationToken, Func{CancellationToken, Task}, Func{Task}, bool)"/>
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
                finalizeAsync: null,
                setupActionAsync,
                responseTimeoutMs,
                completionTimeoutMs,
                cancellationToken);
        }

        private Task<IReadOnlyList<string>> ExecuteTextCommandCoreAsync(
            Func<CancellationToken, Task>? prepareAsync,
            Func<Task>? finalizeAsync,
            Func<CancellationToken, Task> setupActionAsync,
            int responseTimeoutMs,
            int completionTimeoutMs,
            CancellationToken cancellationToken,
            bool keepBlankLines = false)
        {
            return _textExchange.ExecuteAsync(
                prepareAsync,
                finalizeAsync,
                setupActionAsync,
                responseTimeoutMs,
                completionTimeoutMs,
                cancellationToken,
                keepBlankLines);
        }

        /// <summary>
        /// The text (SCPI) exchange primitive and the raw-capture swap it shares its consumer
        /// handling with, extracted so this class delegates rather than hosts them (issue #344).
        /// </summary>
        private readonly TextExchangeEngine _textExchange;

        #region ITextExchangeHost — the engine's view of this device

        /// <inheritdoc />
        bool ITextExchangeHost.IsConnected => IsConnected;

        /// <inheritdoc />
        bool ITextExchangeHost.IsShuttingDown => _disposed || _isDisconnecting;

        /// <inheritdoc />
        IStreamTransport? ITextExchangeHost.Transport => _transport;

        /// <inheritdoc />
        IMessageConsumer<DaqifiOutMessage>? ITextExchangeHost.MessageConsumer => _messageConsumer;

        /// <inheritdoc />
        void ITextExchangeHost.AttachInboundHandler(IMessageConsumer<DaqifiOutMessage> consumer) =>
            AttachInboundMessages(consumer);

        /// <inheritdoc />
        void ITextExchangeHost.DetachInboundHandler(IMessageConsumer<DaqifiOutMessage> consumer) =>
            DetachInboundMessages(consumer);

        /// <inheritdoc />
        bool ITextExchangeHost.HoldsOperationLock => _operations.HoldsOperationLock;

        /// <inheritdoc />
        Task ITextExchangeHost.WaitForOperationLockAsync(CancellationToken cancellationToken) =>
            _operations.WaitForOperationLockAsync(cancellationToken);

        /// <inheritdoc />
        /// <remarks>
        /// Forwarded synchronously, and must stay that way: the <see cref="AsyncLocal{T}"/> write it
        /// performs flows forward from whichever frame performs it. A plain synchronous call chain
        /// keeps it in the engine's own frame; behind an <c>await</c> it would land in a frame
        /// nobody reads and the exchange would stop recognising its own ownership.
        /// </remarks>
        void ITextExchangeHost.EnterOperationLockOwnership() => _operations.EnterOperationLockOwnership();

        /// <inheritdoc />
        /// <remarks>Synchronous for the same reason as <see cref="ITextExchangeHost.EnterOperationLockOwnership"/>.</remarks>
        void ITextExchangeHost.ExitOperationLockOwnership() => _operations.ExitOperationLockOwnership();

        /// <inheritdoc />
        /// <remarks>
        /// The setter is synchronous for the same reason as
        /// <see cref="ITextExchangeHost.EnterOperationLockOwnership"/>: the re-entrancy guard only
        /// works if the flag reaches the callbacks the engine invokes.
        /// </remarks>
        bool ITextExchangeHost.IsInsideTextExchange
        {
            get => _isInsideTextExchange.Value;
            set => _isInsideTextExchange.Value = value;
        }

        /// <inheritdoc />
        Task ITextExchangeHost.DrainOutboundQueueAsync(CancellationToken cancellationToken) =>
            _operations.DrainOutboundQueueAsync(cancellationToken);

        /// <inheritdoc />
        /// <remarks>
        /// The producer is snapshotted once, like every other read of this field off the lock:
        /// teardown nulls it from another thread, so null-checking the field and then dereferencing
        /// it would read it twice. A null answer is the honest one on a device with no producer —
        /// nothing it sends goes through one — and the engine has a branch for it.
        /// </remarks>
        OutboundWriterSample? ITextExchangeHost.SampleOutboundWriter()
        {
            var producer = _messageProducer;

            // Read first, so a write that begins between the two reads is reported as "not started
            // yet, work outstanding" rather than the reverse. That direction is the safe one: it
            // describes the instant the sample was asked for.
            var startedWrites = producer?.StartedWriteCount;

            return startedWrites.HasValue
                ? new OutboundWriterSample(startedWrites.Value, !producer!.IsIdle)
                : null;
        }

        /// <inheritdoc />
        IDisposable ITextExchangeHost.SubscribeConsumerErrors(IMessageConsumer<string> consumer) =>
            new ConsumerErrorSubscription(this, consumer);

        /// <inheritdoc />
        ILogger ITextExchangeHost.Logger => _logger;

        /// <inheritdoc />
        void ITextExchangeHost.OnStaleLineBoundaryCaptured() => OnStaleLineBoundaryCaptured();

        /// <summary>
        /// A no-op on the real device. Overridable for tests, mirroring <see cref="MaxDeferredSends"/> —
        /// see <see cref="ITextExchangeHost.OnStaleLineBoundaryCaptured"/> for why this seam exists.
        /// </summary>
        internal virtual void OnStaleLineBoundaryCaptured()
        {
        }

        /// <inheritdoc />
        void ITextExchangeHost.OnSendBoundaryCaptured() => OnSendBoundaryCaptured();

        /// <summary>
        /// A no-op on the real device. Overridable for tests, mirroring
        /// <see cref="OnStaleLineBoundaryCaptured"/> — see
        /// <see cref="ITextExchangeHost.OnSendBoundaryCaptured"/> for why this seam exists.
        /// </summary>
        internal virtual void OnSendBoundaryCaptured()
        {
        }

        /// <inheritdoc />
        void ITextExchangeHost.OnReplyWaitCompleted(bool sawResponse) => OnReplyWaitCompleted(sawResponse);

        /// <summary>
        /// A no-op on the real device. Overridable for tests, mirroring
        /// <see cref="OnSendBoundaryCaptured"/> — see
        /// <see cref="ITextExchangeHost.OnReplyWaitCompleted"/> for why this seam exists.
        /// </summary>
        internal virtual void OnReplyWaitCompleted(bool sawResponse)
        {
        }

        #endregion

        #region IOperationSerializationHost — the serializer's view of this device

        /// <inheritdoc />
        ILogger IOperationSerializationHost.Logger => _logger;

        /// <inheritdoc />
        int IOperationSerializationHost.MaxDeferredSends => MaxDeferredSends;

        /// <inheritdoc />
        TimeSpan IOperationSerializationHost.OutboundDrainWait => OutboundDrainWait;

        /// <inheritdoc />
        IMessageProducer<string>? IOperationSerializationHost.MessageProducer => _messageProducer;

        /// <inheritdoc />
        void IOperationSerializationHost.SendNow<T>(IOutboundMessage<T> message) => SendNow(message);

        #endregion

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
        /// Each iteration uses <see cref="ExecuteTextCommandAsync(Action, int, int, CancellationToken, Func{CancellationToken, Task}, Func{Task}, bool)"/>, which
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
                if (ScpiResponseClassifier.TryParseSystemErrorReplyCode(reply, out var code) && code == 0)
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
            EnsureConnected();

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

            // The document is also the only description of the device's DAC channels, so this is
            // the moment they can be modelled (#499).
            SyncAnalogOutputChannelsFromCapabilities();

            // The document is the only place the device says what its analog readings are measured
            // in, so this is where a channel learns its unit (#501). Never overwrites a scaling a
            // caller configured, which matters because this method is re-run on every capability
            // refresh.
            var unitsApplied = CapabilityChannelUnits.Apply(GetChannelsSnapshot(), document);
            if (unitsApplied > 0)
            {
                SafeLog(() => _logger.LogDebug(
                    "[ReadCapabilityDocumentAsync] Applied the document's unit to {ChannelCount} analog channel(s).",
                    unitsApplied));
            }

            return document;
        }

        /// <summary>
        /// Raises the <see cref="MessageReceived"/> event when a message is received from the device.
        /// </summary>
        /// <remarks>
        /// Not called for a device frame while <see cref="MessageReceived"/> has no subscribers: the
        /// frame would have to be wrapped in an <see cref="IInboundMessage{T}"/> to be passed here,
        /// and on a streaming device that is an allocation per frame with nowhere to go
        /// (issue #490). An override that must see every frame regardless should use
        /// <see cref="StatusMessageReceived"/>/<see cref="StreamMessageReceived"/> or override
        /// <see cref="OnStatusMessageReceived"/>/<see cref="OnStreamMessageReceived"/>, which are
        /// unconditional.
        /// </remarks>
        /// <param name="message">The message received from the device.</param>
        protected virtual void OnMessageReceived(IInboundMessage<object> message)
        {
            MessageReceived?.Invoke(this, new MessageReceivedEventArgs(message));
        }

        /// <summary>
        /// Wraps <paramref name="message"/> and hands it to <see cref="OnMessageReceived"/>, but
        /// only when <see cref="MessageReceived"/> actually has a subscriber to receive it.
        /// </summary>
        /// <param name="message">The device frame to re-raise.</param>
        private void RaiseUndifferentiatedMessage(DaqifiOutMessage message)
        {
            if (MessageReceived is null)
            {
                return;
            }

            OnMessageReceived(new ProtobufMessage(message));
        }

        /// <summary>
        /// Handles transport status changes and updates device connection status accordingly.
        /// </summary>
        /// <param name="sender">The transport that raised the event.</param>
        /// <param name="e">The transport status event arguments.</param>
        private void OnTransportStatusChanged(object? sender, TransportStatusEventArgs e)
        {
            // Transport connected: device status on the way up is managed by Connect(), so there
            // is nothing to do here.
            if (e.IsConnected)
            {
                return;
            }

            // Transport disconnected — only report Lost for unexpected drops,
            // not during an intentional Disconnect() call
            if (Status != ConnectionStatus.Connected || _isDisconnecting)
            {
                return;
            }

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
        /// <remarks>
        /// Disconnects first, which blocks the calling thread. <see cref="DisposeAsync"/> is the
        /// non-blocking equivalent — prefer <c>await using</c> over <c>using</c> on a UI thread.
        /// </remarks>
        public void Dispose()
        {
            if (!TryClaimDisposal())
            {
                return;
            }

            try
            {
                Disconnect();
            }
            finally
            {
                ReleaseResources();
            }
        }

        /// <summary>
        /// Disposes the device and releases resources without blocking the calling thread, so
        /// <c>await using var device = ...</c> is safe on a UI thread.
        /// </summary>
        /// <remarks>
        /// Equivalent to <see cref="Dispose"/> except that the disconnect it performs first runs
        /// through <see cref="DisconnectAsync"/>. Safe to call more than once, and safe to mix with
        /// <see cref="Dispose"/> — whichever runs first wins and the other becomes a no-op.
        /// </remarks>
        /// <returns>A task representing the asynchronous dispose operation.</returns>
        public async ValueTask DisposeAsync()
        {
            if (!TryClaimDisposal())
            {
                return;
            }

            try
            {
                await DisconnectAsync().ConfigureAwait(false);
            }
            finally
            {
                ReleaseResources();
            }
        }

        /// <summary>
        /// Claims the right to tear this device down, atomically and exactly once.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The gate is taken at the <i>start</i> of disposal rather than published at the end,
        /// because <see cref="DisposeAsync"/> spends real time awaiting
        /// <see cref="DisconnectAsync"/>. A plain "have we finished disposing?" flag leaves that
        /// entire window open, so a concurrent <see cref="Dispose"/> — an <c>await using</c> scope
        /// unwinding while another shutdown path fires, say — would sail through the check and run
        /// a second teardown, disposing the transport and the text-exchange semaphore twice.
        /// </para>
        /// <para>
        /// The loser returns immediately rather than waiting for the winner to finish. That is the
        /// normal contract for a redundant <see cref="IDisposable.Dispose"/> call, and the
        /// alternative — having <see cref="Dispose"/> block on the in-flight
        /// <see cref="DisposeAsync"/> — would reintroduce on the calling thread exactly the stall
        /// this class now exists to avoid.
        /// </para>
        /// </remarks>
        /// <returns><c>true</c> for the first caller only.</returns>
        private bool TryClaimDisposal() => Interlocked.Exchange(ref _disposeClaimed, 1) == 0;

        /// <summary>
        /// Releases everything the device owns once it is already disconnected. Shared tail of
        /// <see cref="Dispose"/> and <see cref="DisposeAsync"/>.
        /// </summary>
        /// <remarks>
        /// The transport is already closed by the preceding disconnect, so
        /// <see cref="IDisposable.Dispose"/> on it does not block here. Runs from a
        /// <c>finally</c> so that a disconnect which throws — a serial close can — still releases
        /// the handles rather than leaking them on a device that can never be disposed again.
        /// </remarks>
        private void ReleaseResources()
        {
            try
            {
                ReleaseDerivedResources();
            }
            catch (Exception ex)
            {
                // Disposal must not fail on account of the library's own cleanup — the handles
                // released below are what a caller is actually relying on this for, and a throwing
                // Dispose would hide them behind an exception nobody can act on. Reported rather
                // than swallowed.
                RaiseDeviceError(DeviceErrorSource.Unknown, ex);
            }
            finally
            {
                _messageConsumer?.Dispose();
                _messageProducer?.Dispose();
                _transport?.Dispose();
                _operations.Dispose();
                _disposed = true;
            }
        }

        /// <summary>
        /// Releases what a derived device inside this library owns, ahead of the handles this class
        /// owns.
        /// </summary>
        /// <remarks>
        /// The companion to <see cref="OnConnectionStatusChanged"/>, and internal for the same
        /// reason. It covers the one teardown a status transition cannot: disposing a device that
        /// was never connected, or was already disconnected, moves it nowhere, so nothing would tell
        /// a live-sample enumeration parked on it that the device is gone (issue #496). An override
        /// that throws is caught and reported on <see cref="ErrorOccurred"/>: the handles this method
        /// exists to release are freed regardless, and <see cref="Dispose()"/> does not fail.
        /// </remarks>
        internal virtual void ReleaseDerivedResources()
        {
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
        /// <b>Steps 2-4 write global device state.</b> Streaming is a single global state on a DAQiFi
        /// device, so step 2 stops a stream <em>any</em> session started, not just this one:
        /// connecting to a device another session is already streaming silently ends that session's
        /// acquisition. That is the right default when this session owns the device (it also clears a
        /// stream orphaned by a crashed session). Set <see cref="PreserveActiveStream"/> before
        /// calling this to skip steps 2-4 and connect as a non-disruptive observer instead.
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
                    DetachInboundMessages(_messageConsumer);
                    AttachInboundMessages(_messageConsumer);
                }

                // Snapshot into a local, once. Every retry attempt and the derived-class hook
                // further down are then handed the same decision explicitly, so it cannot be
                // changed out from under an initialization already in flight — neither by a caller
                // mutating the property nor by a second concurrent InitializeAsync on this same
                // instance. The decision belongs to this operation, so it lives on the stack rather
                // than in a field.
                var preserveActiveStream = PreserveActiveStream;

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
                        // Echo is a per-device text-mode setting, not stream state: this session
                        // needs it off to parse its own replies, and the value is the same one any
                        // other Core session already set. Safe to send either way.
                        Send(ScpiMessageProducer.DisableDeviceEcho);

                        // Everything below writes global stream state. A secondary "observe"
                        // session must not touch it — StopStreamData ends another session's
                        // acquisition outright (#385), and the power-state and stream-format
                        // commands reconfigure the same single acquisition it is running.
                        if (preserveActiveStream)
                        {
                            return;
                        }

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
                await OnDeviceInitializingAsync(preserveActiveStream, cancellationToken).ConfigureAwait(false);

                // A cancelled initialization must never report Ready. Nothing above is guaranteed
                // to observe the token: the capability read returns early on firmware that does not
                // advertise the document, channel population can short-circuit when the status
                // arrives synchronously, and a derived hook may legitimately have no awaitable work
                // (the observing path and the non-USB path both return immediately). So the
                // invariant is enforced here, at the one transition that matters, rather than relying
                // on every path and every override to check for itself.
                cancellationToken.ThrowIfCancellationRequested();

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
        /// <param name="preserveActiveStream">
        /// The <see cref="PreserveActiveStream"/> decision for <em>this</em> initialization, passed
        /// explicitly rather than read from the device so it cannot change while initialization is in
        /// flight. When <c>true</c>, an override must not send any command that writes global stream
        /// state — stopping, reconfiguring, or re-routing the stream would disturb a session that is
        /// already using the device.
        /// </param>
        /// <param name="cancellationToken">A cancellation token to observe.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        protected virtual Task OnDeviceInitializingAsync(
            bool preserveActiveStream,
            CancellationToken cancellationToken) => Task.CompletedTask;

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
        /// Asks the device for a fresh status message and waits for it to arrive, updating
        /// <see cref="Metadata"/> — including <see cref="DeviceMetadata.Health"/> — from the reply.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Health telemetry is <b>not</b> live. The device sends a status message when asked and at
        /// no other time: a connected link carries stream frames only (the firmware builds each one
        /// from four tags, none of them health), so without this call the values captured during
        /// <c>InitializeAsync</c> stay frozen for the life of the connection. Measured on an Nq1
        /// running 3.7.2: 1,587 inbound messages over 65 s, of which exactly one carried health —
        /// the one the harness explicitly asked for (issue #535).
        /// </para>
        /// <para>
        /// Cadence is deliberately the caller's, not Core's. A self-poll inside the library would
        /// contend for the operation lock against streaming and SD work, so how often "current"
        /// needs to be is a decision only the application can make.
        /// </para>
        /// <para>
        /// <b>Known limitation.</b> A status frame carries nothing saying which request it answers,
        /// so if one refresh times out and its reply arrives late, the NEXT refresh can be
        /// completed by that older frame rather than its own. The value is still a genuine device
        /// reading — just one taken before this call, bounded by the previous refresh's timeout —
        /// and this call's own reply lands moments later and updates
        /// <see cref="DeviceMetadata.Health"/> again.
        /// </para>
        /// <para>
        /// It is documented rather than worked around because the protocol offers no correlation
        /// to work around it with. The obvious heuristic — skip one frame after a timeout — makes
        /// the more common case worse: if the earlier reply never arrives at all, the next refresh
        /// would discard its OWN reply and time out too. Concurrent refreshes, which had the same
        /// shape for a different reason, are prevented outright by serializing them.
        /// </para>
        /// </remarks>
        /// <param name="timeout">
        /// How long to wait, in total. Defaults to 5 seconds when omitted, so a silent device
        /// surfaces as a <see cref="TimeoutException"/> rather than hanging the caller.
        /// <para>
        /// The deadline covers the whole operation, not just the device round-trip: waiting behind
        /// another refresh, and waiting for the request itself to be sent. <see cref="Send"/> is
        /// fire-and-forget and is <b>deferred</b> while an exclusive operation holds the device
        /// (an SD download can hold it for many minutes), so a short timeout taken during one can
        /// elapse before the device is ever asked. That is a real outcome, and the exception says
        /// only that no status arrived — pass a timeout that suits what else the device is doing.
        /// </para>
        /// </param>
        /// <param name="cancellationToken">A cancellation token to observe.</param>
        /// <returns>A task that completes once a status message has been received and applied.</returns>
        /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
        /// <exception cref="TimeoutException">Thrown when no status message arrives in time.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
        public async Task RefreshDeviceStatusAsync(
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            EnsureConnected(cancellationToken);

            var wait = timeout ?? DefaultStatusRefreshTimeout;
            if (wait <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timeout), wait, "Timeout must be positive.");
            }

            // One deadline covers the whole operation: waiting for the gate, waiting for the send
            // to leave the queue, and waiting for the reply. See the timeout parameter's note.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(wait);

            try
            {
                await _statusRefreshGate.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Timed out after {wait.TotalSeconds:0.##}s waiting for an earlier status "
                    + "refresh on this device to finish.");
            }

            try
            {
                var statusTcs = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                void OnStatus(DaqifiOutMessage _) => statusTcs.TrySetResult(true);

                // Subscribed before the send, so a device that answers immediately cannot slip the
                // reply through the gap between Send() and the subscription -- the same ordering
                // WaitForChannelsPopulatedAsync relies on.
                //
                // Metadata is updated by OnStatusMessageReceived BEFORE this event is raised, so by
                // the time the wait completes the caller can read Metadata.Health and see the reply.
                _statusRefreshWaiter = OnStatus;
                try
                {
                    Send(ScpiMessageProducer.GetDeviceInfo);

                    try
                    {
                        await statusTcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // A reply landing in the same instant the deadline fires can cancel the
                        // wait even though the status DID arrive and was applied. Reporting that
                        // as a timeout would tell the caller the refresh failed while the data
                        // they asked for is sitting in Metadata.
                        if (statusTcs.Task.IsCompletedSuccessfully)
                        {
                            return;
                        }

                        throw new TimeoutException(
                            $"The device did not return a status message within {wait.TotalSeconds:0.##}s.");
                    }
                }
                finally
                {
                    _statusRefreshWaiter = null;
                }
            }
            finally
            {
                _statusRefreshGate.Release();
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

            // The refresh waiter runs BEFORE any consumer subscriber and outside the multicast
            // event, so no third party can delay or prevent a caller of RefreshDeviceStatusAsync
            // from seeing the reply it asked for. Its own faults are contained here for the same
            // reason the classified event contains a subscriber's.
            var waiter = _statusRefreshWaiter;
            if (waiter != null)
            {
                try
                {
                    waiter(message);
                }
                catch (Exception ex)
                {
                    SafeLog(() => _logger.LogWarning(ex, "[RefreshDeviceStatus] waiter threw"));
                }
            }

            // Raise the classified event first so consumers that only care about status
            // messages can react before the undifferentiated MessageReceived below. A
            // misbehaving subscriber must not prevent MessageReceived from firing for this
            // frame — the consumer loop that calls in here does not retry a failed frame,
            // so an uncaught exception here would silently drop it for every other consumer.
            RaiseClassifiedEvent(StatusMessageReceived, message, nameof(StatusMessageReceived));

            // Raise event for external consumers
            RaiseUndifferentiatedMessage(message);
        }

        /// <summary>
        /// Invokes a classified message event, isolating each subscriber from the others' exceptions.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="OnStatusMessageReceived"/> and <see cref="OnStreamMessageReceived"/> still have
        /// work to do after raising their classified event (the undifferentiated <see cref="MessageReceived"/>
        /// event, and for <see cref="DaqifiStreamingDevice"/> the per-channel sample decode) — an exception
        /// escaping a classified-event subscriber must not skip that remaining work for the frame.
        /// </para>
        /// <para>
        /// A plain <c>try { handler(message); }</c> around the whole multicast delegate only isolates
        /// the device from the subscriber list as a whole — .NET invokes a multicast delegate's
        /// invocation list in order and stops at the first exception, so a throwing subscriber
        /// silently starves every subscriber added after it for the life of the connection (issue #560).
        /// Walking <see cref="Delegate.GetInvocationList"/> and wrapping each entry isolates subscribers
        /// from each other too — strictly more robust, since it can only run more handlers, never fewer.
        /// </para>
        /// <para>
        /// This is on the per-frame path, and <see cref="Delegate.GetInvocationList"/> allocates an
        /// array per call regardless of subscriber count — at 100 Hz with several channels that is a
        /// real allocation rate. Measured against the by-far-common case of a single subscriber (see
        /// <c>DeviceStatusChangedIsolationTests</c>-style allocation tests elsewhere in this repo, e.g.
        /// <c>AcquisitionStatisticsTests</c>), it costs one small array per classified frame. That is
        /// accepted here in exchange for correctness: a cached invocation list refreshed on subscribe/
        /// unsubscribe would avoid it, but adds real complexity for a cost that has not shown up as a
        /// problem in practice. Revisit if profiling ever says otherwise.
        /// </para>
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

            foreach (var subscriber in handler.GetInvocationList())
            {
                InvokeClassifiedSubscriber((Action<DaqifiOutMessage>)subscriber, message, eventName);
            }
        }

        /// <summary>
        /// Invokes a single classified-event subscriber, containing any exception it throws so it
        /// cannot stop sibling subscribers (or the device) from seeing the frame.
        /// </summary>
        /// <param name="subscriber">The single subscriber delegate to invoke.</param>
        /// <param name="message">The message to pass to the subscriber.</param>
        /// <param name="eventName">The event name, for the trace log if the subscriber throws.</param>
        private void InvokeClassifiedSubscriber(Action<DaqifiOutMessage> subscriber, DaqifiOutMessage message, string eventName)
        {
            try
            {
                subscriber(message);
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

            int analogCount;
            int digitalCount;
            IChannel[] channelsSnapshot;

            // Repopulate under the channels lock so a caller folding over a snapshot on
            // another thread (the device-level channel-management API) never observes a
            // half-cleared or torn list. The mapping itself runs inside the lock exactly as it
            // did before the extraction — it reads the current channels to reuse them in place.
            lock (_channelsLock)
            {
                var updatedChannels = new List<IChannel>();

                (analogCount, digitalCount) = _channelPopulator.Populate(message, _channels, updatedChannels);

                // Analog-output (DAC) channels are described only by the capability document —
                // the protobuf declares analog_out_port_num/_res/_range but the firmware never
                // fills them in — so a status message says nothing about them and must not be read
                // as "this device has none". Carry the modelled ones across untouched, in the same
                // order, so the membership comparison below still sees no change.
                //
                // Their lifetime is the document's: metadata is updated from this same message
                // before we get here, so a reconnect that lands this instance on a *different*
                // known board has already dropped the document it read from the previous one, and
                // the channels it described go with it rather than lingering as another board's
                // DACs.
                if (Metadata.CapabilityDocument is not null)
                {
                    CarryForwardAnalogOutputChannels(_channels, updatedChannels);
                }

                // Only a change of *membership* needs handling here. A status that re-asserts a
                // different enabled mask on channels the device already had has moved the version
                // already, through those channels' own notifications — they were still subscribed
                // while Populate ran, which is why the comparison and the swap happen after it.
                //
                // Compared by reference rather than by (type, number): the populator builds a fresh
                // instance whenever it cannot reuse one, and a cache holding the instance it
                // replaced would go on delivering samples to a channel the device no longer has.
                // Identical by reference and in order means the list it just built is the list the
                // device already holds, so there is nothing to swap, re-subscribe or invalidate.
                if (MembershipChanged(_channels, updatedChannels))
                {
                    foreach (var channel in _channels)
                    {
                        if (channel is IChannelEnablementNotifier notifier)
                        {
                            notifier.EnablementChanged -= OnChannelEnablementChanged;
                        }
                    }

                    _channels.Clear();
                    _channels.AddRange(updatedChannels);

                    foreach (var channel in _channels)
                    {
                        if (channel is IChannelEnablementNotifier notifier)
                        {
                            notifier.EnablementChanged += OnChannelEnablementChanged;
                        }
                    }

                    // No per-channel notification covers the set itself changing.
                    Interlocked.Increment(ref _channelStateVersion);
                }

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
        /// Appends the analog-output channels already modelled on this device to a freshly-built
        /// channel list, preserving their instances and their order.
        /// </summary>
        private static void CarryForwardAnalogOutputChannels(
            IReadOnlyList<IChannel> existing, List<IChannel> destination)
        {
            foreach (var channel in existing)
            {
                if (channel.Type == ChannelType.AnalogOutput)
                {
                    destination.Add(channel);
                }
            }
        }

        /// <summary>
        /// Brings the device's analog-output (DAC) channels into line with the capability document
        /// the device most recently reported, so they appear in <see cref="GetChannelsSnapshot"/>
        /// alongside the input channels.
        /// </summary>
        /// <remarks>
        /// The capability document is the <b>only</b> place the firmware describes its DAC
        /// channels: the protobuf status message declares
        /// <c>analog_out_port_num</c>/<c>_res</c>/<c>_range</c> but never populates them, which is
        /// why analog outputs are not built in <see cref="PopulateChannelsFromStatus"/> with
        /// everything else. <see cref="ReadCapabilityDocumentAsync"/> calls this for you; call it
        /// yourself only if you applied a document by hand through
        /// <see cref="DeviceMetadata.ApplyCapabilityDocument"/>. With no document in hand, or on a
        /// device whose document lists no analog outputs, the channel collection is left alone.
        /// Existing instances are refreshed in place so a consumer-held reference survives a
        /// re-read.
        /// </remarks>
        /// <returns>The number of analog-output channels the device now has.</returns>
        public int SyncAnalogOutputChannelsFromCapabilities()
        {
            var document = Metadata.CapabilityDocument;
            if (document is null || document.Channels.Count == 0)
            {
                return 0;
            }

            IChannel[]? channelsSnapshot = null;
            var analogCount = 0;
            var digitalCount = 0;
            int outputCount;

            lock (_channelsLock)
            {
                var existingOutputs = new Dictionary<int, AnalogOutputChannel>();
                var retained = new List<IChannel>(_channels.Count);

                foreach (var channel in _channels)
                {
                    if (channel is AnalogOutputChannel output)
                    {
                        existingOutputs[output.ChannelNumber] = output;
                    }
                    else
                    {
                        retained.Add(channel);

                        if (channel.Type == ChannelType.Analog)
                        {
                            analogCount++;
                        }
                        else if (channel.Type == ChannelType.Digital)
                        {
                            digitalCount++;
                        }
                    }
                }

                var rebuiltOutputs = new List<IChannel>();
                var seenNumbers = new HashSet<int>();

                foreach (var descriptor in document.Channels)
                {
                    if (descriptor.Kind != CapabilityChannelKind.AnalogOutput)
                    {
                        continue;
                    }

                    if (descriptor.Id < 0)
                    {
                        var negativeId = descriptor.Id;
                        SafeLog(() => _logger.LogWarning(
                            "[SyncAnalogOutputChannels] Device '{DeviceName}' described an analog output with a negative id ({ChannelId}); ignoring it.",
                            Name, negativeId));
                        continue;
                    }

                    if (!seenNumbers.Add(descriptor.Id))
                    {
                        var duplicateId = descriptor.Id;
                        SafeLog(() => _logger.LogWarning(
                            "[SyncAnalogOutputChannels] Device '{DeviceName}' described analog output {ChannelId} more than once; keeping the first description.",
                            Name, duplicateId));
                        continue;
                    }

                    var (resolutionBits, minimumVoltage, maximumVoltage, rangeIsAssumed) =
                        ResolveAnalogOutputDescriptor(descriptor);

                    if (existingOutputs.TryGetValue(descriptor.Id, out var existing))
                    {
                        existing.UpdateFromCapabilities(resolutionBits, minimumVoltage, maximumVoltage, rangeIsAssumed);
                        rebuiltOutputs.Add(existing);
                    }
                    else
                    {
                        rebuiltOutputs.Add(new AnalogOutputChannel(
                            descriptor.Id, resolutionBits, minimumVoltage, maximumVoltage, rangeIsAssumed));
                    }
                }

                outputCount = rebuiltOutputs.Count;

                // Only disturb the collection when the set of output channels actually changed; a
                // re-read describing the same DACs updates them in place and raises nothing.
                var compositionChanged =
                    outputCount != existingOutputs.Count ||
                    !seenNumbers.SetEquals(existingOutputs.Keys);

                if (compositionChanged)
                {
                    retained.AddRange(rebuiltOutputs);
                    _channels.Clear();
                    _channels.AddRange(retained);
                    channelsSnapshot = _channels.ToArray();

                    // An analog output carries no enablement notification of its own, so the set
                    // changing is the only thing a cache keyed on the channel list can observe.
                    Interlocked.Increment(ref _channelStateVersion);
                }
            }

            if (channelsSnapshot is not null)
            {
                // Outside the lock: a handler calling back into a channel method takes the same
                // lock. Mirrors PopulateChannelsFromStatus.
                ChannelsPopulated?.Invoke(this, new ChannelsPopulatedEventArgs(
                    Array.AsReadOnly(channelsSnapshot), analogCount, digitalCount));
            }

            return outputCount;
        }

        /// <summary>
        /// Derives the resolution and voltage range to model an analog-output channel with, falling
        /// back to <see cref="AnalogOutputChannel"/>'s NQ3 defaults for anything the document does
        /// not state or states implausibly.
        /// </summary>
        private (int ResolutionBits, double MinimumVoltage, double MaximumVoltage, bool RangeIsAssumed)
            ResolveAnalogOutputDescriptor(CapabilityChannel descriptor)
        {
            var resolutionBits = descriptor.ResolutionBits ?? AnalogOutputChannel.DefaultResolutionBits;

            if (resolutionBits is < AnalogOutputChannel.MinResolutionBits or > AnalogOutputChannel.MaxResolutionBits)
            {
                var stated = resolutionBits;
                SafeLog(() => _logger.LogWarning(
                    "[SyncAnalogOutputChannels] Device '{DeviceName}' reported an implausible DAC resolution ({Resolution} bits) for analog output {ChannelId}; assuming {AssumedResolution}.",
                    Name, stated, descriptor.Id, AnalogOutputChannel.DefaultResolutionBits));
                resolutionBits = AnalogOutputChannel.DefaultResolutionBits;
            }

            var minimum = descriptor.RangeMinimum;
            var maximum = descriptor.RangeMaximum;

            var statedRangeIsUsable =
                minimum.HasValue && maximum.HasValue &&
                double.IsFinite(minimum.Value) && double.IsFinite(maximum.Value) &&
                Math.Abs(minimum.Value) <= AnalogOutputChannel.MaxRangeMagnitudeVolts &&
                Math.Abs(maximum.Value) <= AnalogOutputChannel.MaxRangeMagnitudeVolts &&
                minimum.Value < maximum.Value;

            if (statedRangeIsUsable)
            {
                return (resolutionBits, minimum!.Value, maximum!.Value, false);
            }

            if (minimum.HasValue || maximum.HasValue)
            {
                SafeLog(() => _logger.LogWarning(
                    "[SyncAnalogOutputChannels] Device '{DeviceName}' reported an unusable output range ({Minimum} to {Maximum}) for analog output {ChannelId}; assuming {AssumedMinimum} to {AssumedMaximum} V.",
                    Name, minimum, maximum, descriptor.Id,
                    AnalogOutputChannel.DefaultMinimumVoltage, AnalogOutputChannel.DefaultMaximumVoltage));
            }

            return (
                resolutionBits,
                AnalogOutputChannel.DefaultMinimumVoltage,
                AnalogOutputChannel.DefaultMaximumVoltage,
                true);
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
            RaiseUndifferentiatedMessage(message);
        }

        /// <summary>
        /// Subscribes this device's inbound routing to <paramref name="consumer"/>.
        /// </summary>
        /// <remarks>
        /// Prefers <see cref="StreamMessageConsumer{T}.MessageParsed"/>, which carries the parsed
        /// message and nothing else. The public <c>MessageReceived</c> event additionally carries a
        /// snapshot of everything buffered at the time of the read, and taking that snapshot costs
        /// a copy of the wire throughput on every read of a streaming device (issue #490) — a cost
        /// this device has never had a use for. Any other consumer implementation falls back to the
        /// interface event, which behaves exactly as before.
        /// </remarks>
        /// <param name="consumer">The consumer to subscribe to.</param>
        private void AttachInboundMessages(IMessageConsumer<DaqifiOutMessage> consumer)
        {
            if (consumer is StreamMessageConsumer<DaqifiOutMessage> streamConsumer)
            {
                streamConsumer.MessageParsed += OnInboundMessageParsed;
                return;
            }

            consumer.MessageReceived += OnInboundMessageReceived;
        }

        /// <summary>
        /// Reverses <see cref="AttachInboundMessages"/>. Both events are detached rather than only
        /// the one that would have been attached, so a consumer that somehow carries both
        /// subscriptions cannot be left half-subscribed.
        /// </summary>
        /// <param name="consumer">The consumer to unsubscribe from.</param>
        private void DetachInboundMessages(IMessageConsumer<DaqifiOutMessage> consumer)
        {
            if (consumer is StreamMessageConsumer<DaqifiOutMessage> streamConsumer)
            {
                streamConsumer.MessageParsed -= OnInboundMessageParsed;
            }

            consumer.MessageReceived -= OnInboundMessageReceived;
        }

        /// <summary>
        /// Handles inbound messages from the message consumer and routes them through the protocol handler.
        /// </summary>
        /// <param name="sender">The message consumer that raised the event.</param>
        /// <param name="e">The message received event arguments.</param>
        private void OnInboundMessageReceived(object? sender, MessageReceivedEventArgs<DaqifiOutMessage> e)
            => OnInboundMessageParsed(e.Message);

        /// <summary>
        /// Routes a parsed inbound message through the protocol handler.
        /// </summary>
        /// <remarks>
        /// The consumer is typed to <see cref="DaqifiOutMessage"/>, so the handler's type test can
        /// never fail here — yet satisfying <see cref="IProtocolHandler.CanHandle"/> used to mean
        /// boxing every single frame into a <c>GenericInboundMessage&lt;object&gt;</c> just to ask.
        /// The typed entry point skips both the wrapper and the question (issue #490); a custom
        /// <see cref="IProtocolHandler"/> still goes the long way round.
        /// </remarks>
        /// <param name="message">The parsed message.</param>
        private void OnInboundMessageParsed(IInboundMessage<DaqifiOutMessage> message)
        {
            if (_protocolHandler is ProtobufProtocolHandler protobufHandler)
            {
                protobufHandler.Handle(message.Data);
                return;
            }

            if (_protocolHandler == null)
            {
                return;
            }

            // Convert to generic inbound message and route through protocol handler
            var genericMessage = new GenericInboundMessage<object>(message.Data);

            if (_protocolHandler.CanHandle(genericMessage))
            {
                // Fire and forget - we don't need to wait for the handler to complete
                _ = _protocolHandler.HandleAsync(genericMessage);
            }
        }
    }
} 
