using Daqifi.Core.Channel;
using Daqifi.Core.Communication;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device.Diagnostics;
using Daqifi.Core.Device.Internal;
using Microsoft.Extensions.Logging;
using Daqifi.Core.Device.Network;
using Daqifi.Core.Device.SdCard;
using Daqifi.Core.Firmware;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

#nullable enable

namespace Daqifi.Core.Device
{
    /// <summary>
    /// Represents a DAQiFi device that supports data streaming functionality.
    /// Extends the base DaqifiDevice with streaming-specific operations.
    /// </summary>
    public class DaqifiStreamingDevice : DaqifiDevice, IStreamingDevice, INetworkConfigurable, ISdCardOperations, ILanChipInfoProvider, IDeviceDiagnostics, IDeviceOperationHost
    {
        /// <summary>
        /// Maximum number of retry attempts for the USB stream-interface command sent during
        /// <see cref="OnDeviceInitializingAsync"/> when the device returns a transient SCPI error
        /// (e.g. because the firmware still has the interface set from a prior WiFi session).
        /// </summary>
        private const int UsbStreamInterfaceMaxRetries = 1;

        /// <summary>
        /// Delay in milliseconds before retrying the USB stream-interface command after a
        /// transient SCPI error.
        /// </summary>
        private const int UsbStreamInterfaceRetryDelayMs = 150;

        /// <summary>
        /// Reconstructs host timestamps from the device's rolling 32-bit tick counter during a
        /// streaming session. Scoped to this device instance, so a single fixed key suffices.
        /// </summary>
        private readonly ITimestampProcessor _timestampProcessor = new TimestampProcessor();

        /// <summary>
        /// The per-device key used with <see cref="_timestampProcessor"/>. The processor is not
        /// shared across devices, so the key only needs to be stable within this instance.
        /// </summary>
        private const string StreamTimestampKey = "stream";

        /// <summary>
        /// Detects dropped samples from the device-clock delta between frames. Reset at the start of
        /// every streaming session alongside <see cref="_timestampProcessor"/>. Drives <see cref="GapDetected"/>.
        /// </summary>
        private readonly TimestampGapDetector _gapDetector = new();

        /// <summary>
        /// The maximum number of leading short-analog frames suppressed at stream start
        /// (see <see cref="_awaitingFirstFullAnalogFrame"/>). Bounds the warmup-frame guard so a
        /// genuinely short stream can never be withheld indefinitely.
        /// </summary>
        private const int MaxSuppressedWarmupFrames = 5;

        /// <summary>
        /// True from the start of a streaming session that begins with analog channels enabled,
        /// until the first analog-bearing frame carrying the full enabled-channel complement has
        /// been decoded (disarmed for a digital-only start). Guards the malformed warmup frame
        /// the firmware emits at stream start (issue #351): its fast streaming encoder can emit a
        /// leading frame with fewer analog values than the enabled channel mask, which would
        /// otherwise reach every consumer as a partial <see cref="DataSample"/> (silently corrupting
        /// first-value baselining, gap detection, and export). For such leading short frames only
        /// the malformed analog decode is skipped — a combined frame's digital payload is still
        /// decoded and the raw frame is still re-raised — until the first full frame arrives,
        /// bounded by <see cref="MaxSuppressedWarmupFrames"/>.
        /// </summary>
        private bool _awaitingFirstFullAnalogFrame;

        /// <summary>
        /// Count of leading short-analog frames suppressed in the current session; capped by
        /// <see cref="MaxSuppressedWarmupFrames"/>.
        /// </summary>
        private int _suppressedWarmupFrameCount;

