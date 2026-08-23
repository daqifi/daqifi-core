using Daqifi.Core.Channel;
using Daqifi.Core.Communication;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device.Capabilities;
using Daqifi.Core.Device.Diagnostics;
using Daqifi.Core.Device.Internal;
using Microsoft.Extensions.Logging;
using Daqifi.Core.Device.Network;
using Daqifi.Core.Device.SdCard;
using Daqifi.Core.Firmware;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace Daqifi.Core.Device
{
    /// <summary>
    /// Represents a DAQiFi device that supports data streaming functionality.
    /// Extends the base DaqifiDevice with streaming-specific operations.
    /// </summary>
    public class DaqifiStreamingDevice : DaqifiDevice, IStreamingDevice, ILiveSampleSource, INetworkConfigurable, ISdCardOperations, ILanChipInfoProvider, IDeviceDiagnostics, IDeviceOperationHost, ISdCardOperationHost
    {
        /// <summary>
        /// Response window allowed for the USB stream-interface command sent during
        /// <see cref="OnDeviceInitializingAsync"/>.
        /// </summary>
        private const int UsbStreamInterfaceResponseTimeoutMs = 500;

        /// <summary>
        /// Raised when a stream frame was withheld from consumers because the device should not have
        /// sent it — a malformed leading frame, or one latched from the previous session.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Suppressing a bad frame is the right thing to do, but doing it invisibly is not: a
        /// consumer counting samples or reconciling against the device's own frame count needs to
        /// tell "Core dropped a bad frame" apart from "the device sent nothing". This event, and the
        /// running <see cref="DiscardedStreamFrameCount"/>, are that signal.
        /// </para>
        /// <para>
        /// A subscriber exception is caught and traced rather than propagated, so a misbehaving
        /// handler cannot disturb the frame that follows.
        /// </para>
        /// </remarks>
        public event EventHandler<StreamFrameDiscardedEventArgs>? StreamFrameDiscarded;

        /// <summary>
        /// Gets the number of stream frames withheld from consumers since the current streaming
        /// session began. A healthy stream on firmware without the device-side defects leaves it at
        /// zero; on firmware 3.7.2 it is typically one, for the malformed leading frame.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Reset whenever a streaming session begins, so it describes the current session — that
        /// includes a session started by a raw <c>SYSTem:StartStreamData</c> through
        /// <see cref="Send{T}"/>, not only by <see cref="StartStreaming"/>.
        /// </para>
        /// <para>
        /// Every drop is counted whether or not anyone is subscribed to
        /// <see cref="StreamFrameDiscarded"/>, so a consumer that subscribes after streaming has
        /// begun will see a total larger than the number of events it received. Read inside a
        /// <see cref="StreamFrameDiscarded"/> handler, the count already includes the frame being
        /// reported.
        /// </para>
        /// </remarks>
        public long DiscardedStreamFrameCount => _frameDecoder.DiscardedStreamFrameCount;

        /// <summary>
        /// Gets the number of streaming frames whose decode threw and was discarded since the
        /// current streaming session began.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Per-frame decoding is deliberately best-effort — a single malformed frame must never tear
        /// down the stream — but that isolation used to be completely silent (issue #378): a decode
        /// that failed on every frame produced zero samples and zero diagnostics, indistinguishable
        /// from a device sending nothing. This counter is the cheap always-on version of that
        /// signal; <see cref="DaqifiDevice.ErrorOccurred"/> carries the exception behind it.
        /// </para>
        /// <para>
        /// Reset by <see cref="StartStreaming"/>, so it describes the current session. A healthy
        /// stream leaves it at zero.
        /// </para>
        /// </remarks>
        public long DecodeFailureCount => _frameDecoder.DecodeFailureCount;

        /// <summary>
        /// Gets a value indicating whether the device is currently streaming data.
        /// </summary>
        public bool IsStreaming { get; private set; }

        private int _streamingFrequency;

        /// <summary>
        /// Gets or sets the streaming frequency in Hz (samples per second). The value is
        /// validated against the device's advertised maximum sampling rate
        /// (<see cref="DeviceCapabilities.MaxSamplingRate"/>) so a silently-wrong rate never
        /// reaches the hardware — consistent with the client-side guards Core already applies
        /// to PWM (#306) and channel bounds (#300).
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when the value is less than 1 or greater than the device's maximum sampling rate.
        /// </exception>
        public int StreamingFrequency
        {
            get => _streamingFrequency;
            set
            {
                // MaxSamplingRate is a mutable, unvalidated public property; sanitize the ceiling so
                // an uninitialized/invalid capabilities value (0 or negative) can't produce an
                // impossible range like "1..0" that rejects every valid frequency.
                var maxSamplingRate = Math.Max(1, Metadata.Capabilities.MaxSamplingRate);
                if (value < 1 || value > maxSamplingRate)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(StreamingFrequency),
                        value,
                        $"Streaming frequency must be between 1 and {maxSamplingRate} Hz (the device's maximum sampling rate).");
                }

                _streamingFrequency = value;
            }
        }

        /// <summary>
        /// Gets the highest streaming frequency in Hz this device can actually sustain with the
        /// channels it has enabled right now.
        /// </summary>
        /// <remarks>
        /// Deliberately separate from the <see cref="StreamingFrequency"/> setter's own validation,
        /// which stays on the absolute board ceiling: this figure moves with the channel set, and a
        /// setter that rejected against it would fail a perfectly reasonable
        /// "set the rate, then enable the channels" ordering. Call
        /// <see cref="EnforceStreamingFrequencyCap"/> after changing the channel set instead.
        /// </remarks>
        public int MaximumStreamingFrequencyHz => SampleRateCap.ComputeForDevice(this);

        /// <inheritdoc cref="IStreamingDevice.EnforceStreamingFrequencyCap" />
        public int? EnforceStreamingFrequencyCap() => SampleRateCap.EnforceOn(this);

        /// <summary>
        /// Gets a value indicating whether the device is currently logging data to the SD card.
        /// </summary>
        public bool IsLoggingToSdCard => _sdCardOperations.IsLoggingToSdCard;

        /// <summary>
        /// Gets a value indicating whether the device is connected over USB (serial transport).
        /// The SD card and the WiFi/LAN module share one SPI bus, so this decides how every SD
        /// operation prepares that bus, what a silent transport means to a file transfer, and
        /// whether SD logging can be started at all.
        /// </summary>
        public virtual bool IsUsbConnection => Transport is SerialStreamTransport;

        /// <summary>
        /// Gets the most recently retrieved list of files on the SD card.
        /// </summary>
        public IReadOnlyList<SdCardFileInfo> SdCardFiles => _sdCardOperations.SdCardFiles;

        /// <inheritdoc />
        public event EventHandler<LowSdSpaceWarningEventArgs>? LowSdSpaceWarning;

        /// <summary>
        /// Raised while streaming when the device-clock delta between two consecutive frames
        /// indicates dropped samples (a real gap in the device's stream, distinct from host-side
        /// arrival jitter). Fires once per detected gap, on the decode thread, carrying the outage
        /// duration and the timestamp of the first frame after the gap. See <see cref="TimestampGapDetector"/>.
        /// </summary>
        public event EventHandler<TimestampGapEventArgs>? GapDetected;

        /// <summary>
        /// Initializes a new instance of the <see cref="DaqifiStreamingDevice"/> class.
        /// </summary>
        /// <param name="name">The name of the device.</param>
        /// <param name="ipAddress">The IP address of the device, if known.</param>
        /// <param name="logger">Optional logger for device diagnostics; a no-op logger is used when null.</param>
        public DaqifiStreamingDevice(string name, IPAddress? ipAddress = null, ILogger? logger = null)
            : base(name, ipAddress, logger)
        {
            InitializeStreamingDevice();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DaqifiStreamingDevice"/> class with a transport.
        /// </summary>
        /// <param name="name">The name of the device.</param>
        /// <param name="transport">The transport for device communication.</param>
        /// <param name="logger">Optional logger for device diagnostics; a no-op logger is used when null.</param>
        public DaqifiStreamingDevice(string name, IStreamTransport transport, ILogger? logger = null)
            : base(name, transport, logger)
        {
            InitializeStreamingDevice();
        }

        private void InitializeStreamingDevice()
        {
            // Built here rather than in field initializers because each needs `this` as its host,
            // which a field initializer cannot reference. Every constructor routes through this
            // method, so they are always in place before the device is handed to a caller.
            _frameDecoder = new StreamFrameDecoder(this);
            _liveSampleStream = new LiveSampleStream(this);
            _channelControl = new ChannelControlOperations(this);
            _administration = new DeviceAdministrationOperations(this);
            _networkOperations = new NetworkConfigurationOperations(this);
            _sdCardOperations = new SdCardOperations(this);
            _lanChipInfoOperations = new LanChipInfoOperations(this);
            _diagnosticsOperations = new DeviceDiagnosticsOperations(this);

            StreamingFrequency = 100;

            // Clear the "already-sent" PWM frequency cache on any transition away from Connected —
            // an intentional Disconnected as well as an unexpected drop (which sets Lost, not
            // Disconnected) and the Retrying/Failed states. After any of these the device's runtime
            // PWM state is no longer trustworthy, so a reconnect on the same instance must re-send.
            // See #345.
            StatusChanged += (_, e) =>
            {
                if (e.Status != ConnectionStatus.Connected)
                {
                    _channelControl.ResetSentPwmFrequency();
                }
            };
        }

        /// <summary>
        /// For USB/serial connections, sets the streaming interface to USB so data is routed to the
        /// serial consumer rather than to a previously-configured WiFi destination. Runs as part of
        /// <see cref="DaqifiDevice.InitializeAsync"/> after the standard SCPI sequence.
        /// </summary>
        /// <remarks>
        /// Whether to route at all, and how hard to retry a transient rejection, is
        /// <see cref="UsbStreamInterfaceInitializer"/>'s decision; this hook supplies the effect —
        /// the actual command send — and the connection facts the decision needs.
        ///
        /// The send goes through <c>DaqifiDevice.ExecuteTextCommandAsync</c> so the command is sent
        /// in text mode (protobuf consumer temporarily stopped) and any SCPI error response is
        /// captured rather than garbling the protobuf stream.
        ///
        /// This runs inside the base <see cref="DaqifiDevice.InitializeAsync"/> exception handling
        /// (before the device is marked initialized/ready), so a cancellation or SCPI error here
        /// leaves the device in a consistent state and re-initializable, rather than falsely Ready.
        /// </remarks>
        /// <param name="preserveActiveStream">
        /// When <c>true</c>, this initialization must leave a stream another session is already
        /// running untouched, so the routing command is not sent at all.
        /// </param>
        /// <param name="cancellationToken">A cancellation token to observe while initializing.</param>
        /// <returns>A task representing the asynchronous initialization operation.</returns>
        /// <exception cref="ScpiInitializationErrorException">
        /// Thrown when the device returns a SCPI error while setting the stream interface to USB
        /// that persists after an internal retry. A common trigger is the firmware rejecting the
        /// command because it still has the interface set from a prior WiFi-streaming session,
        /// within the tight response window right after connect.
        /// </exception>
        protected override Task OnDeviceInitializingAsync(
            bool preserveActiveStream,
            CancellationToken cancellationToken) =>
            UsbStreamInterfaceInitializer.RouteStreamToUsbAsync(
                IsUsbConnection,
                preserveActiveStream,
                ct => ExecuteTextCommandAsync(
                    () => Send(ScpiMessageProducer.SetStreamInterface(StreamInterface.Usb)),
                    responseTimeoutMs: UsbStreamInterfaceResponseTimeoutMs,
                    cancellationToken: ct),
                cancellationToken);

        /// <summary>
        /// Starts streaming data from the device at the configured frequency.
        /// </summary>
        /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
        public void StartStreaming()
        {
            EnsureConnected();

            if (IsStreaming) return;

            BeginStreamingSession();

            IsStreaming = true;
            Send(ScpiMessageProducer.StartStreaming(StreamingFrequency));
        }

        /// <summary>
        /// Resets everything that is scoped to one streaming session, so the frames that follow are
        /// decoded against this session rather than the last one.
        /// </summary>
        /// <remarks>
        /// Shared by <see cref="StartStreaming"/> and by the tracking of a start-streaming command
        /// sent directly through <see cref="Send{T}"/>. It is one method precisely so the two cannot
        /// drift: a raw-started stream that skipped this would reconstruct timestamps from the
        /// previous session's anchor and re-use its gap detector, producing samples stamped with
        /// times that never happened — silently.
        /// </remarks>
        private void BeginStreamingSession() =>
            _frameDecoder.BeginSession(TimestampFrequency, StreamingFrequency);

        /// <summary>
        /// Stops streaming data from the device.
        /// </summary>
        /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
        public void StopStreaming()
        {
            EnsureConnected();

            if (!IsStreaming) return;

            IsStreaming = false;
            Send(ScpiMessageProducer.StopStreaming);
        }

        /// <inheritdoc cref="IStreamingDevice.StartStreamingAsync" />
        public Task StartStreamingAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartStreaming();
            return Task.CompletedTask;
        }

        /// <inheritdoc cref="IStreamingDevice.StopStreamingAsync" />
        public Task StopStreamingAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopStreaming();
            return Task.CompletedTask;
        }

        #region Session state tracking for commands sent directly (issue #379)

        /// <summary>
        /// Sends a command, and keeps this device's view of the streaming session in step with it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="Send{T}"/> is public and is a perfectly ordinary way to drive a device — the
        /// example CLI does the whole job that way — but a session driven through it used to be
        /// completely invisible to this class: <see cref="IsStreaming"/> stayed <c>false</c> while
        /// data poured in, and the enabled-channel set stayed empty. That mattered once reconnect
        /// arrived (issue #379). <see cref="SessionCommandInterpreter"/> reads the commands that
        /// define a session; this method applies what they imply, so the same state is updated that
        /// <see cref="StartStreaming"/> / <see cref="StopStreaming"/> and <see cref="EnableChannel"/>
        /// would have set.
        /// </para>
        /// <para>
        /// Only those commands are interpreted, and only after the send itself has succeeded.
        /// Everything else passes through untouched.
        /// </para>
        /// <para>
        /// <b>What equivalence with the typed API covers.</b> Each typed method was walked and every
        /// effect beyond setting a flag accounted for, so this is a stated scope rather than a
        /// hopeful one:
        /// </para>
        /// <list type="bullet">
        /// <item><description>
        /// <see cref="StartStreaming"/> — the whole per-session reset (timestamp anchor and tick
        /// frequency, gap detector, warmup guard and its counter, decode-failure count) is shared
        /// through <see cref="BeginStreamingSession"/> and runs here too. Skipping it was a real
        /// defect: frames decoded against a previous session's anchor carry times that never
        /// happened.
        /// </description></item>
        /// <item><description>
        /// <see cref="StopStreaming"/> — does nothing beyond clearing the flag, so clearing it here
        /// is complete.
        /// </description></item>
        /// <item><description>
        /// <see cref="EnableChannels"/> — assigns <see cref="IChannel.IsEnabled"/> under the
        /// channels lock and derives the outbound mask from it. The mask has already been sent by
        /// the time this runs, so only the assignment is replayed, and it is applied to every
        /// analog channel because the firmware treats the mask as a set-replace.
        /// </description></item>
        /// </list>
        /// <para>
        /// Running after the send is safe rather than merely convenient. A string command is handed
        /// to the background producer, so it has not reached the wire when this runs, and the device
        /// cannot answer a command it has not received; on the producer-less path that writes
        /// synchronously there is no message consumer decoding frames at all. Either way no frame
        /// can be decoded between the send and the state it should be decoded against.
        /// </para>
        /// </remarks>
        /// <typeparam name="T">The type of the message data payload.</typeparam>
        /// <param name="message">The message to send.</param>
        public override void Send<T>(IOutboundMessage<T> message)
        {
            base.Send(message);

            // Only after the send has actually gone through: a command that threw never reached
            // the device and must not move this device's idea of the session.
            if (message is IOutboundMessage<string> textCommand)
            {
                TrackSessionCommand(textCommand.Data);
            }
        }

        /// <summary>
        /// Updates the streaming-session view from a command that has just been sent.
        /// </summary>
        /// <remarks>
        /// The sampling ceiling is read exactly once and handed to the interpreter, which validates
        /// the rate against it; the value that comes back is then assigned to the backing field
        /// rather than through the validating <see cref="StreamingFrequency"/> setter. That setter
        /// re-reads <see cref="DeviceCapabilities.MaxSamplingRate"/>, which is a mutable public
        /// property — so validating against one read and assigning through a setter that takes
        /// another would let a concurrent capabilities update throw out of a <see cref="Send{T}"/>
        /// whose command has already gone to the device. Tracking a command must never be able to
        /// fail the send that carried it.
        /// </remarks>
        private void TrackSessionCommand(string? command)
        {
            var effect = SessionCommandInterpreter.Interpret(command, Metadata.Capabilities.MaxSamplingRate);

            switch (effect.Kind)
            {
                case SessionCommandEffectKind.StopStreaming:
                    IsStreaming = false;
                    break;

                case SessionCommandEffectKind.StartStreaming:
                    // Frequency first: anything observing IsStreaming must never catch it true next
                    // to a rate belonging to a previous session.
                    _streamingFrequency = effect.StreamingFrequency;

                    // A restart while already streaming is not a session boundary — the typed API
                    // cannot even express it (StartStreaming returns early) — so there is nothing to
                    // re-anchor and recording the new rate is all that is warranted.
                    if (!IsStreaming)
                    {
                        // A session is beginning, so it gets exactly the preparation StartStreaming
                        // would have given it. Ordering matches too: the state is ready before the
                        // flag flips.
                        BeginStreamingSession();
                        IsStreaming = true;
                    }

                    break;

                case SessionCommandEffectKind.UnusableStreamingStart:
                    SafeTrace(
                        $"[{nameof(TrackSessionCommand)}] Ignoring a start-streaming command with an unusable rate "
                        + $"('{effect.RejectedRate}'); the session state is unchanged.");
                    break;

                case SessionCommandEffectKind.SetAdcEnableMask:
                    ApplyAdcEnableMask(effect.AdcEnableMask);
                    break;
            }
        }

        /// <summary>
        /// Applies a sent ADC enable bitmask to this device's analog channels, so a caller who
        /// enabled channels with a raw command has the same restorable session as one who used
        /// <see cref="EnableChannels"/>.
        /// </summary>
        /// <remarks>
        /// The mask is a set-replace, exactly as the firmware treats it, so every analog channel is
        /// assigned from it rather than only the set bits. A device-reported mask still wins on the
        /// next status frame (#409) — that is the device's own view, and it outranks what was asked
        /// for.
        /// </remarks>
        private void ApplyAdcEnableMask(uint mask)
        {
            WithChannelsLock(() =>
            {
                foreach (var channel in SnapshotChannels())
                {
                    if (channel.Type != ChannelType.Analog || channel.ChannelNumber > ChannelControlOperations.MaxAdcBitmaskChannel)
                    {
                        continue;
                    }

                    channel.IsEnabled = (mask & (1u << channel.ChannelNumber)) != 0;
                }
            });
        }

        #endregion

        #region Session restore after an automatic reconnect (issue #379)

        /// <summary>
        /// What the streaming session looked like at the instant the connection dropped. Null until
        /// a drop is detected with reconnect enabled.
        /// </summary>
        private volatile StreamingSessionSnapshot? _sessionSnapshot;

        /// <inheritdoc />
        protected override void CaptureSessionSnapshot() =>
            _sessionSnapshot = StreamingSessionSnapshot.Capture(GetChannelsSnapshot(), IsStreaming);

        /// <summary>
        /// Re-applies the enabled-channel set recorded at the drop and, if the policy says so,
        /// restarts a stream that was interrupted.
        /// </summary>
        /// <remarks>
        /// <para>
        /// What to restore is <see cref="StreamingSessionSnapshot.PlanRestore"/>'s decision; this
        /// method owns the effects, and their order is the part that matters. See that method for
        /// why the enable set is replayed from the snapshot rather than read back off the channels.
        /// </para>
        /// <para>
        /// A resumed stream is a genuinely new session: timestamp reconstruction re-anchors and the
        /// gap detector resets, because the device's tick counter may well have restarted while it
        /// was away, and carrying the old anchor across would manufacture a nonsense gap.
        /// <see cref="DaqifiDevice.Reconnected"/> is the marker for the outage, and it carries its
        /// duration.
        /// </para>
        /// </remarks>
        protected override Task<bool> RestoreSessionSnapshotAsync(
            ReconnectOptions options,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(options);

            // A reconnected device is never streaming: re-initialization has just sent it
            // StopStreamData. The flag, though, is still set from before the drop — nothing stopped
            // the stream, the connection simply ended — and leaving it that way would report a
            // device as streaming while it sits idle, and make StartStreaming() a silent no-op.
            IsStreaming = false;

            var snapshot = _sessionSnapshot;
            if (snapshot == null)
            {
                return Task.FromResult(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Normalize to a known state before re-applying: whatever the device came back with is
            // not necessarily what it had, and the enable commands are set-replace anyway. The
            // channel list is read afterwards so the plan is built against the post-reset objects.
            DisableAllChannels();

            var plan = snapshot.PlanRestore(GetChannelsSnapshot(), options);
            if (plan.ChannelsToEnable.Count > 0)
            {
                EnableChannels(plan.ChannelsToEnable);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (plan.ResumeStreaming)
            {
                StartStreaming();
            }

            return Task.FromResult(plan.ResumeStreaming);
        }

        #endregion

        /// <summary>
        /// The default bounded-buffer capacity (in samples) used by <see cref="StreamSamplesAsync"/>.
        /// </summary>
        public const int DefaultLiveSampleBufferCapacity = 4096;

        /// <summary>
        /// Gets the cumulative number of live samples dropped across all <see cref="StreamSamplesAsync"/>
        /// enumerations because a consumer could not keep up with the incoming rate (drop-oldest policy).
        /// A non-zero and growing value means a live consumer is too slow for the current stream rate.
        /// </summary>
        public long DroppedLiveSampleCount => _liveSampleStream.DroppedSampleCount;

        /// <summary>
        /// Exposes decoded live samples as an <see cref="IAsyncEnumerable{T}"/> for pull-based
        /// <c>await foreach</c> consumption with cancellation and backpressure — bringing the live path
        /// up to the same async-stream idiom the SD-card and export paths already use. Additive: the
        /// per-channel <see cref="IChannel.SampleReceived"/> and raw-frame events are unaffected.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Samples are buffered in a bounded channel with a <b>drop-oldest</b> overflow policy: if the
        /// consumer falls behind, the oldest buffered samples are discarded (memory never grows
        /// unbounded) and <see cref="DroppedLiveSampleCount"/> is incremented — the decode thread that
        /// produces samples is never blocked. Enumeration observes the channels present when it starts;
        /// cancelling <paramref name="cancellationToken"/> ends it promptly (surfaced as
        /// <see cref="OperationCanceledException"/>) and unsubscribes, but does <b>not</b> stop the
        /// device's stream — call <see cref="StopStreaming"/> for that.
        /// </para>
        /// <para>
        /// <b>An enumeration is bound to the connected session it starts in</b> (issue #496), because
        /// samples only ever arrive on one. It ends as soon as the device stops being connected, after
        /// yielding whatever was already buffered:
        /// </para>
        /// <list type="bullet">
        /// <item><description>
        /// A teardown that was asked for — <see cref="DaqifiDevice.Disconnect"/>,
        /// <see cref="DaqifiDevice.DisconnectAsync"/>, or <see cref="DaqifiDevice.Dispose()"/> — ends
        /// the <c>await foreach</c> normally.
        /// </description></item>
        /// <item><description>
        /// A drop the caller did not ask for — an unplug, a WiFi loss, a reconnect that gives up —
        /// throws <see cref="DeviceNotConnectedException"/>. A cut-short acquisition must not be
        /// indistinguishable from a complete one.
        /// </description></item>
        /// <item><description>
        /// Starting an enumeration on a device that is not connected throws
        /// <see cref="DeviceNotConnectedException"/> on the first <c>MoveNextAsync</c>, rather than
        /// waiting for samples that cannot come.
        /// </description></item>
        /// </list>
        /// <para>
        /// Automatic reconnection (<see cref="DaqifiDevice.ReconnectOptions"/>) does <b>not</b> resume
        /// an enumeration: the drop ends it, and a caller that wants live samples from the restored
        /// session starts a new one once <see cref="DaqifiDevice.Status"/> reads
        /// <see cref="ConnectionStatus.Connected"/> again. Ending is what makes the outcome the same
        /// whether or not reconnect is enabled, and the restored session may not even be built from
        /// the same channel objects this enumeration subscribed to.
        /// </para>
        /// <para>
        /// This returns <see cref="LiveSampleStream"/>'s async iterator directly rather than wrapping
        /// it in one of its own, which is what keeps the two deferred behaviors a caller can observe
        /// exactly as they were: <c>WithCancellation</c> still reaches the iterator's own
        /// <c>[EnumeratorCancellation]</c> parameter, and an invalid <paramref name="bufferCapacity"/>
        /// still throws on the first <c>MoveNextAsync</c> rather than at the call.
        /// </para>
        /// </remarks>
        /// <param name="cancellationToken">Ends enumeration when cancelled.</param>
        /// <param name="bufferCapacity">
        /// Bounded buffer capacity; defaults to <see cref="DefaultLiveSampleBufferCapacity"/> when null.
        /// </param>
        /// <returns>An async stream of <see cref="LiveSample"/> (channel + decoded sample).</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="bufferCapacity"/> is less than 1.</exception>
        /// <exception cref="DeviceNotConnectedException">
        /// The device is not connected when enumeration starts, or the connection was lost while it
        /// was running. Both are raised from <c>MoveNextAsync</c>, after any buffered samples.
        /// </exception>
        public IAsyncEnumerable<LiveSample> StreamSamplesAsync(
            CancellationToken cancellationToken = default,
            int? bufferCapacity = null)
            => _liveSampleStream.StreamSamplesAsync(cancellationToken, bufferCapacity);

        /// <summary>
        /// Ends the live-sample enumerations when the device stops being connected (issue #496).
        /// </summary>
        /// <remarks>
        /// The only device state that has to react to a transition before consumers do. Everything
        /// else the streaming device owns is either driven by inbound frames — which stop on their
        /// own — or torn down by the disconnect itself.
        /// </remarks>
        internal override void OnConnectionStatusChanged(ConnectionStatus status)
        {
            base.OnConnectionStatusChanged(status);

            // Null only if a transition could somehow beat InitializeStreamingDevice, which no
            // constructor path allows; cheap enough to not depend on that staying true.
            _liveSampleStream?.OnConnectionStatusChanged(status);
        }

        /// <inheritdoc />
        internal override void ReleaseDerivedResources()
        {
            base.ReleaseDerivedResources();
            _liveSampleStream?.OnDeviceReleased();
        }

        /// <summary>
        /// Handles a streaming data frame by handing it to <see cref="StreamFrameDecoder"/>, which
        /// screens out the frames the device should not have sent, then re-raises what survives for
        /// raw-frame consumers (via the base implementation) and, while streaming, decodes it into
        /// per-channel samples that drive <see cref="IChannel.SampleReceived"/>.
        /// </summary>
        /// <remarks>
        /// Screening covers both consumer paths, which is the whole point of issue #425. The
        /// per-channel decode has guarded against the firmware's malformed leading frame since
        /// issue #351, but the raw <see cref="DaqifiDevice.MessageReceived"/> event was still handed
        /// the frame verbatim — and that is the path most callers actually use, including the
        /// example CLI, whose offline export inferred a channel count of one from it and truncated
        /// every sample that followed. Whatever is unfit for the decoded path is unfit for the raw
        /// one; both are gated together, and every drop is reported through
        /// <see cref="StreamFrameDiscarded"/>.
        /// </remarks>
        /// <param name="message">The streaming message from the device.</param>
        protected override void OnStreamMessageReceived(DaqifiOutMessage message) =>
            _frameDecoder.ProcessFrame(message);

        /// <summary>
        /// Raises <see cref="StreamFrameDiscarded"/> for a frame the decoder withheld. Subscriber
        /// exceptions are isolated, mirroring <see cref="RaiseGapDetected"/>.
        /// </summary>
        /// <remarks>
        /// Kept on the device rather than moved into <see cref="StreamFrameDecoder"/> so the event's
        /// <c>sender</c> stays the device a subscriber attached to. The decoder counts the discard
        /// before calling this, so <see cref="DiscardedStreamFrameCount"/> read inside a handler
        /// still already includes the frame being reported.
        /// </remarks>
        /// <param name="e">The discard to report.</param>
        private void RaiseStreamFrameDiscarded(StreamFrameDiscardedEventArgs e)
        {
            var handler = StreamFrameDiscarded;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(this, e);
            }
            catch (Exception ex)
            {
                SafeTrace($"[{nameof(StreamFrameDiscarded)}] Subscriber threw: {ex}");
            }
        }

        /// <summary>
        /// Writes a diagnostic line, swallowing anything a misbehaving <see cref="TraceListener"/>
        /// throws.
        /// </summary>
        /// <remarks>
        /// <see cref="Trace"/> dispatches to listeners the consumer installed, so it is consumer
        /// code and can throw like any other. That matters most in the places that exist purely to
        /// isolate the frame pipeline from faults: a listener throwing out of the <c>catch</c> that
        /// was containing a bad subscriber would defeat the containment and take down the very
        /// frame processing it was protecting. Same reasoning, and the same guarantee, as
        /// <c>DaqifiDevice.SafeLog</c> — which is private to the base class, hence this local twin.
        /// </remarks>
        /// <param name="message">The diagnostic line to write.</param>
        private static void SafeTrace(string message)
        {
            try
            {
                Trace.WriteLine(message);
            }
            catch
            {
                // A trace listener that throws is not permitted to affect device operation.
            }
        }

        /// <summary>
        /// Raises <see cref="GapDetected"/>, isolating the decode pipeline from a subscriber
        /// exception so a throwing handler cannot skip this frame's per-channel decode (which the
        /// broad catch in <see cref="OnStreamMessageReceived"/> would then silently drop). Mirrors
        /// <c>DaqifiDevice.RaiseClassifiedEvent</c>.
        /// </summary>
        private void RaiseGapDetected(TimestampGapEventArgs args)
        {
            var handler = GapDetected;
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
                SafeTrace($"[{nameof(GapDetected)}] Subscriber threw: {ex}");
            }
        }

        /// <inheritdoc />
        public void EnableChannel(IChannel channel) => _channelControl.EnableChannel(channel);

        /// <inheritdoc cref="IStreamingDevice.EnableChannelAsync" />
        public Task EnableChannelAsync(IChannel channel, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnableChannel(channel);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public void EnableChannels(IEnumerable<IChannel> channels) => _channelControl.EnableChannels(channels);

        /// <inheritdoc cref="IStreamingDevice.EnableChannelsAsync" />
        public Task EnableChannelsAsync(IEnumerable<IChannel> channels, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnableChannels(channels);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public void DisableChannel(IChannel channel) => _channelControl.DisableChannel(channel);

        /// <inheritdoc cref="IStreamingDevice.DisableChannelAsync" />
        public Task DisableChannelAsync(IChannel channel, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DisableChannel(channel);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public void DisableAllChannels() => _channelControl.DisableAllChannels();

        /// <inheritdoc cref="IStreamingDevice.DisableAllChannelsAsync" />
        public Task DisableAllChannelsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DisableAllChannels();
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public void SetDioDirection(IChannel channel, ChannelDirection direction)
            => _channelControl.SetDioDirection(channel, direction);

        /// <inheritdoc cref="IStreamingDevice.SetDioDirectionAsync" />
        public Task SetDioDirectionAsync(IChannel channel, ChannelDirection direction, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetDioDirection(channel, direction);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public void SetDioValue(IChannel channel, bool value) => _channelControl.SetDioValue(channel, value);

        /// <inheritdoc cref="IStreamingDevice.SetDioValueAsync" />
        public Task SetDioValueAsync(IChannel channel, bool value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetDioValue(channel, value);
            return Task.CompletedTask;
        }

        /// <summary>
        /// The lowest PWM frequency the firmware reproduces correctly. Below this the firmware's
        /// 16-bit period register silently wraps and the output runs in the kilohertz range.
        /// </summary>
        public const int MinPwmFrequencyHz = 6;

        /// <summary>
        /// The highest PWM frequency the device advertises (full duty resolution is retained
        /// well past this, so the advertised cap is the binding limit).
        /// </summary>
        public const int MaxPwmFrequencyHz = 50_000;

        /// <summary>
        /// Default device-wide PWM frequency, in hertz, used until a frequency has been
        /// commanded via <see cref="SetPwmFrequency"/>.
        /// </summary>
        public const int DefaultPwmFrequencyHz = 1_000;

        /// <summary>
        /// Gets the last commanded device-wide PWM frequency in hertz. Local bookkeeping
        /// mirroring <see cref="SetPwmFrequency"/>; defaults to <see cref="DefaultPwmFrequencyHz"/>
        /// (a commandable value) until a frequency has been set this session.
        /// </summary>
        public int PwmFrequencyHz => _channelControl.PwmFrequencyHz;

        /// <inheritdoc />
        public void SetPwmEnabled(IChannel channel, bool enabled) => _channelControl.SetPwmEnabled(channel, enabled);

        /// <inheritdoc cref="IStreamingDevice.SetPwmEnabledAsync" />
        public Task SetPwmEnabledAsync(IChannel channel, bool enabled, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetPwmEnabled(channel, enabled);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public void SetPwmDutyCycle(IChannel channel, int dutyCyclePercent)
            => _channelControl.SetPwmDutyCycle(channel, dutyCyclePercent);

        /// <inheritdoc cref="IStreamingDevice.SetPwmDutyCycleAsync" />
        public Task SetPwmDutyCycleAsync(IChannel channel, int dutyCyclePercent, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetPwmDutyCycle(channel, dutyCyclePercent);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public void SetPwmFrequency(int frequencyHz) => _channelControl.SetPwmFrequency(frequencyHz);

        /// <inheritdoc cref="IStreamingDevice.SetPwmFrequencyAsync" />
        public Task SetPwmFrequencyAsync(int frequencyHz, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetPwmFrequency(frequencyHz);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Sets and persists the device's user-defined friendly name to NVM, then optimistically
        /// updates <see cref="DaqifiDevice.Metadata"/>'s <see cref="DeviceMetadata.FriendlyName"/>.
        /// </summary>
        /// <remarks>
        /// Composes the firmware sequence <c>SYSTem:DEVice:NAME "name"</c> then
        /// <c>SYSTem:DEVice:NAME:SAVE</c> (producer commands added in #302). The device does not echo
        /// the new name back synchronously — and may not stream another status frame for a while — so
        /// the local metadata is updated optimistically once both commands are sent. This is the
        /// device-level composition desktop hand-rolled (its "no producer helper exists" note is stale).
        ///
        /// <para>Completion semantics: the returned task completes once the commands are enqueued to
        /// the outbound producer — it does <b>not</b> await on-device application or NVM persistence,
        /// which the firmware does not acknowledge. This matches the other fire-and-forget device
        /// commands (e.g. <see cref="LoadNetworkConfigurationAsync"/>, <see cref="FactoryResetNetworkAsync"/>);
        /// the async signature exists for cancellation and device-surface consistency.</para>
        /// </remarks>
        /// <param name="name">
        /// 1-<see cref="ScpiMessageProducer.MaxFriendlyNameLength"/> printable ASCII characters
        /// (0x20-0x7E), excluding <c>"</c> and <c>\</c> — see <see cref="ScpiMessageProducer.IsFriendlyNameValid"/>.
        /// </param>
        /// <param name="cancellationToken">A cancellation token observed before the commands are sent.</param>
        /// <returns>A task that completes once both commands have been sent.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="name"/> is null.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="name"/> fails validation.</exception>
        /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
        public Task SetFriendlyNameAsync(string name, CancellationToken cancellationToken = default)
            => _administration.SetFriendlyNameAsync(name, cancellationToken);

        /// <inheritdoc />
        public void SetAnalogOutput(int channelNumber, double voltage)
            => _channelControl.SetAnalogOutput(channelNumber, voltage);

        /// <inheritdoc cref="IStreamingDevice.SetAnalogOutputAsync" />
        public Task SetAnalogOutputAsync(int channelNumber, double voltage, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetAnalogOutput(channelNumber, voltage);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Stages an analog output (DAC) voltage without applying it. The value takes effect on the
        /// next <see cref="LatchAnalogOutputs"/>, so several channels can be made to change together.
        /// </summary>
        /// <param name="channelNumber">The analog output channel number.</param>
        /// <param name="voltage">
        /// The output voltage, in volts. Must be finite, and within the channel's range when the
        /// device described one.
        /// </param>
        /// <remarks>
        /// This is the device's own two-step protocol made explicit:
        /// <see cref="SetAnalogOutput"/> is exactly a stage followed by a latch. Until the latch,
        /// <see cref="Daqifi.Core.Channel.IAnalogOutputChannel.PendingVoltage"/> reports the staged
        /// value and <see cref="Daqifi.Core.Channel.IAnalogOutputChannel.OutputVoltage"/> still
        /// reports what the pin is driving. Analog output is available on NQ3 hardware only.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The channel number is negative, or the voltage falls outside the channel's stated range.
        /// </exception>
        /// <exception cref="DeviceNotConnectedException">The device is not connected.</exception>
        public void StageAnalogOutput(int channelNumber, double voltage)
            => _channelControl.StageAnalogOutput(channelNumber, voltage);

        /// <summary>
        /// Applies every analog output (DAC) voltage staged since the last latch, so the staged
        /// channels change together.
        /// </summary>
        /// <remarks>
        /// Latching with nothing staged is harmless — the device re-applies what it already holds.
        /// Analog output is available on NQ3 hardware only.
        /// </remarks>
        /// <exception cref="DeviceNotConnectedException">The device is not connected.</exception>
        public void LatchAnalogOutputs() => _channelControl.LatchAnalogOutputs();

        /// <summary>
        /// Asks the device what voltage an analog output (DAC) channel is holding, and records it
        /// on the modelled channel.
        /// </summary>
        /// <param name="channelNumber">The analog output channel number.</param>
        /// <param name="cancellationToken">Cancels the exchange.</param>
        /// <returns>The voltage the device reports, in volts.</returns>
        /// <remarks>
        /// The DAC has no hardware readback, so the device answers with the value it was last told
        /// to drive. That still makes this the authoritative round-trip: it reflects what the
        /// device actually accepted, including a write made before this session connected.
        /// Analog output is available on NQ3 hardware only.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="channelNumber"/> is negative.</exception>
        /// <exception cref="DeviceNotConnectedException">The device is not connected.</exception>
        /// <exception cref="InvalidOperationException">
        /// The device rejected the query or answered with something that is not a voltage.
        /// </exception>
        public Task<double> GetAnalogOutputAsync(int channelNumber, CancellationToken cancellationToken = default)
            => _channelControl.GetAnalogOutputAsync(channelNumber, cancellationToken);

        /// <inheritdoc />
        public void Reboot() => _administration.Reboot();

        /// <summary>
        /// Reboots the device and disconnects from it, without blocking the calling thread.
        /// </summary>
        /// <remarks>
        /// Unlike <see cref="IStreamingDevice.RebootAsync"/>'s default implementation, this override
        /// does not call the blocking <see cref="Reboot"/>: that path tears down through
        /// <see cref="DaqifiDevice.Disconnect"/>, which can stall the caller for the full teardown
        /// wait (up to <see cref="DaqifiDevice.TextExchangeTeardownWait"/>) — exactly the freeze the
        /// async surface exists to avoid. This sends the same reboot command and then awaits
        /// <see cref="DaqifiDevice.DisconnectAsync"/> instead, so the local teardown is genuinely
        /// asynchronous.
        /// </remarks>
        /// <inheritdoc cref="IStreamingDevice.RebootAsync" path="/param|/returns|/exception" />
        public async Task RebootAsync(CancellationToken cancellationToken = default)
        {
            // Cancellation is checked before validation, matching every other ...Async member on
            // this class: a pre-cancelled token must surface as OperationCanceledException even on
            // a disconnected device, not be masked by DeviceNotConnectedException.
            cancellationToken.ThrowIfCancellationRequested();

            EnsureConnected();

            Send(ScpiMessageProducer.RebootDevice);

            // The device drops its link while restarting, so tear down the local connection —
            // without blocking the caller, unlike Reboot()'s synchronous Disconnect().
            await DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public void SaveAdcCalibration() => _administration.SaveAdcCalibration();

        /// <inheritdoc />
        public void LoadAdcCalibration() => _administration.LoadAdcCalibration();

        /// <inheritdoc />
        public void SetAdcCalibrationSlope(int channelNumber, double calM)
            => _administration.SetAdcCalibrationSlope(channelNumber, calM);

        /// <inheritdoc />
        public void SetAdcCalibrationOffset(int channelNumber, double calB)
            => _administration.SetAdcCalibrationOffset(channelNumber, calB);

        /// <inheritdoc />
        public void SaveFactoryAdcCalibration() => _administration.SaveFactoryAdcCalibration();

        /// <inheritdoc />
        public void LoadFactoryAdcCalibration() => _administration.LoadFactoryAdcCalibration();

        /// <inheritdoc />
        public void UseAdcCalibration(int bank) => _administration.UseAdcCalibration(bank);

        /// <inheritdoc />
        public void SaveVoltagePrecision() => _administration.SaveVoltagePrecision();

        /// <inheritdoc />
        public void LoadVoltagePrecision() => _administration.LoadVoltagePrecision();

        /// <inheritdoc />
        public Task SaveAdcCalibrationAsync(CancellationToken cancellationToken = default)
            => _administration.SaveAdcCalibrationAsync(cancellationToken);

        /// <inheritdoc />
        public Task LoadAdcCalibrationAsync(CancellationToken cancellationToken = default)
            => _administration.LoadAdcCalibrationAsync(cancellationToken);

        /// <inheritdoc />
        public Task SetAdcCalibrationSlopeAsync(int channelNumber, double calM, CancellationToken cancellationToken = default)
            => _administration.SetAdcCalibrationSlopeAsync(channelNumber, calM, cancellationToken);

        /// <inheritdoc />
        public Task SetAdcCalibrationOffsetAsync(int channelNumber, double calB, CancellationToken cancellationToken = default)
            => _administration.SetAdcCalibrationOffsetAsync(channelNumber, calB, cancellationToken);

        /// <inheritdoc />
        public Task SaveFactoryAdcCalibrationAsync(CancellationToken cancellationToken = default)
            => _administration.SaveFactoryAdcCalibrationAsync(cancellationToken);

        /// <inheritdoc />
        public Task LoadFactoryAdcCalibrationAsync(CancellationToken cancellationToken = default)
            => _administration.LoadFactoryAdcCalibrationAsync(cancellationToken);

        /// <inheritdoc />
        public Task UseAdcCalibrationAsync(int bank, CancellationToken cancellationToken = default)
            => _administration.UseAdcCalibrationAsync(bank, cancellationToken);

        /// <inheritdoc />
        public Task SaveVoltagePrecisionAsync(CancellationToken cancellationToken = default)
            => _administration.SaveVoltagePrecisionAsync(cancellationToken);

        /// <inheritdoc />
        public Task LoadVoltagePrecisionAsync(CancellationToken cancellationToken = default)
            => _administration.LoadVoltagePrecisionAsync(cancellationToken);


        // -----------------------------------------------------------------
        // Delegation to the operation collaborators.
        //
        // Each block below was lifted out of this class wholesale (#344); what
        // remains is the public surface, unchanged, forwarding to the object
        // that now owns the implementation. The collaborators reach back
        // through IDeviceOperationHost, implemented explicitly at the bottom of
        // this file, so every call still passes through this device's own
        // virtual members and any subclass override of them.
        // -----------------------------------------------------------------

        /// <summary>The streaming hot path: frame screening, timestamps, gaps, per-channel decode.</summary>
        private StreamFrameDecoder _frameDecoder = null!;

        /// <summary>The pull-based live-sample view: bounded buffer, drop-oldest, drop counter.</summary>
        private LiveSampleStream _liveSampleStream = null!;

        /// <summary>Channel enable/disable, DIO, PWM and analog output (<see cref="IStreamingDevice"/>).</summary>
        private ChannelControlOperations _channelControl = null!;

        /// <summary>Reboot, ADC calibration banks, voltage precision and the friendly-name write.</summary>
        private DeviceAdministrationOperations _administration = null!;

        /// <summary>WiFi/LAN configuration (<see cref="INetworkConfigurable"/>).</summary>
        private NetworkConfigurationOperations _networkOperations = null!;

        /// <summary>SD card operations (<see cref="ISdCardOperations"/>) and the shared-SPI handover.</summary>
        private SdCardOperations _sdCardOperations = null!;

        /// <summary>WiFi module chip info (<see cref="ILanChipInfoProvider"/>).</summary>
        private LanChipInfoOperations _lanChipInfoOperations = null!;

        /// <summary>Device diagnostics (<see cref="IDeviceDiagnostics"/>).</summary>
        private DeviceDiagnosticsOperations _diagnosticsOperations = null!;

        #region INetworkConfigurable

        /// <inheritdoc />
        public NetworkConfiguration NetworkConfiguration => _networkOperations.NetworkConfiguration;

        /// <inheritdoc />
        public Task UpdateNetworkConfigurationAsync(NetworkConfiguration configuration, CancellationToken cancellationToken = default)
            => _networkOperations.UpdateNetworkConfigurationAsync(configuration, cancellationToken);

        /// <inheritdoc />
        public Task LoadNetworkConfigurationAsync(CancellationToken cancellationToken = default)
            => _networkOperations.LoadNetworkConfigurationAsync(cancellationToken);

        /// <inheritdoc />
        public Task FactoryResetNetworkAsync(CancellationToken cancellationToken = default)
            => _networkOperations.FactoryResetNetworkAsync(cancellationToken);

        /// <inheritdoc />
        public void PrepareSdInterface() => _sdCardOperations.PrepareSdInterface();

        /// <inheritdoc />
        public void PrepareLanInterface() => _sdCardOperations.PrepareLanInterface();

        #endregion

        #region ISdCardOperations

        /// <inheritdoc />
        public Task<IReadOnlyList<SdCardFileInfo>> GetSdCardFilesAsync(CancellationToken cancellationToken = default)
            => _sdCardOperations.GetSdCardFilesAsync(cancellationToken);

        /// <inheritdoc />
        public Task<SdCardStorageInfo> GetSdCardStorageAsync(CancellationToken cancellationToken = default)
            => _sdCardOperations.GetSdCardStorageAsync(cancellationToken);

        /// <inheritdoc />
        public Task<SdCardSpaceCheckResult> CheckSdCardSpaceAsync(
            SdCardCaptureEstimate? plannedCapture = null,
            long minimumFreeBytes = SdCardSpaceCheck.DefaultMinimumFreeBytes,
            CancellationToken cancellationToken = default)
            => _sdCardOperations.CheckSdCardSpaceAsync(plannedCapture, minimumFreeBytes, cancellationToken);

        /// <inheritdoc />
        public void SetSdCardMinimumFreeSpace(long bytes) => _sdCardOperations.SetSdCardMinimumFreeSpace(bytes);

        /// <inheritdoc />
        public Task StartSdCardLoggingAsync(string? fileName = null, string? channelMask = null, SdCardLogFormat format = SdCardLogFormat.Protobuf, CancellationToken cancellationToken = default)
            => _sdCardOperations.StartSdCardLoggingAsync(fileName, channelMask, format, cancellationToken);

        /// <inheritdoc />
        public Task<SdCardLoggingSession> StartSdCardLoggingSessionAsync(string? fileName = null, string? channelMask = null, SdCardLogFormat format = SdCardLogFormat.Protobuf, CancellationToken cancellationToken = default)
            => _sdCardOperations.StartSdCardLoggingSessionAsync(fileName, channelMask, format, cancellationToken);

        /// <inheritdoc />
        public Task StopSdCardLoggingAsync(CancellationToken cancellationToken = default)
            => _sdCardOperations.StopSdCardLoggingAsync(cancellationToken);

        /// <inheritdoc />
        public Task DeleteSdCardFileAsync(string fileName, CancellationToken cancellationToken = default)
            => _sdCardOperations.DeleteSdCardFileAsync(fileName, cancellationToken);

        /// <inheritdoc />
        public Task FormatSdCardAsync(CancellationToken cancellationToken = default)
            => _sdCardOperations.FormatSdCardAsync(cancellationToken);

        /// <inheritdoc />
        public Task<SdCardDownloadResult> DownloadSdCardFileAsync(
            string fileName,
            Stream destinationStream,
            IProgress<SdCardTransferProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => _sdCardOperations.DownloadSdCardFileAsync(fileName, destinationStream, progress, cancellationToken);

        /// <inheritdoc />
        public Task<SdCardDownloadResult> DownloadSdCardFileAsync(
            string fileName,
            IProgress<SdCardTransferProgress>? progress = null,
            CancellationToken cancellationToken = default)
            => _sdCardOperations.DownloadSdCardFileAsync(fileName, progress, cancellationToken);

        /// <summary>
        /// Raises the <see cref="LowSdSpaceWarning"/> event.
        /// </summary>
        /// <param name="e">The warning event arguments.</param>
        /// <remarks>
        /// Stays on the device rather than moving with the space check that triggers it: the event
        /// is part of this device's public surface, so subscribers must keep seeing this device as
        /// the <c>sender</c>, and a subclass override must keep intercepting it.
        /// </remarks>
        protected virtual void OnLowSdSpaceWarning(LowSdSpaceWarningEventArgs e)
        {
            LowSdSpaceWarning?.Invoke(this, e);
        }

        /// <summary>
        /// Overall wall-clock budget for one
        /// <see cref="DownloadSdCardFileAsync(string, Stream, IProgress{SdCardTransferProgress}?, CancellationToken)"/>
        /// call, covering every GET attempt. This is the per-transfer limit the receiver has
        /// always applied; what #399 changed is that the download now enforces it itself instead
        /// of trusting whatever the transfer is parked in to notice a cancellation token.
        /// </summary>
        /// <remarks>Virtual only as a test seam — a 30-minute budget is not unit-testable.</remarks>
        internal virtual TimeSpan SdCardDownloadTimeout => TimeSpan.FromMinutes(30);

        /// <summary>
        /// How long a download may go without receiving a single byte before it is declared
        /// stalled. Bounds a WiFi/TCP transfer whose device stopped answering, which the socket
        /// itself never reports (see <see cref="SdCardFileReceiver.DefaultIdleTimeout"/>).
        /// </summary>
        /// <remarks>Virtual only as a test seam — a 20-second window is not unit-testable.</remarks>
        internal virtual TimeSpan SdCardTransferIdleTimeout => SdCardFileReceiver.DefaultIdleTimeout;

        #endregion

        #region ILanChipInfoProvider

        /// <inheritdoc />
        public Task<LanChipInfo?> GetLanChipInfoAsync(CancellationToken cancellationToken = default)
            => _lanChipInfoOperations.GetLanChipInfoAsync(cancellationToken);

        #endregion

        #region IDeviceDiagnostics

        /// <inheritdoc />
        public Task<IReadOnlyList<SystemLogEntry>> GetSystemLogAsync(CancellationToken cancellationToken = default)
            => _diagnosticsOperations.GetSystemLogAsync(cancellationToken);

        /// <inheritdoc />
        public Task ClearSystemLogAsync(CancellationToken cancellationToken = default)
            => _diagnosticsOperations.ClearSystemLogAsync(cancellationToken);

        /// <inheritdoc />
        public Task<LogLevelSetting> SetLogLevelAsync(string module, int level, CancellationToken cancellationToken = default)
            => _diagnosticsOperations.SetLogLevelAsync(module, level, cancellationToken);

        /// <inheritdoc />
        public Task<IReadOnlyList<string>> GetCommandHistoryAsync(CancellationToken cancellationToken = default)
            => _diagnosticsOperations.GetCommandHistoryAsync(cancellationToken);

        /// <inheritdoc />
        public Task TestSystemLogAsync(CancellationToken cancellationToken = default)
            => _diagnosticsOperations.TestSystemLogAsync(cancellationToken);

        /// <inheritdoc />
        public Task<int> GetSystemErrorCountAsync(CancellationToken cancellationToken = default)
            => _diagnosticsOperations.GetSystemErrorCountAsync(cancellationToken);

        /// <inheritdoc />
        public Task<StreamStats> GetStreamStatsAsync(CancellationToken cancellationToken = default)
            => _diagnosticsOperations.GetStreamStatsAsync(cancellationToken);

        /// <inheritdoc />
        public Task<MemoryDiagnostics> GetMemoryDiagnosticsAsync(CancellationToken cancellationToken = default)
            => _diagnosticsOperations.GetMemoryDiagnosticsAsync(cancellationToken);

        #endregion

        #region IDeviceOperationHost / ISdCardOperationHost

        // Explicit implementation: this is how the collaborators reach the device, and none of it
        // belongs on the public surface. Every member forwards to the member it names, so the
        // virtual ones stay virtual and a subclass that overrides them still intercepts every
        // operation that was moved out of this class.

        bool IDeviceOperationHost.IsConnected => IsConnected;

        bool IDeviceOperationHost.IsUsbConnection => IsUsbConnection;

        bool IDeviceOperationHost.IsStreaming
        {
            get => IsStreaming;
            set => IsStreaming = value;
        }

        int IDeviceOperationHost.StreamingFrequency => StreamingFrequency;

        void IDeviceOperationHost.StopStreaming() => StopStreaming();

        void IDeviceOperationHost.StartStreaming() => StartStreaming();

        void IDeviceOperationHost.Send<T>(IOutboundMessage<T> message) => Send(message);

        DeviceMetadata IDeviceOperationHost.Metadata => Metadata;

        void IDeviceOperationHost.Disconnect() => Disconnect();

        IReadOnlyList<IChannel> IDeviceOperationHost.SnapshotChannels() => SnapshotChannels();

        /// <inheritdoc />
        long IDeviceOperationHost.ChannelStateVersion => ChannelStateVersion;

        void IDeviceOperationHost.WithChannelsLock(Action action) => WithChannelsLock(action);

#pragma warning disable CA1068 // Matches the seam it forwards to.
        Task<IReadOnlyList<string>> IDeviceOperationHost.ExecuteTextCommandAsync(
            Action setupAction,
            int responseTimeoutMs,
            int completionTimeoutMs,
            CancellationToken cancellationToken,
            Func<CancellationToken, Task>? prepareAsync,
            Func<Task>? finalizeAsync,
            bool keepBlankLines)
            => ExecuteTextCommandAsync(
                setupAction, responseTimeoutMs, completionTimeoutMs, cancellationToken, prepareAsync,
                finalizeAsync, keepBlankLines);
#pragma warning restore CA1068

        Task<IReadOnlyList<string>> IDeviceOperationHost.DrainErrorQueueAsync(
            int maxIterations,
            CancellationToken cancellationToken)
            => DrainErrorQueueAsync(maxIterations, cancellationToken);

        Task IDeviceOperationHost.ExecuteRawCaptureAsync(
            Func<Stream, CancellationToken, Task> rawAction,
            CancellationToken cancellationToken)
            => ExecuteRawCaptureAsync(rawAction, cancellationToken);

        void IDeviceOperationHost.EnsureSupported(DeviceFeature feature) => EnsureSupported(feature);

        FeatureNotSupportedException IDeviceOperationHost.CreateFeatureNotSupportedException(DeviceFeature feature)
            => CreateFeatureNotSupportedException(feature);

        TimeSpan ISdCardOperationHost.SdCardDownloadTimeout => SdCardDownloadTimeout;

        TimeSpan ISdCardOperationHost.SdCardTransferIdleTimeout => SdCardTransferIdleTimeout;

        void ISdCardOperationHost.RaiseLowSdSpaceWarning(LowSdSpaceWarningEventArgs e) => OnLowSdSpaceWarning(e);

        void IDeviceOperationHost.RaiseStreamFrameDiscarded(StreamFrameDiscardedEventArgs e)
            => RaiseStreamFrameDiscarded(e);

        void IDeviceOperationHost.RaiseGapDetected(TimestampGapEventArgs e) => RaiseGapDetected(e);

        // Deliberately base.OnStreamMessageReceived and not the override: the override is what hands
        // the frame to the decoder in the first place, so calling it here would recurse. This is the
        // same base call the decode block made before it moved out.
        void IDeviceOperationHost.RaiseRawStreamFrame(DaqifiOutMessage message)
            => base.OnStreamMessageReceived(message);

        void IDeviceOperationHost.RaiseStreamDecodeFailure(Exception error)
            => RaiseDeviceError(DeviceErrorSource.StreamDecode, error);

        #endregion
    }
}