        /// <summary>
        /// Backing counter for <see cref="DecodeFailureCount"/>.
        /// </summary>
        private long _decodeFailureCount;

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
        public long DecodeFailureCount => Interlocked.Read(ref _decodeFailureCount);

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
                    _lastSentPwmFrequencyHz = null;
                }
            };
        }

        /// <summary>
        /// For USB/serial connections, sets the streaming interface to USB so data is routed to the
        /// serial consumer rather than to a previously-configured WiFi destination. Runs as part of
        /// <see cref="DaqifiDevice.InitializeAsync"/> after the standard SCPI sequence.
        /// </summary>
        /// <remarks>
        /// The DAQiFi firmware persists the last configured stream interface across sessions.
        /// If the device was previously set to stream to WiFi (<c>SYSTem:STReam:INTerface 1</c>),
        /// it will continue sending data over WiFi even when connected via USB — causing the serial
        /// consumer to receive nothing. Sending <c>SYSTem:STReam:INTerface 0</c> during USB
        /// initialization ensures data flows to the serial port.
        ///
        /// This runs inside the base <see cref="DaqifiDevice.InitializeAsync"/> exception handling
        /// (before the device is marked initialized/ready), so a cancellation or SCPI error here
        /// leaves the device in a consistent state and re-initializable, rather than falsely Ready.
        ///
        /// The routing command is global device state: it takes the stream away from whatever
        /// interface it was going to, so a second session running it steals another session's data
        /// (#385). It is therefore skipped entirely when <paramref name="preserveActiveStream"/>
        /// is set.
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
        protected override async Task OnDeviceInitializingAsync(
            bool preserveActiveStream,
            CancellationToken cancellationToken)
        {
            if (!IsUsbConnection)
            {
                return;
            }

            // An observe-only session must not re-route the device's single global stream: doing so
            // would take the data away from the session that is already receiving it (#385). The
            // interface is left exactly as the owning session configured it.
            //
            // Returning without observing the token is deliberate: there is no work to abandon, and
            // InitializeAsync re-checks cancellation before it marks the device Ready, so a token
            // cancelled during this hook is still honored.
            if (preserveActiveStream)
            {
                return;
            }

            // Direct streaming to the USB interface. Uses ExecuteTextCommandAsync so the
            // command is sent in text mode (protobuf consumer temporarily stopped) and any
            // SCPI error response is captured rather than garbling the protobuf stream.
            //
            // The firmware persists the last-used stream interface across sessions, so this can
            // transiently reject with a -200 "Execution error" right after connect. Retry with a
            // settle delay before treating it as a hard failure (mirrors the SD card retry).
            IReadOnlyList<string> lines = Array.Empty<string>();
            for (var attempt = 0; attempt <= UsbStreamInterfaceMaxRetries; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(UsbStreamInterfaceRetryDelayMs, cancellationToken).ConfigureAwait(false);
                }

                lines = await ExecuteTextCommandAsync(
                    () => Send(ScpiMessageProducer.SetStreamInterface(StreamInterface.Usb)),
                    responseTimeoutMs: 500,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (!ScpiResponseClassifier.ContainsScpiError(lines))
                {
                    return;
                }
            }

            var lastScpiError = lines.LastOrDefault(ScpiResponseClassifier.IsScpiErrorLine)?.Trim();
            throw new ScpiInitializationErrorException(
                "Device returned a SCPI error while setting stream interface to USB.",
                lines,
                lastScpiError);
        }

        /// <summary>
        /// Starts streaming data from the device at the configured frequency.
        /// </summary>
        /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
        public void StartStreaming()
        {
            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

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
        private void BeginStreamingSession()
        {
            // Re-anchor per-session timestamp reconstruction: the first frame of this session
            // anchors to the current host time, and subsequent frames advance by the device-tick
            // delta. Apply the device-reported tick frequency (falls back to the 50 MHz default
            // when unreported, e.g. older firmware).
            _timestampProcessor.Reset(StreamTimestampKey);
            _timestampProcessor.SetTimestampFrequency(StreamTimestampKey, TimestampFrequency);
            _gapDetector.Reset();

            // Arm the warmup-frame guard only when analog channels are enabled at stream start —
            // the reproduced failure mode (issue #351) is the firmware's leading partial-analog
            // frame at the start of an *analog* stream. A digital-only start needs no guard; leaving
            // it disarmed there also avoids suppressing short analog frames that could arrive far
            // from session start if analog channels are enabled mid-stream (a scenario with no
            // observed warmup frame).
            _awaitingFirstFullAnalogFrame = CountEnabledAnalogChannels(SnapshotChannels()) > 0;
            _suppressedWarmupFrameCount = 0;
            Interlocked.Exchange(ref _decodeFailureCount, 0);
        }

        /// <summary>
        /// Stops streaming data from the device.
        /// </summary>
        /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
        public void StopStreaming()
        {
            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            if (!IsStreaming) return;

            IsStreaming = false;
            Send(ScpiMessageProducer.StopStreaming);
        }

        #region Session state tracking for commands sent directly (issue #379)

        /// <summary>The command <see cref="ScpiMessageProducer.StartStreaming"/> emits.</summary>
        private const string StartStreamingCommand = "SYSTem:StartStreamData";

        /// <summary>The command <see cref="ScpiMessageProducer.StopStreaming"/> emits.</summary>
        private const string StopStreamingCommand = "SYSTem:StopStreamData";

        /// <summary>The command <see cref="ScpiMessageProducer.EnableAdcChannels"/> emits.</summary>
        private const string EnableAdcChannelsCommand = "ENAble:VOLTage:DC";

        /// <summary>
        /// Sends a command, and keeps this device's view of the streaming session in step with it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="Send{T}"/> is public and is a perfectly ordinary way to drive a device — the
        /// example CLI does the whole job that way — but a session driven through it used to be
        /// completely invisible to this class: <see cref="IsStreaming"/> stayed <c>false</c> while
        /// data poured in, and the enabled-channel set stayed empty. That mattered once reconnect
        /// arrived (issue #379). A physical cable pull on the bench recovered the link and then
        /// reported <c>StreamingResumed: false</c>, because as far as Core was concerned nothing had
        /// ever been streaming — while re-initialization had in fact just stopped the stream that
        /// was running. The session looked restored and was not.
        /// </para>
        /// <para>
        /// So the two commands that define a streaming session are recognized here regardless of
        /// which API produced them, and the same state is updated that
        /// <see cref="StartStreaming"/> / <see cref="StopStreaming"/> and
        /// <see cref="EnableChannel"/> would have set. This is the same principle as #409, where
        /// analog <c>IsEnabled</c> is resynced from the device's own reported mask: what Core
        /// believes about a session has to track what is actually true of it.
        /// </para>
        /// <para>
        /// Only these commands are interpreted, and only after the send itself has succeeded.
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
        /// <b>Deliberately outside it.</b> The global DIO enable is one switch for the whole port
        /// rather than a per-channel mask, so a raw <c>DIO:PORt:ENAble</c> carries no information
        /// about <i>which</i> digital channels were wanted and none is inferred. Argument validation
        /// is not replayed either: by the time a command is seen here it has already gone to the
        /// device, and the device is the authority on whether it accepted it.
        /// </para>
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
        private void TrackSessionCommand(string? command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return;
            }

            var trimmed = command.Trim();

            if (trimmed.StartsWith(StopStreamingCommand, StringComparison.OrdinalIgnoreCase))
            {
                IsStreaming = false;
                return;
            }

            if (trimmed.StartsWith(StartStreamingCommand, StringComparison.OrdinalIgnoreCase))
            {
                TrackStreamingStart(trimmed.AsSpan(StartStreamingCommand.Length));
                return;
            }

            if (trimmed.StartsWith(EnableAdcChannelsCommand, StringComparison.OrdinalIgnoreCase))
            {
                TrackAdcEnableMask(trimmed.AsSpan(EnableAdcChannelsCommand.Length));
            }
        }

        /// <summary>
        /// Records a start-streaming command, but only one carrying a rate this device can model.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A command whose argument is missing, unparseable, or outside the device's sampling range
        /// is <b>not</b> treated as the start of a session. Marking one as streaming anyway would be
        /// wrong three times over: the firmware rejects such a command and does not start streaming,
        /// so the flag would not describe the device; <see cref="StreamingFrequency"/> would be left
        /// holding a rate from some earlier session, which a reconnect would then faithfully restore
        /// — resuming at a rate nobody asked for is the silent-wrong-data failure this whole feature
        /// exists to prevent; and a stale <see cref="IsStreaming"/> makes the next legitimate
        /// <see cref="StartStreaming"/> a silent no-op, which is the same stale-flag trap that
        /// issue #118 and the defensive stops scattered through the SD paths already guard against.
        /// </para>
        /// <para>
        /// The existing state is left alone rather than cleared. A device already streaming at a
        /// good rate goes on doing exactly that when the firmware rejects a malformed start, so
        /// <see cref="IsStreaming"/> and <see cref="StreamingFrequency"/> both remain true of it;
        /// forcing them off would swap one inaccuracy for another.
        /// </para>
        /// <para>
        /// Because of this, <see cref="IsStreaming"/> is never <c>true</c> alongside a rate that was
        /// not validated — so session restore has no "streaming at an unknown rate" case to decide
        /// what to do about. The state it replays is always one that was really commanded.
        /// </para>
        /// </remarks>
        private void TrackStreamingStart(ReadOnlySpan<char> argument)
        {
            var rate = argument.Trim();

            // One read of the ceiling, used for both the check and the assignment below. The public
            // StreamingFrequency setter re-reads it and throws when it does not like the value, and
            // MaxSamplingRate is a mutable public property — so validating against one read and
            // then assigning through a setter that takes another would let a concurrent
            // capabilities update throw out of a Send whose command has already gone to the device.
            // Tracking a command must never be able to fail the send that carried it.
            var maxSamplingRate = Math.Max(1, Metadata.Capabilities.MaxSamplingRate);

            if (!int.TryParse(rate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frequency)
                || frequency < 1
                || frequency > maxSamplingRate)
            {
                Trace.WriteLine(
                    $"[{nameof(TrackStreamingStart)}] Ignoring a start-streaming command with an unusable rate "
                    + $"('{rate.ToString()}'); the session state is unchanged.");
                return;
            }

            // Frequency first: anything observing IsStreaming must never catch it true next to a
            // rate belonging to a previous session. Assigned to the backing field, not through the
            // validating setter, for the reason above — the value has just been validated against
            // the same rule.
            _streamingFrequency = frequency;

            if (IsStreaming)
            {
                // A restart while already streaming. The typed API cannot even express this
                // (StartStreaming returns early), so there is no session boundary to re-anchor at
                // and no equivalence to preserve; recording the new rate is all that is warranted.
                return;
            }

            // A session is beginning, so it gets exactly the preparation StartStreaming would have
            // given it. Ordering matches too: the state is ready before the flag flips.
            BeginStreamingSession();
            IsStreaming = true;
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
        private void TrackAdcEnableMask(ReadOnlySpan<char> argument)
        {
            if (!uint.TryParse(argument.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mask))
            {
                return;
            }

            WithChannelsLock(() =>
            {
                foreach (var channel in SnapshotChannels())
                {
                    if (channel.Type != ChannelType.Analog || channel.ChannelNumber > MaxAdcBitmaskChannel)
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

        /// <summary>
        /// The subset of a streaming session that Core owns and can therefore put back: which
        /// channels were enabled, and whether data was flowing.
        /// </summary>
        private sealed class StreamingSessionSnapshot
        {
            public StreamingSessionSnapshot(HashSet<(ChannelType Type, int Number)> enabledChannels, bool wasStreaming)
            {
                EnabledChannels = enabledChannels;
                WasStreaming = wasStreaming;
            }

            /// <summary>
            /// The enabled channels, held by identity rather than by reference: a reconnect can
            /// replace the channel objects, and a device that came back with a different channel
            /// count should restore the intersection rather than fail.
            /// </summary>
            public HashSet<(ChannelType Type, int Number)> EnabledChannels { get; }

            public bool WasStreaming { get; }
        }

        /// <inheritdoc />
        protected override void CaptureSessionSnapshot()
        {
            var enabled = new HashSet<(ChannelType, int)>();
            foreach (var channel in GetChannelsSnapshot())
            {
                if (channel.IsEnabled)
                {
                    enabled.Add((channel.Type, channel.ChannelNumber));
                }
            }

            _sessionSnapshot = new StreamingSessionSnapshot(enabled, IsStreaming);
        }

        /// <summary>
        /// Re-applies the enabled-channel set recorded at the drop and, if the policy says so,
        /// restarts a stream that was interrupted.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The enable set has to be replayed from the snapshot rather than read back off the
        /// channel objects: <see cref="DaqifiDevice.PopulateChannelsFromStatus"/> resyncs analog
        /// <c>IsEnabled</c> from the device's own enabled mask on every status message (#409), so by
        /// the time re-initialization is done the in-memory view reflects the freshly reconnected
        /// device, not the session that was lost.
        /// </para>
        /// <para>
        /// The streaming frequency needs no replay — it is a host-side setting that the drop never
        /// touched — but it does have to reach the device again, which is what the resumed
        /// <see cref="StartStreaming"/> does.
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
            // not necessarily what it had, and the enable commands are set-replace anyway.
            DisableAllChannels();

            var toEnable = new List<IChannel>();
            foreach (var channel in GetChannelsSnapshot())
            {
                if (snapshot.EnabledChannels.Contains((channel.Type, channel.ChannelNumber)))
                {
                    toEnable.Add(channel);
                }
            }

            if (toEnable.Count > 0)
            {
                EnableChannels(toEnable);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var resumeStreaming = snapshot.WasStreaming && options.ResumeStreaming;
            if (resumeStreaming)
            {
                StartStreaming();
            }

            return Task.FromResult(resumeStreaming);
        }

        #endregion

        /// <summary>
        /// The default bounded-buffer capacity (in samples) used by <see cref="StreamSamplesAsync"/>.
        /// </summary>
        public const int DefaultLiveSampleBufferCapacity = 4096;

        private long _droppedLiveSampleCount;

        /// <summary>
        /// Gets the cumulative number of live samples dropped across all <see cref="StreamSamplesAsync"/>
        /// enumerations because a consumer could not keep up with the incoming rate (drop-oldest policy).
        /// A non-zero and growing value means a live consumer is too slow for the current stream rate.
        /// </summary>
        public long DroppedLiveSampleCount => Interlocked.Read(ref _droppedLiveSampleCount);

        /// <summary>
        /// Exposes decoded live samples as an <see cref="IAsyncEnumerable{T}"/> for pull-based
        /// <c>await foreach</c> consumption with cancellation and backpressure — bringing the live path
        /// up to the same async-stream idiom the SD-card and export paths already use. Additive: the
        /// per-channel <see cref="IChannel.SampleReceived"/> and raw-frame events are unaffected.
        /// </summary>
        /// <remarks>
        /// Samples are buffered in a bounded channel with a <b>drop-oldest</b> overflow policy: if the
        /// consumer falls behind, the oldest buffered samples are discarded (memory never grows
        /// unbounded) and <see cref="DroppedLiveSampleCount"/> is incremented — the decode thread that
        /// produces samples is never blocked. Enumeration observes the channels present when it starts;
        /// cancelling <paramref name="cancellationToken"/> ends it promptly (surfaced as
        /// <see cref="OperationCanceledException"/>) and unsubscribes, but does <b>not</b> stop the
        /// device's stream — call <see cref="StopStreaming"/> for that.
        /// </remarks>
        /// <param name="cancellationToken">Ends enumeration when cancelled.</param>
        /// <param name="bufferCapacity">
        /// Bounded buffer capacity; defaults to <see cref="DefaultLiveSampleBufferCapacity"/> when null.
        /// </param>
        /// <returns>An async stream of <see cref="LiveSample"/> (channel + decoded sample).</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="bufferCapacity"/> is less than 1.</exception>
        public async IAsyncEnumerable<LiveSample> StreamSamplesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default,
            int? bufferCapacity = null)
        {
            var capacity = bufferCapacity ?? DefaultLiveSampleBufferCapacity;
            if (capacity < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(bufferCapacity), capacity, "Buffer capacity must be at least 1.");
            }

            var buffer = System.Threading.Channels.Channel.CreateBounded<LiveSample>(
                new BoundedChannelOptions(capacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                },
                _ => Interlocked.Increment(ref _droppedLiveSampleCount));

            void OnSample(object? sender, SampleReceivedEventArgs e) =>
                buffer.Writer.TryWrite(new LiveSample(e.Channel, e.Sample));

            var channels = SnapshotChannels();
            foreach (var channel in channels)
            {
                channel.SampleReceived += OnSample;
            }

            try
            {
                await foreach (var sample in buffer.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    yield return sample;
                }
            }
            finally
            {
                foreach (var channel in channels)
                {
                    channel.SampleReceived -= OnSample;
                }
                buffer.Writer.TryComplete();
            }
        }

        /// <summary>
        /// Handles a streaming data frame: re-raises it for raw-frame consumers (via the base
        /// implementation) and, while streaming, decodes it into per-channel samples that drive
        /// <see cref="IChannel.SampleReceived"/>.
        /// </summary>
        /// <param name="message">The streaming message from the device.</param>
        protected override void OnStreamMessageReceived(DaqifiOutMessage message)
        {
            // Preserve the raw-frame MessageReceived event so existing consumers that hand-demux
            // the protobuf frame keep working unchanged.
            base.OnStreamMessageReceived(message);

            // Only decode into channel samples while an app-driven stream is active. A stray frame
            // that arrives outside a streaming session is still re-raised above but not decoded.
            if (!IsStreaming)
            {
                return;
            }

            try
            {
                DecodeStreamFrame(message);
            }
            catch (Exception ex)
            {
                // A single malformed frame must never tear down the stream or starve other
                // consumers; decoding is best-effort per frame. That isolation stays exactly as it
                // was — the frame is dropped and the loop continues — but it is no longer silent
                // (issue #378): a decode that throws on every frame yields no samples, which used
                // to be indistinguishable from a device sending nothing at all. Both the counter
                // and the (throttled) event are observation only; neither changes what happens to
                // this frame or the next one.
                Interlocked.Increment(ref _decodeFailureCount);
                RaiseDeviceError(DeviceErrorSource.StreamDecode, ex);
            }
        }

        /// <summary>
        /// Decodes a streaming frame into per-channel samples: selects the active channels in
        /// device order, chooses the correct value source (USB pre-scaled float vs. WiFi raw ADC
        /// count scaled via calibration), unpacks digital bits, and pushes a sample to each channel.
        /// </summary>
        /// <param name="message">The streaming message to decode.</param>
        private void DecodeStreamFrame(DaqifiOutMessage message)
        {
            var hasFloat = message.AnalogInDataFloat.Count > 0;
            var hasRawAnalog = message.AnalogInData.Count > 0;
            var hasDigital = message.DigitalData.Length > 0;

            if (!hasFloat && !hasRawAnalog && !hasDigital)
            {
                return;
            }

            // Snapshot channels once: the consumer thread that repopulates channels is the same
            // thread that runs this decode, so the structure is stable for the duration of the call.
            var channels = SnapshotChannels();

            // Suppress the firmware's malformed warmup frame at stream start (issue #351): its fast
            // streaming encoder can emit a leading analog-bearing frame with fewer values than the
            // enabled channel mask. Only the malformed *analog* values are withheld — a combined
            // frame's digital payload is still decoded, and the frame's (normal one-period)
            // timestamp still anchors the session clock, so digital state/edges are not lost. Only
            // leading short frames are suppressed (mid-stream short frames stay best-effort mapped),
            // bounded so a genuinely short stream is never withheld indefinitely.
            var suppressWarmupAnalog = false;
            if (_awaitingFirstFullAnalogFrame && (hasFloat || hasRawAnalog))
            {
                var analogValueCount = hasFloat ? message.AnalogInDataFloat.Count : message.AnalogInData.Count;
                var enabledAnalogCount = CountEnabledAnalogChannels(channels);
                if (enabledAnalogCount > 0 && analogValueCount < enabledAnalogCount
                    && _suppressedWarmupFrameCount < MaxSuppressedWarmupFrames)
                {
                    _suppressedWarmupFrameCount++;
                    suppressWarmupAnalog = true;
                }
                else
                {
                    _awaitingFirstFullAnalogFrame = false;
                }
            }

            // Reconstruct a host timestamp from the device tick counter (rollover-aware) and carry
            // the raw device tick value through to each decoded sample.
            var deviceTimestamp = message.MsgTimeStamp;
            var timestampResult = _timestampProcessor.ProcessTimestamp(StreamTimestampKey, deviceTimestamp);
            var hostTimestamp = timestampResult.Timestamp;

            // Flag dropped samples from the device-clock delta (immune to host arrival jitter).
            // Isolate subscriber exceptions (see RaiseGapDetected) so a throwing GapDetected handler
            // cannot skip the per-channel decode below — which the caller's broad catch would then
            // silently drop.
            if (_gapDetector.IsGap(timestampResult.SecondsBetweenMessages))
            {
                RaiseGapDetected(new TimestampGapEventArgs(
                    hostTimestamp, timestampResult.SecondsBetweenMessages, deviceTimestamp));
            }

            if ((hasFloat || hasRawAnalog) && !suppressWarmupAnalog)
            {
                DecodeAnalog(message, channels, hostTimestamp, deviceTimestamp, hasFloat);
            }

            if (hasDigital)
            {
                DecodeDigital(message, channels, hostTimestamp, deviceTimestamp);
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
                Trace.WriteLine($"[{nameof(GapDetected)}] Subscriber threw: {ex}");
            }
        }

        /// <summary>
        /// Maps a frame's analog values to the enabled analog channels, in ascending channel order.
        /// USB firmware streams pre-scaled floats (used directly); WiFi firmware streams raw ADC
        /// counts (scaled per channel via <see cref="IAnalogChannel.GetScaledValue"/>).
        /// </summary>
        private static int CountEnabledAnalogChannels(IReadOnlyList<IChannel> channels)
        {
            var count = 0;
            foreach (var channel in channels)
            {
                if (channel.IsEnabled && channel is IAnalogChannel)
                {
                    count++;
                }
            }
            return count;
        }

        private static void DecodeAnalog(
            DaqifiOutMessage message,
            IReadOnlyList<IChannel> channels,
            DateTime hostTimestamp,
            uint deviceTimestamp,
            bool hasFloat)
        {
            // The device streams one value per enabled analog channel, ordered by channel number,
            // not by activation order — so re-derive that ordering here.
            var activeAnalog = new List<IAnalogChannel>();
            foreach (var channel in channels)
            {
                if (channel.IsEnabled && channel is IAnalogChannel analog)
                {
                    activeAnalog.Add(analog);
                }
            }
            activeAnalog.Sort((a, b) => a.ChannelNumber.CompareTo(b.ChannelNumber));

            var dataCount = hasFloat ? message.AnalogInDataFloat.Count : message.AnalogInData.Count;
            var count = Math.Min(dataCount, activeAnalog.Count);

            for (var i = 0; i < count; i++)
            {
                var channel = activeAnalog[i];
                double scaled;
                int? raw;

                if (hasFloat)
                {
                    // USB firmware already scaled to volts; no raw ADC count is available.
                    scaled = message.AnalogInDataFloat[i];
                    raw = null;
                }
                else
                {
                    // WiFi firmware sent a raw ADC count; apply this channel's calibration.
                    var rawValue = message.AnalogInData[i];
                    scaled = channel.GetScaledValue(rawValue);
                    raw = rawValue;
                }

                channel.SetActiveSample(new DataSample(hostTimestamp, scaled, raw, deviceTimestamp));
            }
        }

        /// <summary>
        /// Unpacks a frame's digital byte(s) into per-channel high/low samples for the enabled
        /// digital input channels. The firmware streams the whole DIO port as a raw pin-state
        /// snapshot (the wire-level DIO enable is global, not per pin), so a channel's bit
        /// position is its channel number — bit <c>n</c> lives at byte <c>n / 8</c>, bit
        /// <c>n % 8</c> (LSB first) — independent of which channels the client has enabled.
        /// Output-direction channels are not sampled (their state is client-driven via
        /// <see cref="SetDioValue"/>). Channels whose number lies beyond the payload get no
        /// sample rather than a bogus "low" reading.
        /// </summary>
        private static void DecodeDigital(
            DaqifiOutMessage message,
            IReadOnlyList<IChannel> channels,
            DateTime hostTimestamp,
            uint deviceTimestamp)
        {
            var digitalData = message.DigitalData;
            var bitCount = digitalData.Length * 8;

            foreach (var channel in channels)
            {
                if (!channel.IsEnabled || channel.Type != ChannelType.Digital)
                {
                    continue;
                }

                // Only input-direction channels carry a meaningful streamed reading.
                if (channel.Direction != ChannelDirection.Input)
                {
                    continue;
                }

                var bitIndex = channel.ChannelNumber;
                if (bitIndex >= bitCount)
                {
                    continue;
                }

                var bit = (digitalData[bitIndex / 8] & (1 << (bitIndex % 8))) != 0;

                channel.SetActiveSample(
                    new DataSample(hostTimestamp, bit ? 1.0 : 0.0, bit ? 1 : 0, deviceTimestamp));
            }
        }

        /// <summary>
        /// The maximum analog channel number that can be encoded in the ADC enable bitmask.
        /// The mask is a 32-bit value (<c>1u &lt;&lt; ChannelNumber</c>), so channel numbers must be 0-31.
        /// </summary>
        private const int MaxAdcBitmaskChannel = 31;

        /// <inheritdoc />
        public void EnableChannel(IChannel channel)
        {
            ArgumentNullException.ThrowIfNull(channel);
            SetChannelsEnabled(new[] { channel }, enabled: true);
        }

        /// <inheritdoc />
        public void EnableChannels(IEnumerable<IChannel> channels)
        {
            ArgumentNullException.ThrowIfNull(channels);
            SetChannelsEnabled(channels as IReadOnlyList<IChannel> ?? channels.ToList(), enabled: true);
        }

        /// <inheritdoc />
        public void DisableChannel(IChannel channel)
        {
            ArgumentNullException.ThrowIfNull(channel);
            SetChannelsEnabled(new[] { channel }, enabled: false);
        }

        /// <inheritdoc />
        public void DisableAllChannels()
        {
            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            (bool HasChannels, uint Mask) adcMask = default;
            (bool HasChannels, bool AnyEnabled) dioState = default;

            // Mutate and derive the outbound masks in one critical section — see the matching
            // comment in SetChannelsEnabled (#409) for why the gap matters.
            WithChannelsLock(() =>
            {
                foreach (var channel in SnapshotChannels())
                {
                    channel.IsEnabled = false;
                }

                adcMask = ComputeAdcEnableMask();
                dioState = ComputeDioEnableState();
            });

            // Push the cleared state for whichever channel types this device actually has.
            if (adcMask.HasChannels)
            {
                Send(ScpiMessageProducer.EnableAdcChannels(adcMask.Mask.ToString(CultureInfo.InvariantCulture)));
            }

            if (dioState.HasChannels)
            {
                Send(dioState.AnyEnabled
                    ? ScpiMessageProducer.EnableDioPorts()
                    : ScpiMessageProducer.DisableDioPorts());
            }
        }

        /// <inheritdoc />
        public void SetDioDirection(IChannel channel, ChannelDirection direction)
        {
            // Argument validation precedes the connection (state) check so misuse surfaces
            // the same exception type regardless of connection state.
            ArgumentNullException.ThrowIfNull(channel);

            if (channel.Type != ChannelType.Digital)
            {
                throw new ArgumentException("Direction can only be set on digital channels.", nameof(channel));
            }

            if (direction != ChannelDirection.Input && direction != ChannelDirection.Output)
            {
                throw new ArgumentOutOfRangeException(nameof(direction), direction, "Direction must be Input or Output.");
            }

            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            EnsureChannelBelongs(channel);

            channel.Direction = direction;
            Send(ScpiMessageProducer.SetDioPortDirection(
                channel.ChannelNumber,
                direction == ChannelDirection.Output ? 1 : 0));
        }

        /// <inheritdoc />
        public void SetDioValue(IChannel channel, bool value)
        {
            ArgumentNullException.ThrowIfNull(channel);

            // Gate on Type (matching SetDioDirection) rather than the IDigitalChannel interface,
            // so both DIO methods accept the same set of channels. The SCPI command only needs
            // the channel number; OutputValue mirroring is best-effort local bookkeeping.
            if (channel.Type != ChannelType.Digital)
            {
                throw new ArgumentException("A digital output value can only be set on digital channels.", nameof(channel));
            }

            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            EnsureChannelBelongs(channel);

            if (channel is IDigitalChannel digitalChannel)
            {
                digitalChannel.OutputValue = value;
            }

            Send(ScpiMessageProducer.SetDioPortState(channel.ChannelNumber, value ? 1 : 0));
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
        public int PwmFrequencyHz { get; private set; } = DefaultPwmFrequencyHz;

        /// <summary>
        /// The PWM frequency actually sent to the device this connection, or <c>null</c> if none has
        /// been sent yet (also reset to <c>null</c> on disconnect). Distinct from
        /// <see cref="PwmFrequencyHz"/>, which carries a session default before anything is sent —
        /// this drives the skip-if-unchanged guard so a fresh connection always sends. See #345.
        /// </summary>
        private int? _lastSentPwmFrequencyHz;

        /// <inheritdoc />
        public void SetPwmEnabled(IChannel channel, bool enabled)
        {
            ArgumentNullException.ThrowIfNull(channel);

            if (channel.Type != ChannelType.Digital)
            {
                throw new ArgumentException("PWM can only be controlled on digital channels.", nameof(channel));
            }

            // Enabling PWM on a non-capable channel must be blocked here: the firmware flags the
            // channel PWM-active before its capability check fails and never rolls that back,
            // leaving the channel dead to digital writes. Disabling is that state's only recovery
            // command, so it is accepted on any digital channel.
            if (enabled && channel is not IDigitalChannel { IsPwmCapable: true })
            {
                throw new ArgumentException(
                    $"Channel {channel.ChannelNumber} does not support PWM. PWM-capable channels: {PwmCapableChannelList}.",
                    nameof(channel));
            }

            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            EnsureChannelBelongs(channel);

            if (channel is IDigitalChannel digitalChannel)
            {
                digitalChannel.IsPwmEnabled = enabled;
                if (!enabled)
                {
                    // Disabling PWM leaves the pin high-impedance and the firmware zeroes its
                    // stored output value; mirror that so local state doesn't claim a driven level.
                    // Direction is intentionally left as-is: the firmware keeps the channel's
                    // stored direction and re-applies it (resuming driving) on the next state or
                    // direction write, or on the next streaming tick — verified on hardware.
                    digitalChannel.OutputValue = false;
                }
            }

            Send(ScpiMessageProducer.SetPwmChannelEnabled(channel.ChannelNumber, enabled));
        }

        /// <inheritdoc />
        public void SetPwmDutyCycle(IChannel channel, int dutyCyclePercent)
        {
            ArgumentNullException.ThrowIfNull(channel);

            if (channel.Type != ChannelType.Digital)
            {
                throw new ArgumentException("PWM can only be controlled on digital channels.", nameof(channel));
            }

            if (channel is not IDigitalChannel { IsPwmCapable: true })
            {
                throw new ArgumentException(
                    $"Channel {channel.ChannelNumber} does not support PWM. PWM-capable channels: {PwmCapableChannelList}.",
                    nameof(channel));
            }

            // Duty 0 is rejected rather than forwarded: the firmware stores it but never writes
            // the compare register, so the output keeps toggling at the previous duty while the
            // stored value claims 0. Stopping the output is SetPwmEnabled(channel, false).
            if (dutyCyclePercent is < 1 or > 100)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(dutyCyclePercent), dutyCyclePercent,
                    "Duty cycle must be 1-100 percent. To stop the output, use SetPwmEnabled(channel, false).");
            }

            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            EnsureChannelBelongs(channel);

            if (channel is IDigitalChannel digitalChannel)
            {
                digitalChannel.PwmDutyCyclePercent = dutyCyclePercent;
            }

            Send(ScpiMessageProducer.SetPwmChannelDutyCycle(channel.ChannelNumber, dutyCyclePercent));
        }

        /// <inheritdoc />
        public void SetPwmFrequency(int frequencyHz)
        {
            if (frequencyHz is < MinPwmFrequencyHz or > MaxPwmFrequencyHz)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(frequencyHz), frequencyHz,
                    $"PWM frequency must be {MinPwmFrequencyHz}-{MaxPwmFrequencyHz} Hz.");
            }

            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            // Skip the redundant round-trip when the device already has this frequency (from a
            // send earlier this connection). The cache is cleared on disconnect so a fresh
            // connection always sends. PwmFrequencyHz still reflects the commanded value. See #345.
            if (frequencyHz == _lastSentPwmFrequencyHz)
            {
                return;
            }

            // The SCPI command is addressed to a channel, but the firmware drives all PWM from
            // one shared timer and applies the frequency to every channel. Channel 0 is used as
            // the address because it is PWM-capable on all supported hardware.
            Send(ScpiMessageProducer.SetPwmChannelFrequency(0, frequencyHz));
            _lastSentPwmFrequencyHz = frequencyHz;
            PwmFrequencyHz = frequencyHz;
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
        {
            if (name is null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            if (!ScpiMessageProducer.IsFriendlyNameValid(name))
            {
                throw new ArgumentException(
                    $"Device name must be 1-{ScpiMessageProducer.MaxFriendlyNameLength} printable ASCII characters and cannot contain '\"' or '\\'.",
                    nameof(name));
            }

            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            cancellationToken.ThrowIfCancellationRequested();

            Send(ScpiMessageProducer.SetDeviceName(name));
            Send(ScpiMessageProducer.SaveDeviceName);
            Metadata.FriendlyName = name;

            return Task.CompletedTask;
        }

        /// <summary>
        /// Comma-separated PWM-capable channel numbers for error messages, derived from this
        /// device's channel collection.
        /// </summary>
        private string PwmCapableChannelList
        {
            get
            {
                var capable = new List<int>();
                foreach (var ch in SnapshotChannels())
                {
                    if (ch is IDigitalChannel { IsPwmCapable: true })
                    {
                        capable.Add(ch.ChannelNumber);
                    }
                }
                capable.Sort();
                return capable.Count > 0 ? string.Join(", ", capable) : "none on this device";
            }
        }

        /// <inheritdoc />
        public void SetAnalogOutput(int channelNumber, double voltage)
        {
            if (channelNumber < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(channelNumber), channelNumber, "Channel number cannot be negative.");
            }

            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            // Analog-output (DAC) channels are addressed by number; they are not part of the
            // populated Channels collection (PopulateChannelsFromStatus creates analog *input*
            // channels only). Stage the level, then latch it.
            Send(ScpiMessageProducer.SetAnalogOutputVoltage(channelNumber, voltage));
            Send(ScpiMessageProducer.UpdateDacOutputs);
        }

        /// <inheritdoc />
        public void Reboot()
        {
            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            Send(ScpiMessageProducer.RebootDevice);

            // The device drops its link while restarting, so tear down the local
            // connection rather than leaving a stale one that reports Connected.
            Disconnect();
        }

        /// <inheritdoc />
        public void SaveAdcCalibration()
        {
            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            Send(ScpiMessageProducer.SaveAdcCalibration);
        }

        /// <inheritdoc />
        public void LoadAdcCalibration()
        {
            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            Send(ScpiMessageProducer.LoadAdcCalibration);
        }

        /// <inheritdoc />
        public void SetAdcCalibrationSlope(int channelNumber, double calM)
        {
            if (channelNumber < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(channelNumber), channelNumber, "Channel number cannot be negative.");
            }

            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            Send(ScpiMessageProducer.SetAdcCalibrationSlope(channelNumber, calM));
        }

        /// <inheritdoc />
        public void SetAdcCalibrationOffset(int channelNumber, double calB)
        {
            if (channelNumber < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(channelNumber), channelNumber, "Channel number cannot be negative.");
            }

            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            Send(ScpiMessageProducer.SetAdcCalibrationOffset(channelNumber, calB));
        }

        /// <inheritdoc />
        public void SaveFactoryAdcCalibration()
        {
            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            Send(ScpiMessageProducer.SaveFactoryAdcCalibration);
        }

        /// <inheritdoc />
        public void LoadFactoryAdcCalibration()
        {
            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            Send(ScpiMessageProducer.LoadFactoryAdcCalibration);
        }

        /// <inheritdoc />
        public void UseAdcCalibration(int bank)
        {
            if (bank is < 0 or > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(bank), bank, "Calibration bank must be 0 (factory) or 1 (user).");
            }

            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            Send(ScpiMessageProducer.UseAdcCalibration(bank));
        }

        /// <inheritdoc />
        public void SaveVoltagePrecision()
        {
            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            Send(ScpiMessageProducer.SaveVoltagePrecision);
        }

        /// <inheritdoc />
        public void LoadVoltagePrecision()
        {
            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            Send(ScpiMessageProducer.LoadVoltagePrecision);
        }

        /// <summary>
        /// Sets the enabled state for a set of channels, then sends one device command per affected
        /// channel type (the ADC enable bitmask for analog, the global DIO enable for digital).
        /// Validation runs before any mutation so an invalid entry leaves device state untouched.
        /// </summary>
        private void SetChannelsEnabled(IReadOnlyList<IChannel> channels, bool enabled)
        {
            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            // Validate everything up front so a bad entry can't leave a partially-applied state.
            foreach (var channel in channels)
            {
                if (channel is null)
                {
                    throw new ArgumentException("The channel collection contains a null entry.", nameof(channels));
                }

                EnsureChannelBelongs(channel);
            }

            var touchedAnalog = false;
            var touchedDigital = false;
            var adcMask = (HasChannels: false, Mask: 0u);
            var dioState = (HasChannels: false, AnyEnabled: false);

            // Mutate IsEnabled and derive the outbound masks from it in one critical section, so
            // a status frame resyncing analog IsEnabled from the device (#409) on the consumer
            // thread cannot interleave between the mutation and the read that computes the mask —
            // which would otherwise send a mask reflecting a value the status frame is about to
            // overwrite, silently failing to apply the requested enable/disable on the device.
            WithChannelsLock(() =>
            {
                foreach (var channel in channels)
                {
                    channel.IsEnabled = enabled;

                    if (channel.Type == ChannelType.Analog)
                    {
                        touchedAnalog = true;
                    }
                    else if (channel.Type == ChannelType.Digital)
                    {
                        touchedDigital = true;
                    }
                }

                if (touchedAnalog)
                {
                    adcMask = ComputeAdcEnableMask();
                }

                if (touchedDigital)
                {
                    dioState = ComputeDioEnableState();
                }
            });

            if (touchedAnalog && adcMask.HasChannels)
            {
                Send(ScpiMessageProducer.EnableAdcChannels(adcMask.Mask.ToString(CultureInfo.InvariantCulture)));
            }

            if (touchedDigital && dioState.HasChannels)
            {
                Send(dioState.AnyEnabled
                    ? ScpiMessageProducer.EnableDioPorts()
                    : ScpiMessageProducer.DisableDioPorts());
            }
        }

        /// <summary>
        /// Computes the ADC enable bitmask over all currently-enabled analog channels. Must be
        /// called under <see cref="DaqifiDevice.WithChannelsLock{T}"/> alongside any IsEnabled
        /// mutation it should reflect (#409) — see <see cref="SetChannelsEnabled"/>.
        /// </summary>
        private (bool HasChannels, uint Mask) ComputeAdcEnableMask()
        {
            uint mask = 0;
            var hasAnalogChannels = false;

            foreach (var channel in SnapshotChannels())
            {
                if (channel.Type != ChannelType.Analog)
                {
                    continue;
                }

                hasAnalogChannels = true;

                if (!channel.IsEnabled)
                {
                    continue;
                }

                if (channel.ChannelNumber > MaxAdcBitmaskChannel)
                {
                    throw new InvalidOperationException(
                        $"Analog channel number {channel.ChannelNumber} exceeds the maximum ({MaxAdcBitmaskChannel}) that can be encoded in the ADC enable bitmask.");
                }

                mask |= 1u << channel.ChannelNumber;
            }

            return (hasAnalogChannels, mask);
        }

        /// <summary>
        /// Recomputes the ADC enable bitmask over all currently-enabled analog channels and sends it.
        /// Does nothing when the device has no analog channels. The firmware treats the value as a
        /// set-replace, so the full mask of enabled analog channels is sent every time.
        /// </summary>
        private void SendAdcEnableMask()
        {
            var (hasAnalogChannels, mask) = WithChannelsLock(ComputeAdcEnableMask);

            if (!hasAnalogChannels)
            {
                return;
            }

            Send(ScpiMessageProducer.EnableAdcChannels(mask.ToString(CultureInfo.InvariantCulture)));
        }

        /// <summary>
        /// Computes whether any digital channel is enabled. Must be called under
        /// <see cref="DaqifiDevice.WithChannelsLock{T}"/> alongside any IsEnabled mutation it
        /// should reflect — see <see cref="SetChannelsEnabled"/>.
        /// </summary>
        private (bool HasChannels, bool AnyEnabled) ComputeDioEnableState()
        {
            var hasDigitalChannels = false;
            var anyEnabled = false;

            foreach (var channel in SnapshotChannels())
            {
                if (channel.Type != ChannelType.Digital)
                {
                    continue;
                }

                hasDigitalChannels = true;

                if (channel.IsEnabled)
                {
                    anyEnabled = true;
                }
            }

            return (hasDigitalChannels, anyEnabled);
        }

        /// <summary>
        /// Sends the global DIO enable command reflecting whether any digital channel is enabled.
        /// Does nothing when the device has no digital channels. The firmware exposes only a global
        /// DIO enable, so per-channel digital enabling is collapsed to this aggregate state.
        /// </summary>
        private void SendDioEnableState()
        {
            var (hasDigitalChannels, anyEnabled) = WithChannelsLock(ComputeDioEnableState);

            if (!hasDigitalChannels)
            {
                return;
            }

            Send(anyEnabled
                ? ScpiMessageProducer.EnableDioPorts()
                : ScpiMessageProducer.DisableDioPorts());
        }

        /// <summary>
        /// Throws when the supplied channel is not part of this device's populated channel collection,
        /// which would mean mutating it could not affect the device-level enable state.
        /// </summary>
        private void EnsureChannelBelongs(IChannel channel)
        {
            if (!SnapshotChannels().Contains(channel))
            {
                throw new ArgumentException("The specified channel does not belong to this device.", nameof(channel));
            }
        }

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

        #region IDeviceOperationHost

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

        void IDeviceOperationHost.Send<T>(IOutboundMessage<T> message) => Send(message);

#pragma warning disable CA1068 // Matches the seam it forwards to.
        Task<IReadOnlyList<string>> IDeviceOperationHost.ExecuteTextCommandAsync(
            Action setupAction,
            int responseTimeoutMs,
            int completionTimeoutMs,
            CancellationToken cancellationToken,
            Func<CancellationToken, Task>? prepareAsync,
            Func<Task>? finalizeAsync)
            => ExecuteTextCommandAsync(
                setupAction, responseTimeoutMs, completionTimeoutMs, cancellationToken, prepareAsync, finalizeAsync);
#pragma warning restore CA1068

        Task IDeviceOperationHost.ExecuteRawCaptureAsync(
            Func<Stream, CancellationToken, Task> rawAction,
            CancellationToken cancellationToken)
            => ExecuteRawCaptureAsync(rawAction, cancellationToken);

        void IDeviceOperationHost.EnsureSupported(DeviceFeature feature) => EnsureSupported(feature);

        FeatureNotSupportedException IDeviceOperationHost.CreateFeatureNotSupportedException(DeviceFeature feature)
            => CreateFeatureNotSupportedException(feature);

        TimeSpan IDeviceOperationHost.SdCardDownloadTimeout => SdCardDownloadTimeout;

        TimeSpan IDeviceOperationHost.SdCardTransferIdleTimeout => SdCardTransferIdleTimeout;

        void IDeviceOperationHost.RaiseLowSdSpaceWarning(LowSdSpaceWarningEventArgs e) => OnLowSdSpaceWarning(e);

        #endregion
    }
}
