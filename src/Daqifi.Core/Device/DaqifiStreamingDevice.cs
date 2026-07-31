using Daqifi.Core.Channel;
using Daqifi.Core.Communication;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device.Diagnostics;
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
    public class DaqifiStreamingDevice : DaqifiDevice, IStreamingDevice, INetworkConfigurable, ISdCardOperations, ILanChipInfoProvider, IDeviceDiagnostics
    {
        /// <summary>
        /// The delay in milliseconds to wait for the WiFi module to restart after applying configuration.
        /// </summary>
        private const int WIFI_MODULE_RESTART_DELAY_MS = 2000;

        /// <summary>
        /// The delay in milliseconds to wait after switching between LAN and SD card interfaces.
        /// The SD card and LAN share the SPI bus, so a settle period is needed for the device
        /// firmware to complete the interface switch before sending further commands.
        /// </summary>
        private const int SD_INTERFACE_SETTLE_DELAY_MS = 100;

        /// <summary>
        /// Maximum number of retry attempts for SD card list operations that receive transient
        /// SCPI errors (e.g., -200 Execution error) due to interface-switch timing.
        /// </summary>
        private const int SD_LIST_MAX_RETRIES = 1;

        /// <summary>
        /// Inactivity window that ends the SD listing text exchange, in milliseconds.
        /// </summary>
        /// <remarks>
        /// Deliberately longer than the 250ms default. The listing is only accepted once its
        /// end-of-listing terminator has been seen (see <see cref="GetSdCardFilesAsync"/>), and the
        /// terminator can trail the last listing line by more than the default window — the firmware
        /// walks the directory tree between chunks, and a congested WiFi link adds its own gaps. With
        /// the default, a merely-slow terminator would read as a missing one and fail a listing that
        /// was about to complete.
        /// </remarks>
        private const int SD_LIST_COMPLETION_TIMEOUT_MS = 1000;

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
        /// libscpi's <c>SCPI_ERROR_UNDEFINED_HEADER</c> — the code the firmware returns for a
        /// command it doesn't recognize (e.g. a command that postdates the connected firmware).
        /// This is the wire-level signal behind the <see cref="FeatureNotSupportedException"/>
        /// backstop (ADR 0001, docs/adr/0001-firmware-feature-gating.md).
        /// </summary>
        private const int ScpiErrorCodeUndefinedHeader = -113;

        private bool _isLoggingToSdCard;
        private IReadOnlyList<SdCardFileInfo> _sdCardFiles = Array.Empty<SdCardFileInfo>();

        /// <summary>
        /// Admits one SD download at a time. A download that hits its deadline is ABANDONED, not
        /// stopped — its worker can still be parked in native I/O holding the transport stream —
        /// so the gate is released only when that worker actually finishes, however long that
        /// takes. Without it, a caller retrying against a device that stays wedged (an "import
        /// all" loop, say) would start a second reader on the same stream, which is the framing
        /// corruption <see cref="DaqifiDevice"/> already refuses to risk when restarting the
        /// protobuf consumer, and would stack another permanently blocked thread each time (#399).
        /// </summary>
        /// <remarks>
        /// Deliberately not disposed: we only ever call <see cref="SemaphoreSlim.Wait(int)"/> and
        /// <see cref="SemaphoreSlim.Release()"/>, never <see cref="SemaphoreSlim.AvailableWaitHandle"/>,
        /// so there is no handle to release — and an abandoned worker may release this long after
        /// the device is disposed, which would otherwise fault a continuation nobody observes.
        /// </remarks>
        private readonly SemaphoreSlim _sdDownloadGate = new(1, 1);

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
        public bool IsLoggingToSdCard => _isLoggingToSdCard;

        /// <summary>
        /// Gets a value indicating whether the device is connected over USB (serial transport).
        /// SD card file downloads require a USB connection because the SD card and WiFi/LAN share the SPI bus.
        /// </summary>
        public virtual bool IsUsbConnection => Transport is SerialStreamTransport;

        /// <summary>
        /// Gets the most recently retrieved list of files on the SD card.
        /// </summary>
        public IReadOnlyList<SdCardFileInfo> SdCardFiles => _sdCardFiles;

        /// <inheritdoc />
        public event EventHandler<LowSdSpaceWarningEventArgs>? LowSdSpaceWarning;

        /// <summary>
        /// Raised while streaming when the device-clock delta between two consecutive frames
        /// indicates dropped samples (a real gap in the device's stream, distinct from host-side
        /// arrival jitter). Fires once per detected gap, on the decode thread, carrying the outage
        /// duration and the timestamp of the first frame after the gap. See <see cref="TimestampGapDetector"/>.
        /// </summary>
        public event EventHandler<TimestampGapEventArgs>? GapDetected;

        private readonly NetworkConfiguration _networkConfiguration = new NetworkConfiguration();

        /// <summary>
        /// Gets a copy of the current network configuration.
        /// </summary>
        /// <remarks>
        /// Returns a clone to prevent external modification. Use <see cref="UpdateNetworkConfigurationAsync"/>
        /// to change the device's network configuration.
        /// </remarks>
        public NetworkConfiguration NetworkConfiguration => _networkConfiguration.Clone();

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
        /// </remarks>
        /// <param name="cancellationToken">A cancellation token to observe while initializing.</param>
        /// <returns>A task representing the asynchronous initialization operation.</returns>
        /// <exception cref="ScpiInitializationErrorException">
        /// Thrown when the device returns a SCPI error while setting the stream interface to USB
        /// that persists after an internal retry. A common trigger is the firmware rejecting the
        /// command because it still has the interface set from a prior WiFi-streaming session,
        /// within the tight response window right after connect.
        /// </exception>
        protected override async Task OnDeviceInitializingAsync(CancellationToken cancellationToken)
        {
            if (!IsUsbConnection)
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

                if (!ContainsScpiError(lines))
                {
                    return;
                }
            }

            var lastScpiError = lines.LastOrDefault(IsScpiErrorLine)?.Trim();
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

            IsStreaming = true;
            Send(ScpiMessageProducer.StartStreaming(StreamingFrequency));
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
        /// Everything else passes through untouched. The global DIO enable is deliberately not
        /// tracked: it is one switch for the whole port rather than a per-channel mask, so it
        /// carries no information about <i>which</i> digital channels a caller wanted.
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

            if (!int.TryParse(rate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frequency)
                || frequency < 1
                || frequency > Math.Max(1, Metadata.Capabilities.MaxSamplingRate))
            {
                Trace.WriteLine(
                    $"[{nameof(TrackStreamingStart)}] Ignoring a start-streaming command with an unusable rate "
                    + $"('{rate.ToString()}'); the session state is unchanged.");
                return;
            }

            // Frequency first: anything observing IsStreaming must never catch it true next to a
            // rate belonging to a previous session.
            StreamingFrequency = frequency;
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

        /// <summary>
        /// Updates the device network configuration with the specified settings.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Supported over any transport, including WiFi/TCP. The settings are staged, persisted to
        /// NVM, and only then applied, so the configuration is durable before the applying restart
        /// can disturb the control connection (#352).
        /// </para>
        /// <para>
        /// <b>Over a WiFi/TCP control connection this method is expected to drop the connection.</b>
        /// Applying the settings restarts the WiFi module, and when the new configuration points at
        /// a different network the device necessarily leaves the one carrying the control link.
        /// That is normal: the configuration has already been saved at that point. Callers should
        /// treat the device as disconnected once this method returns over WiFi and rediscover or
        /// reconnect on the new network — typically at a new address.
        /// </para>
        /// </remarks>
        /// <param name="configuration">The new network configuration to apply.</param>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="configuration"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when an unsupported WiFi mode or security type is specified.</exception>
        /// <exception cref="OperationCanceledException">
        /// Thrown when the operation is canceled <b>before</b> the configuration is committed to the
        /// device. Once the save and apply have been dispatched the operation always completes
        /// successfully: cancelling during the restart wait ends the wait early instead of failing,
        /// because the device has already persisted and applied the new settings.
        /// </exception>
        public async Task UpdateNetworkConfigurationAsync(NetworkConfiguration configuration, CancellationToken cancellationToken = default)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            // Stop streaming if active
            if (IsStreaming)
            {
                StopStreaming();
            }

            // Set WiFi mode
            switch (configuration.Mode)
            {
                case WifiMode.ExistingNetwork:
                    Send(ScpiMessageProducer.SetNetworkWifiModeExisting);
                    break;
                case WifiMode.SelfHosted:
                    Send(ScpiMessageProducer.SetNetworkWifiModeSelfHosted);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(configuration), configuration.Mode, "Unsupported WiFi mode.");
            }

            // Set SSID
            Send(ScpiMessageProducer.SetNetworkWifiSsid(configuration.Ssid));

            // Set security type and password
            switch (configuration.SecurityType)
            {
                case WifiSecurityType.None:
                    Send(ScpiMessageProducer.SetNetworkWifiSecurityOpen);
                    break;
                case WifiSecurityType.WpaPskPhrase:
                    Send(ScpiMessageProducer.SetNetworkWifiSecurityWpa);
                    Send(ScpiMessageProducer.SetNetworkWifiPassword(configuration.Password));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(configuration), configuration.SecurityType, "Unsupported WiFi security type.");
            }

            // Stage static IP fields (firmware writes these into the runtime
            // WiFi settings that ApplyNetworkLan consumes). Skip any field the
            // caller left null so DHCP-only callers see no behavior change.
            if (configuration.StaticIP != null)
            {
                Send(ScpiMessageProducer.SetLanAddress(configuration.StaticIP));
            }
            if (configuration.SubnetMask != null)
            {
                Send(ScpiMessageProducer.SetLanMask(configuration.SubnetMask));
            }
            if (configuration.Gateway != null)
            {
                Send(ScpiMessageProducer.SetLanGateway(configuration.Gateway));
            }

            // Stage the LAN interface state alongside the credentials above. LAN:ENAbled writes
            // isEnabled into the same runtime settings struct the SET commands populate and does
            // not restart anything itself, so it belongs before the save (to be persisted) and
            // before the apply (the firmware only fires a module REINIT when isEnabled is set).
            // This deliberately does NOT call PrepareLanInterface() — that is the transport-aware
            // SD-operation restore, which leaves the LAN alone over WiFi (where #598/#599 keep it
            // up). Here the LAN enable is unconditional: reconfiguration owns the LAN state.
            Send(ScpiMessageProducer.DisableStorageSd);
            Send(ScpiMessageProducer.EnableNetworkLan);

            // Cancellation boundary. This is the last point where abandoning still avoids the two
            // things that matter: nothing has been persisted (no LAN:SAVE) and no module restart
            // has been triggered (no LAN:APPLY), so the device keeps serving the network
            // configuration it already had. Past the save below it has committed, and cancellation
            // stops being a way out.
            //
            // This is deliberately NOT a side-effect-free point. The staged credentials, the LAN
            // enable flag and the SD disable above have all reached the device's runtime state, and
            // a later LAN:APPLY from any caller would pick up those staged values. No side-effect-
            // free abort exists once the sequence has begun — only the check at the top of this
            // method precedes every Send.
            cancellationToken.ThrowIfCancellationRequested();

            // Persist BEFORE applying (#352). LAN:SAVE copies the staged runtime settings straight
            // to NVM; it does NOT require them to have been applied first. Sending it here — while
            // the control link is still guaranteed alive — is what makes the reconfiguration
            // durable regardless of what the apply below does to the connection.
            Send(ScpiMessageProducer.SaveNetworkLan);

            // Apply last: this restarts the WiFi module. Over a WiFi/TCP control connection that
            // restart necessarily tears down the link — inherent to moving the device onto a
            // different network, not a fault to be avoided. Because the save above already
            // committed the configuration to NVM, losing the link here costs nothing: the device
            // comes back on the new network with the settings intact. Nothing is sent after this
            // command, so there is no tail left to drop.
            Send(ScpiMessageProducer.ApplyNetworkLan);

            // Hold for the module restart window before returning, so the apply is flushed to the
            // transport rather than left buffered in a connection that is about to go away.
            // Cancelling here ends the wait but does NOT fail the operation: the device has already
            // persisted and applied the new configuration, so reporting "canceled" — and skipping
            // the local-state update below — would leave the caller believing nothing happened
            // while the device is sitting on a different network.
            try
            {
                await Task.Delay(WIFI_MODULE_RESTART_DELAY_MS, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Already committed on the device; stop waiting early and complete normally.
            }

            // Update local configuration. Static IP fields use null = "leave
            // unchanged" semantics, so only overwrite when the caller provided
            // a value — otherwise we'd clobber the previously known static IP.
            _networkConfiguration.Mode = configuration.Mode;
            _networkConfiguration.SecurityType = configuration.SecurityType;
            _networkConfiguration.Ssid = configuration.Ssid;
            _networkConfiguration.Password = configuration.Password;
            if (configuration.StaticIP != null)
            {
                _networkConfiguration.StaticIP = configuration.StaticIP;
            }
            if (configuration.SubnetMask != null)
            {
                _networkConfiguration.SubnetMask = configuration.SubnetMask;
            }
            if (configuration.Gateway != null)
            {
                _networkConfiguration.Gateway = configuration.Gateway;
            }
        }

        /// <summary>
        /// Loads the persisted LAN configuration from the device's NVM back into its runtime settings.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
        public Task LoadNetworkConfigurationAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            // Re-check right before the state-changing send so a cancellation requested after the
            // entry guard still short-circuits the command (matches the pattern accepted in #324).
            cancellationToken.ThrowIfCancellationRequested();
            Send(ScpiMessageProducer.LoadNetworkLan);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Resets the device's LAN configuration to firmware factory defaults.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
        public Task FactoryResetNetworkAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            // Re-check right before the state-changing send so a cancellation requested after the
            // entry guard still short-circuits the command (matches the pattern accepted in #324).
            cancellationToken.ThrowIfCancellationRequested();
            Send(ScpiMessageProducer.FactoryResetNetworkLan);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Prepares the SD-card interface for a file operation. Over USB the LAN interface is
        /// disabled first to free the shared SPI bus for the SD card. Over WiFi/TCP (firmware
        /// &gt;= v3.7.0, #598/#599) the LAN interface MUST stay enabled — the Harmony SPI driver
        /// arbitrates SD/WiFi transactions on the shared bus, and the SD reply routes back over the
        /// very TCP channel that requested it, so disabling LAN would drop the control channel
        /// mid-operation. Only the SD subsystem is enabled in that case.
        /// </summary>
        /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
        public void PrepareSdInterface()
        {
            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            if (IsUsbConnection)
            {
                Send(ScpiMessageProducer.DisableNetworkLan);
            }

            Send(ScpiMessageProducer.EnableStorageSd);
        }

        /// <summary>
        /// Restores the interface after an SD-card file operation. The SD subsystem is disabled in
        /// both cases. Over USB the LAN interface is re-enabled (it was disabled by
        /// <see cref="PrepareSdInterface"/>). Over WiFi/TCP the LAN was never disabled, so it is
        /// left alone — re-enabling it would re-initialize the WiFi module and drop the connection.
        /// </summary>
        /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
        public void PrepareLanInterface()
        {
            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            Send(ScpiMessageProducer.DisableStorageSd);

            if (IsUsbConnection)
            {
                Send(ScpiMessageProducer.EnableNetworkLan);
            }
        }

        /// <summary>
        /// Applies the transport predicate for an SD-card operation that drives the card while the
        /// link is active (LIST / GET / DELETE and the storage-space query). Over USB (serial) these
        /// are available on all SD-capable firmware and are not gated. Over WiFi/TCP they are gated
        /// on <see cref="DeviceFeature.SdFileTransferOverWifi"/>, which
        /// <see cref="DaqifiDevice.EnsureSupported"/> resolves against the requirement table
        /// (ADR 0001) — pre-empting a command the firmware cannot service over WiFi, which would
        /// otherwise stall on the shared SPI bus.
        /// </summary>
        /// <remarks>
        /// This is only the transport half of the gate: which feature applies depends on the active
        /// transport, but whether the device has that feature is the seam's answer, not this
        /// method's.
        /// </remarks>
        /// <exception cref="FeatureNotSupportedException">
        /// Thrown when the active transport is not USB and the device does not support
        /// <see cref="DeviceFeature.SdFileTransferOverWifi"/>.
        /// </exception>
        private void EnsureSdFileTransferSupportedOnTransport()
        {
            if (IsUsbConnection)
            {
                return;
            }

            EnsureSupported(DeviceFeature.SdFileTransferOverWifi);
        }

        /// <summary>
        /// Retrieves the list of files stored on the device's SD card.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>A task that represents the asynchronous operation, containing the list of files.</returns>
        /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
        /// <exception cref="SdCardNotPresentException">Thrown when no SD card is installed in the device.</exception>
        /// <exception cref="SdCardFilesystemException">Thrown when the SD card filesystem cannot satisfy the request (corrupt card, unreadable directory).</exception>
        /// <exception cref="SdCardOperationException">Thrown when the device returned an SCPI error that did not match a more specific condition. Empty directories return an empty list rather than throwing.</exception>
        /// <exception cref="SdCardListIncompleteException">
        /// Thrown when the listing did not arrive in full — the device never answered, or stopped
        /// answering part-way through. Distinguishing this from a genuinely empty card is the whole
        /// point of the terminator probe described in the remarks (closes #396).
        /// </exception>
        /// <remarks>
        /// <para>
        /// The firmware emits no end-of-listing marker, and for an empty directory it writes nothing
        /// at all, so a lost or truncated reply is byte-for-byte indistinguishable from a healthy
        /// empty card. Core closes that gap by appending a <c>SYSTem:ERRor?</c> query to the same
        /// text exchange: the transport delivers in order and the firmware does not process the
        /// next command until the listing has been handed to the output, so receiving the reply
        /// proves both that the device is answering and that the listing ahead of it is complete.
        /// Its absence means the response is incomplete, and the caller gets an exception instead of
        /// a plausible-looking empty list.
        /// </para>
        /// <para>
        /// The terminator is only meaningful if it cannot be confused with a late reply to an
        /// earlier command, so two things guard that boundary: the text exchange discards whatever
        /// was already in flight when it opened, and this method does its SPI-bus switch and settle
        /// delay before the exchange rather than inside it, leaving the exchange with no internal
        /// gap for a stale reply to slip into.
        /// </para>
        /// <para>
        /// The terminator's error code is used only as a liveness marker, never for classification:
        /// the queue it pops can hold entries left by earlier commands, so attributing the code to
        /// this listing would misreport stale failures. SD errors continue to be classified from the
        /// listing lines themselves. Note the side effect this implies — each listing consumes one
        /// entry from the device's SCPI error queue, so a
        /// <see cref="DaqifiDevice.DrainErrorQueueAsync"/> run afterwards will not see the entry
        /// this listing generated.
        /// </para>
        /// </remarks>
        public async Task<IReadOnlyList<SdCardFileInfo>> GetSdCardFilesAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            EnsureSdFileTransferSupportedOnTransport();

            cancellationToken.ThrowIfCancellationRequested();

            // Defensive: always send stop command even if IsStreaming is stale (see issue #118)
            Send(ScpiMessageProducer.StopStreaming);
            IsStreaming = false;

            IReadOnlyList<string> lines = Array.Empty<string>();
            IReadOnlyList<string> listing = Array.Empty<string>();
            var isComplete = false;
            try
            {
                // Attempt 0 plus SD_LIST_MAX_RETRIES retries. A SCPI error here is often a transient
                // timing issue, and an unterminated response can be a one-off stall, so both are
                // retried once after an additional settle delay before being surfaced.
                for (var attempt = 0; attempt <= SD_LIST_MAX_RETRIES; attempt++)
                {
                    if (attempt > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        await Task.Delay(SD_INTERFACE_SETTLE_DELAY_MS, cancellationToken).ConfigureAwait(false);
                    }

                    // The SPI bus switch and its settle wait run as the exchange's prepare phase:
                    // inside the exchange lock, so a competing text exchange cannot restore the LAN
                    // interface between the switch and the LIST, and ahead of the stale-line
                    // boundary, so the settle wait does not become a window in which a late reply to
                    // an earlier command could pass for this listing's terminator. Querying the card
                    // too soon after the switch makes the device answer -200 (Execution error), so
                    // the wait itself is not optional.
                    lines = await ExecuteTextCommandAsync(
                        () =>
                        {
                            Send(ScpiMessageProducer.GetSdFileList);

                            // End-of-listing terminator — see this method's remarks. Sent inside
                            // the same text exchange so the ordering guarantee holds.
                            Send(ScpiMessageProducer.GetSystemError);
                        },
                        responseTimeoutMs: 3000,
                        completionTimeoutMs: SD_LIST_COMPLETION_TIMEOUT_MS,
                        cancellationToken: cancellationToken,
                        prepareAsync: PrepareSdInterfaceAndSettleAsync);

                    isComplete = TrySplitAtSdListTerminator(lines, out listing);

                    if (isComplete && !ContainsScpiError(listing))
                    {
                        break;
                    }
                }
            }
            finally
            {
                // Restore LAN interface regardless of outcome
                if (IsConnected)
                {
                    PrepareLanInterface();
                }
            }

            if (!isComplete)
            {
                throw new SdCardListIncompleteException(lines);
            }

            ThrowIfSdCardListError(listing);

            var files = SdCardFileListParser.ParseFileList(listing);
            _sdCardFiles = files;
            return files;
        }

        /// <summary>
        /// Prepare phase shared by the SD card text exchanges: switches the shared SPI bus over to
        /// the card and waits for the firmware to complete the switch.
        /// </summary>
        /// <remarks>
        /// Passed as the <c>prepareAsync</c> phase of
        /// <see cref="DaqifiDevice.ExecuteTextCommandAsync(Action, int, int, CancellationToken, Func{CancellationToken, Task})"/>
        /// rather than run
        /// inline, so it executes inside the text-exchange lock — a competing exchange restoring the
        /// LAN interface between the switch and the commands that depend on it would leave them
        /// running against the wrong interface — and ahead of the exchange's stale-line boundary, so
        /// the settle wait cannot be mistaken for a window in which the device was answering.
        /// </remarks>
        private async Task PrepareSdInterfaceAndSettleAsync(CancellationToken cancellationToken)
        {
            PrepareSdInterface();

            // Querying the card too soon after the switch makes the device answer -200
            // (Execution error), so this wait is not optional.
            await Task.Delay(SD_INTERFACE_SETTLE_DELAY_MS, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Splits a raw SD listing response at the <c>SYSTem:ERRor?</c> terminator reply that
        /// <see cref="GetSdCardFilesAsync"/> appends to the exchange.
        /// </summary>
        /// <param name="lines">The raw response lines captured from the device.</param>
        /// <param name="listingLines">
        /// The lines that precede the terminator — the directory listing proper — when the method
        /// returns <c>true</c>; otherwise the unmodified input.
        /// </param>
        /// <returns>
        /// <c>true</c> when the terminator was present, meaning the response is complete;
        /// <c>false</c> when it never arrived, meaning the response is missing or truncated.
        /// </returns>
        private static bool TrySplitAtSdListTerminator(
            IReadOnlyList<string> lines,
            out IReadOnlyList<string> listingLines)
        {
            // Scan from the end. A terminator reply from a PREVIOUS, timed-out exchange can still
            // be sitting in the transport buffer and lead this response; splitting at the first
            // match would then discard the listing that follows it and report an empty card —
            // exactly the failure this terminator exists to prevent.
            var terminatorIndex = -1;
            for (var i = lines.Count - 1; i >= 0; i--)
            {
                if (ScpiResponseClassifier.IsSystemErrorReplyLine(lines[i]))
                {
                    terminatorIndex = i;
                    break;
                }
            }

            if (terminatorIndex < 0)
            {
                listingLines = lines;
                return false;
            }

            var listing = new List<string>(terminatorIndex);
            for (var j = 0; j < terminatorIndex; j++)
            {
                // Any other terminator-shaped line is a stale reply of the same kind, not
                // directory content — no firmware listing entry can match that shape, since
                // entries are always "<path> <size>".
                if (ScpiResponseClassifier.IsSystemErrorReplyLine(lines[j]))
                {
                    continue;
                }

                listing.Add(lines[j]);
            }

            listingLines = listing;
            return true;
        }

        /// <summary>
        /// Retrieves the free and total byte counts of the device's SD card.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>A task that represents the asynchronous operation, containing the SD card storage info.</returns>
        /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
        /// <exception cref="System.InvalidOperationException">Thrown when the device is currently logging to SD card.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
        /// <exception cref="SdCardNotPresentException">Thrown when no SD card is installed in the device.</exception>
        /// <exception cref="FeatureNotSupportedException">
        /// Thrown when the device's firmware does not recognize the storage query (SCPI -113
        /// "Undefined header"), typically because it predates <see cref="DaqifiDevice.MinSupportedFirmware"/>;
        /// or, over a WiFi/TCP transport, when the firmware predates SD-over-WiFi support
        /// (<see cref="DeviceFeature.SdFileTransferOverWifi"/>) — the storage query drives the SD
        /// card through the same transport gate as the file operations.
        /// </exception>
        /// <exception cref="SdCardOperationException">Thrown when the device returned a SCPI error or an unparseable response.</exception>
        public async Task<SdCardStorageInfo> GetSdCardStorageAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            if (_isLoggingToSdCard)
            {
                throw new InvalidOperationException("Cannot query SD card storage while logging to SD card.");
            }

            // The storage-space query drives the SD card through the same transport-aware
            // PrepareSdInterface() as LIST/GET/DELETE, so it carries the identical SD-over-WiFi
            // requirement: over WiFi it needs firmware >= v3.7.0 (#598/#599 SPI arbitration) — else
            // it would access the SD card with the LAN still enabled on firmware that never learned
            // to arbitrate the shared bus. Gate it up front for the same reason as its siblings.
            EnsureSdFileTransferSupportedOnTransport();

            cancellationToken.ThrowIfCancellationRequested();

            // Defensive: always send stop command even if IsStreaming is stale (see issue #118)
            Send(ScpiMessageProducer.StopStreaming);
            IsStreaming = false;

            IReadOnlyList<string> lines;
            try
            {
                lines = await ExecuteTextCommandAsync(() =>
                {
                    PrepareSdInterface();

                    // Allow the device firmware to complete the SPI bus switch
                    // before querying the SD card. Without this delay, the device
                    // can return SCPI error -200 (Execution error).
                    Thread.Sleep(SD_INTERFACE_SETTLE_DELAY_MS);

                    Send(ScpiMessageProducer.GetSdSpace);
                }, responseTimeoutMs: 3000, cancellationToken: cancellationToken);

                // Only retry transient SCPI errors. A "No SD Card Detected" line
                // is non-transient — retrying just delays the typed exception and
                // risks misclassification if the marker isn't repeated on retry.
                if (ContainsScpiError(lines) && !ContainsNoSdCardMarker(lines))
                {
                    for (var retry = 0; retry < SD_LIST_MAX_RETRIES; retry++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        await Task.Delay(SD_INTERFACE_SETTLE_DELAY_MS, cancellationToken);

                        lines = await ExecuteTextCommandAsync(() =>
                        {
                            PrepareSdInterface();
                            Thread.Sleep(SD_INTERFACE_SETTLE_DELAY_MS);
                            Send(ScpiMessageProducer.GetSdSpace);
                        }, responseTimeoutMs: 3000, cancellationToken: cancellationToken);

                        if (!ContainsScpiError(lines) || ContainsNoSdCardMarker(lines))
                        {
                            break;
                        }
                    }
                }
            }
            finally
            {
                if (IsConnected)
                {
                    PrepareLanInterface();
                }
            }

            if (SdCardSpaceParser.TryParseLines(lines, out var storage))
            {
                return storage;
            }

            // Parser failed — translate the firmware response into a typed exception.
            var lastScpiError = lines.LastOrDefault(IsScpiErrorLine)?.Trim();

            if (ContainsNoSdCardMarker(lines))
            {
                throw new SdCardNotPresentException(lines, lastScpiError);
            }

            // A -113 "Undefined header" reply means the firmware doesn't recognize the storage
            // query at all — typically because it predates the version that introduced it — so
            // it gets the typed feature-gating exception instead of a generic operation error.
            // The device's answer is authoritative here, so this throws on the wire response
            // rather than on Supports(); the seam only supplies the required version and board.
            if (lastScpiError != null
                && ScpiResponseClassifier.TryExtractErrorCode(lastScpiError, out var scpiErrorCode)
                && scpiErrorCode == ScpiErrorCodeUndefinedHeader)
            {
                throw CreateFeatureNotSupportedException(DeviceFeature.SdStorageQuery);
            }

            throw new SdCardOperationException(
                lastScpiError != null
                    ? "The SD card storage query failed: " + lastScpiError
                    : "The SD card storage query returned an unparseable response.",
                lines,
                lastScpiError);
        }

        private static bool ContainsNoSdCardMarker(IReadOnlyList<string> lines)
        {
            return lines.Any(l => l.IndexOf("No SD Card Detected", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <inheritdoc />
        public async Task<SdCardSpaceCheckResult> CheckSdCardSpaceAsync(
            SdCardCaptureEstimate? plannedCapture = null,
            long minimumFreeBytes = SdCardSpaceCheck.DefaultMinimumFreeBytes,
            CancellationToken cancellationToken = default)
        {
            // Delegates connection / logging-state validation and the typed SD exceptions
            // (no card, old firmware, unparseable response) to GetSdCardStorageAsync.
            var storage = await GetSdCardStorageAsync(cancellationToken).ConfigureAwait(false);

            var result = SdCardSpaceCheck.Evaluate(storage, plannedCapture, minimumFreeBytes);

            // Advisory only — raise the warning but never block the caller from starting logging.
            if (result.ShouldWarn)
            {
                OnLowSdSpaceWarning(new LowSdSpaceWarningEventArgs(result));
            }

            return result;
        }

        /// <summary>
        /// Raises the <see cref="LowSdSpaceWarning"/> event.
        /// </summary>
        /// <param name="e">The warning event arguments.</param>
        protected virtual void OnLowSdSpaceWarning(LowSdSpaceWarningEventArgs e)
        {
            LowSdSpaceWarning?.Invoke(this, e);
        }

        /// <inheritdoc />
        public void SetSdCardMinimumFreeSpace(long bytes)
        {
            // Argument validation precedes the connection (state) check so misuse surfaces the same
            // exception type regardless of connection state (matches SetAnalogOutput / SetDioDirection).
            if (bytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bytes), bytes, "Minimum free space cannot be negative.");
            }

            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            Send(ScpiMessageProducer.SetSdMinFreeSpace(bytes));
        }

        /// <summary>
        /// Starts logging data to the SD card. Compatibility overload preserving the original
        /// <see cref="Task"/> return; use <see cref="StartSdCardLoggingSessionAsync"/> to also learn
        /// the effective on-card file name.
        /// </summary>
        /// <param name="fileName">The log file name, or null/empty to auto-generate a timestamped name.</param>
        /// <param name="channelMask">Optional decimal channel bitmask; null/empty uses the current config.</param>
        /// <param name="format">The logging format to use. Defaults to <see cref="SdCardLogFormat.Protobuf"/>.</param>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
        public Task StartSdCardLoggingAsync(string? fileName = null, string? channelMask = null, SdCardLogFormat format = SdCardLogFormat.Protobuf, CancellationToken cancellationToken = default)
            => StartSdCardLoggingSessionAsync(fileName, channelMask, format, cancellationToken);

        /// <summary>
        /// Starts logging data to the SD card and returns the effective session details.
        /// </summary>
        /// <param name="fileName">
        /// The name of the log file. If null or empty, a timestamped name is generated automatically
        /// using the pattern "log_YYYYMMDD_HHMMSS" with an extension matching <paramref name="format"/>
        /// (.bin for Protobuf, .json for JSON, .csv for CSV).
        /// </param>
        /// <param name="channelMask">
        /// Optional decimal bitmask string to enable specific ADC channels (e.g. "3" enables channels 0 and 1).
        /// The firmware parses this as a decimal integer where each bit enables a channel.
        /// If null or empty, the current device channel configuration is used.
        /// </param>
        /// <param name="format">The logging format to use. Defaults to <see cref="SdCardLogFormat.Protobuf"/>.</param>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>
        /// A task that resolves to an <see cref="SdCardLoggingSession"/> carrying the effective on-card
        /// file name (supplied or auto-generated) and the logging format.
        /// </returns>
        /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
        public async Task<SdCardLoggingSession> StartSdCardLoggingSessionAsync(string? fileName = null, string? channelMask = null, SdCardLogFormat format = SdCardLogFormat.Protobuf, CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            if (!IsUsbConnection)
            {
                throw new InvalidOperationException(
                    "SD card logging requires a USB/serial connection. " +
                    "The SD card and WiFi/LAN share the SPI bus, so SD operations cannot be performed over a network connection.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var extension = format switch
            {
                SdCardLogFormat.Json => ".json",
                SdCardLogFormat.Csv => ".csv",
                _ => ".bin",
            };

            var logFileName = !string.IsNullOrWhiteSpace(fileName)
                ? fileName!
                : $"log_{DateTime.Now:yyyyMMdd_HHmmss}{extension}";

            ValidateSdCardFileName(logFileName);

            // SdCardLogFormat integer values map 1:1 to SYSTem:STReam:FORmat SCPI arguments
            var formatCommand = new ScpiMessage($"SYSTem:STReam:FORmat {(int)format}");

            // SD card and LAN share the SPI bus on the hardware, so LAN must be
            // disabled before the SD card can be used.
            Send(ScpiMessageProducer.DisableNetworkLan);
            await Task.Delay(100, cancellationToken);

            Send(ScpiMessageProducer.EnableStorageSd);
            await Task.Delay(100, cancellationToken);

            // Route the data stream to the SD card interface.
            Send(ScpiMessageProducer.SetStreamInterface(StreamInterface.SdCard));
            await Task.Delay(100, cancellationToken);

            Send(ScpiMessageProducer.SetSdLoggingFileName(logFileName));
            await Task.Delay(100, cancellationToken);

            Send(formatCommand);
            await Task.Delay(100, cancellationToken);

            if (!string.IsNullOrWhiteSpace(channelMask))
            {
                Send(ScpiMessageProducer.EnableAdcChannels(channelMask));
                await Task.Delay(100, cancellationToken);
            }

            Send(ScpiMessageProducer.StartStreaming(StreamingFrequency));

            _isLoggingToSdCard = true;
            IsStreaming = true;

            return new SdCardLoggingSession(logFileName, format);
        }

        /// <summary>
        /// Stops logging data to the SD card.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
        public Task StopSdCardLoggingAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Defensive: always send stop command even if IsStreaming is stale (see issue #118)
            Send(ScpiMessageProducer.StopStreaming);
            IsStreaming = false;

            Send(ScpiMessageProducer.DisableStorageSd);

            // Restore stream interface to USB so subsequent non-SD operations work.
            if (IsUsbConnection)
            {
                Send(ScpiMessageProducer.SetStreamInterface(StreamInterface.Usb));
            }

            // Re-enable LAN interface. StartSdCardLoggingAsync disables LAN because
            // the SD card and WiFi/LAN share the SPI bus on the hardware.
            Send(ScpiMessageProducer.EnableNetworkLan);

            _isLoggingToSdCard = false;

            return Task.CompletedTask;
        }

        /// <summary>
        /// Deletes a file from the SD card.
        /// </summary>
        /// <param name="fileName">The name of the file to delete.</param>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
        /// <exception cref="System.InvalidOperationException">Thrown when the device is currently logging to SD card.</exception>
        /// <exception cref="ArgumentException">Thrown when the filename is null, empty, or contains invalid characters.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
        public async Task DeleteSdCardFileAsync(string fileName, CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            if (_isLoggingToSdCard)
            {
                throw new InvalidOperationException("Cannot delete files while logging to SD card.");
            }

            EnsureSdFileTransferSupportedOnTransport();

            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new ArgumentException("Filename cannot be null or empty.", nameof(fileName));
            }

            ValidateSdCardFileName(fileName);

            // Defensive: always send stop command even if IsStreaming is stale (see issue #118)
            Send(ScpiMessageProducer.StopStreaming);
            IsStreaming = false;

            IReadOnlyList<string> lines;
            try
            {
                // Same prepare-phase treatment as GetSdCardFilesAsync, for the same two reasons —
                // the SPI switch stays serialized against competing text exchanges, and its settle
                // wait stays outside the stale-line boundary. The consequence of a stale line is
                // milder here (delete keys off ContainsScpiError, so it would mean a pointless
                // delete-and-relist retry rather than a bad listing) but it is the same defect.
                lines = await ExecuteTextCommandAsync(
                    () =>
                    {
                        Send(ScpiMessageProducer.DeleteSdFile(fileName));
                        Send(ScpiMessageProducer.GetSdFileList);
                    },
                    responseTimeoutMs: 3000,
                    cancellationToken: cancellationToken,
                    prepareAsync: PrepareSdInterfaceAndSettleAsync);

                if (ContainsScpiError(lines))
                {
                    for (var retry = 0; retry < SD_LIST_MAX_RETRIES; retry++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        await Task.Delay(SD_INTERFACE_SETTLE_DELAY_MS, cancellationToken).ConfigureAwait(false);

                        lines = await ExecuteTextCommandAsync(
                            () =>
                            {
                                Send(ScpiMessageProducer.DeleteSdFile(fileName));
                                Send(ScpiMessageProducer.GetSdFileList);
                            },
                            responseTimeoutMs: 3000,
                            cancellationToken: cancellationToken,
                            prepareAsync: PrepareSdInterfaceAndSettleAsync);

                        if (!ContainsScpiError(lines))
                        {
                            break;
                        }
                    }
                }
            }
            finally
            {
                if (IsConnected)
                {
                    PrepareLanInterface();
                }
            }

            _sdCardFiles = SdCardFileListParser.ParseFileList(lines);
        }

        /// <summary>
        /// Formats the entire SD card, erasing all data.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
        /// <exception cref="System.InvalidOperationException">Thrown when the device is currently logging to SD card.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
        public Task FormatSdCardAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            if (_isLoggingToSdCard)
            {
                throw new InvalidOperationException("Cannot format SD card while logging.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Defensive: always send stop command even if IsStreaming is stale (see issue #118)
            Send(ScpiMessageProducer.StopStreaming);
            IsStreaming = false;

            Send(ScpiMessageProducer.EnableStorageSd);
            Send(ScpiMessageProducer.FormatSdCard);

            return Task.CompletedTask;
        }

        /// <summary>
        /// Downloads a file from the device's SD card over USB.
        /// </summary>
        /// <param name="fileName">The name of the file to download.</param>
        /// <param name="destinationStream">The stream to write file contents to.</param>
        /// <param name="progress">Optional progress reporting.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Metadata about the downloaded file.</returns>
        /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
        /// <exception cref="FeatureNotSupportedException">Thrown over a WiFi/TCP transport when the firmware predates SD-over-WiFi file transfer.</exception>
        /// <exception cref="ArgumentException">Thrown when the filename is null, empty, or contains invalid characters.</exception>
        /// <exception cref="SdCardEmptyTransferException">
        /// Thrown when the device serves a marker-only (0-byte) transfer across all retry attempts
        /// for a file the last <see cref="GetSdCardFilesAsync"/> listing reported as non-empty (or
        /// whose listed size is unknown), indicating its SD subsystem is not ready. A file the
        /// listing reports as 0 bytes downloads successfully as a legitimate empty file.
        /// </exception>
        /// <exception cref="SdCardTransferStalledException">
        /// Thrown when the transfer stops making progress before the end-of-file marker arrives.
        /// </exception>
        /// <exception cref="TimeoutException">
        /// Thrown when the download does not finish within <see cref="SdCardDownloadTimeout"/>.
        /// The deadline is enforced by this method itself, so it still applies when the transfer
        /// is parked in a call that cannot observe a cancellation token (#399).
        /// </exception>
        /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is canceled.</exception>
        /// <remarks>
        /// On a timeout — or a cancellation the parked transfer cannot itself observe — the
        /// in-flight transfer is <b>abandoned</b> rather than awaited: it may be blocked in native
        /// serial I/O that no token can interrupt, and waiting for it is the hang this method
        /// exists to bound. The abandoned transfer's token is cancelled first, so it unwinds at
        /// its next token check — but that check is only reached once whatever it is blocked in
        /// returns, which may be never. Two consequences for callers: it can still write to
        /// <paramref name="destinationStream"/> after this method has thrown, so the stream must
        /// not be reused for anything else; and the device is left mid-<c>SD:GET</c> with the
        /// protobuf consumer stopped, so reconnecting (or power-cycling, if its SD subsystem is
        /// genuinely wedged) is the reliable way to resume normal operation.
        /// <para>
        /// Until an abandoned transfer unwinds it still owns the transport, so a further download
        /// on the same device fails fast with <see cref="InvalidOperationException"/> rather than
        /// putting a second reader on the same stream. A caller looping over many files against a
        /// wedged card therefore gets one timeout and then immediate, cheap failures — not a
        /// growing pile of blocked threads.
        /// </para>
        /// </remarks>
        public async Task<SdCardDownloadResult> DownloadSdCardFileAsync(
            string fileName,
            Stream destinationStream,
            IProgress<SdCardTransferProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            // Over WiFi/TCP this requires firmware >= v3.7.0 (#598/#599); over USB it is always
            // available on SD-capable firmware. Older firmware over WiFi gets a typed
            // FeatureNotSupportedException instead of the old blanket USB-only rejection (ADR 0001).
            EnsureSdFileTransferSupportedOnTransport();

            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new ArgumentException("Filename cannot be null or empty.", nameof(fileName));
            }

            ValidateSdCardFileName(fileName);
            ArgumentNullException.ThrowIfNull(destinationStream);

            cancellationToken.ThrowIfCancellationRequested();

            if (_isLoggingToSdCard)
            {
                throw new InvalidOperationException("Cannot download files while logging to SD card.");
            }

            // Defensive: always send stop command even if IsStreaming is stale (see issue #118)
            Send(ScpiMessageProducer.StopStreaming);
            IsStreaming = false;

            var stopwatch = Stopwatch.StartNew();
            long fileSize = 0;
            var budget = SdCardDownloadTimeout;

            try
            {
                await RunWithHardDeadlineAsync(async token =>
                {
                    await ExecuteRawCaptureAsync(async (stream, ct) =>
                    {
                        // Prepare SD card interface
                        PrepareSdInterface();

                        // Small delay to let the interface switch settle
                        await Task.Delay(50, ct).ConfigureAwait(false);

                        // Send the SCPI command to request the file
                        Send(ScpiMessageProducer.GetSdFile(fileName));

                        // Receive the file data. A marker-only (0-byte) transfer for a file the
                        // listing reports as non-empty means the device's SD subsystem wasn't ready
                        // when it opened the file - the same kind of transient condition
                        // GetSdCardFilesAsync's LIST retry already absorbs - so retry the GET a
                        // bounded number of times before giving up (see #264). Passing the listed
                        // size keeps that retry off a genuinely 0-byte file, which is a legitimate
                        // empty download rather than a wedged subsystem (#398 gap 2).
                        var receiver = new SdCardFileReceiver(stream);
                        var listedFileSizeBytes = TryGetListedFileSize(fileName);
                        long bytesReceived;
                        var attempt = 0;
                        while (true)
                        {
                            try
                            {
                                // Each attempt gets what is left of the overall budget, never a
                                // fresh full one: retries must not be able to push the total past
                                // the deadline the caller was promised.
                                bytesReceived = await receiver.ReceiveAsync(
                                    destinationStream,
                                    fileName,
                                    progress,
                                    timeout: RemainingBudget(budget, stopwatch),
                                    cancellationToken: ct,
                                    listedFileSizeBytes: listedFileSizeBytes).ConfigureAwait(false);
                                break;
                            }
                            catch (SdCardEmptyTransferException) when (attempt < SD_LIST_MAX_RETRIES)
                            {
                                attempt++;
                                await Task.Delay(SD_INTERFACE_SETTLE_DELAY_MS, ct).ConfigureAwait(false);
                                Send(ScpiMessageProducer.GetSdFile(fileName));
                            }
                        }

                        fileSize = bytesReceived;
                    }, token).ConfigureAwait(false);
                }, budget, fileName, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // Restore LAN interface
                if (IsConnected)
                {
                    try
                    {
                        PrepareLanInterface();
                    }
                    catch
                    {
                        // Best-effort restoration; the device may have disconnected
                    }
                }
            }

            stopwatch.Stop();
            return new SdCardDownloadResult(fileName, fileSize, stopwatch.Elapsed);
        }

        /// <summary>
        /// Looks up the size the most recent directory listing reported for a file. Returns null
        /// ("unknown", which the receiver treats conservatively) when no listing has been fetched,
        /// when the listing did not include this file or a size for it, or when more than one
        /// listed entry shares the name.
        /// </summary>
        private long? TryGetListedFileSize(string fileName)
        {
            // Snapshot the field: GetSdCardFilesAsync replaces the list wholesale, so a
            // concurrent refresh swaps the reference rather than mutating what we enumerate.
            var listedFiles = _sdCardFiles;

            long? matchedSize = null;
            var matched = false;

            foreach (var file in listedFiles)
            {
                // FAT names are case-insensitive. The listing keeps only the leaf name, so the
                // same name can appear twice from different directories; that is ambiguous and
                // an over-confident size here would wave through the very failure (a wedged
                // subsystem serving nothing) the empty-transfer guard exists to catch.
                if (!string.Equals(file.FileName, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (matched)
                {
                    return null;
                }

                matched = true;
                matchedSize = file.SizeInBytes;
            }

            return matchedSize;
        }

        /// <summary>
        /// Downloads a file from the device's SD card over USB to a temporary file.
        /// </summary>
        /// <param name="fileName">The name of the file to download.</param>
        /// <param name="progress">Optional progress reporting.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Metadata about the downloaded file, including the local file path.</returns>
        /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
        /// <exception cref="FeatureNotSupportedException">Thrown over a WiFi/TCP transport when the firmware predates SD-over-WiFi file transfer.</exception>
        /// <exception cref="ArgumentException">Thrown when the filename is null, empty, or contains invalid characters.</exception>
        public async Task<SdCardDownloadResult> DownloadSdCardFileAsync(
            string fileName,
            IProgress<SdCardTransferProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var ext = Path.GetExtension(fileName);
            if (string.IsNullOrEmpty(ext)) ext = ".bin";
            var tempPath = Path.Combine(Path.GetTempPath(), $"daqifi_{Guid.NewGuid():N}{ext}");
            try
            {
                await using var fileStream = new FileStream(
                    tempPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 65536,
                    useAsync: true);

                var result = await DownloadSdCardFileAsync(fileName, fileStream, progress, cancellationToken)
                    .ConfigureAwait(false);

                return result with { FilePath = tempPath };
            }
            catch
            {
                try { File.Delete(tempPath); } catch { /* ignore cleanup failures */ }
                throw;
            }
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
        /// The part of <paramref name="budget"/> not yet consumed, floored at zero (a negative
        /// timeout is not a legal <see cref="CancellationTokenSource"/> delay).
        /// </summary>
        private static TimeSpan RemainingBudget(TimeSpan budget, Stopwatch stopwatch)
        {
            var remaining = budget - stopwatch.Elapsed;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        /// <summary>
        /// The instant the download is given up on regardless of what it is doing. It sits just
        /// past the cooperative <paramref name="budget"/> so that a transfer which IS observing
        /// its token still fails through the receiver's own timeout — which reports how many
        /// bytes arrived — and the hard deadline only decides the case where it is not.
        /// </summary>
        private static TimeSpan HardDeadlineFor(TimeSpan budget)
        {
            var graceMs = Math.Clamp(budget.TotalMilliseconds * 0.1, 100, 5000);
            return budget + TimeSpan.FromMilliseconds(graceMs);
        }

        /// <summary>
        /// Runs an SD download on a worker task and races it against a hard deadline, so neither
        /// the deadline nor the caller's cancellation depends on the transfer being somewhere it
        /// can observe a token (#399). On expiry the worker is abandoned rather than awaited.
        /// </summary>
        /// <param name="operation">The transfer. Receives a token cancelled by caller cancellation or the deadline, whichever comes first.</param>
        /// <param name="budget">The cooperative budget; the hard deadline is <see cref="HardDeadlineFor"/> of it.</param>
        /// <param name="fileName">Used only in the <see cref="TimeoutException"/> message.</param>
        /// <param name="cancellationToken">The caller's token, observed by the race itself and not only by the worker.</param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when a previous download still owns <see cref="_sdDownloadGate"/> — it is either
        /// genuinely in flight or was abandoned and is still parked on the transport.
        /// </exception>
        private async Task RunWithHardDeadlineAsync(
            Func<CancellationToken, Task> operation,
            TimeSpan budget,
            string fileName,
            CancellationToken cancellationToken)
        {
            // Checked before taking the gate so a cancelled caller neither acquires it nor gets an
            // answer about some other transfer.
            cancellationToken.ThrowIfCancellationRequested();

            // Fail fast rather than becoming a second reader on a stream an abandoned transfer
            // still holds. Wait(0) never blocks: this either takes the gate or reports the state.
            if (!_sdDownloadGate.Wait(0))
            {
                // Cancellation wins when it raced the gate check — the same precedence the abandon
                // path below applies. The caller asked to stop; that is a truer answer than a
                // report about a different download.
                cancellationToken.ThrowIfCancellationRequested();

                throw new InvalidOperationException(
                    "A previous SD card download is still in flight, or was abandoned after timing out and " +
                    "is still parked on the transport. Reconnect the device before retrying.");
            }

            // Released exactly once, by whichever path is last to be done with the worker: the
            // finally below in the normal case, or the abandon-path continuation when the worker
            // finally unwinds. Interlocked because a worker that completes right at the deadline
            // boundary can reach both.
            var gateReleased = 0;
            void ReleaseGate()
            {
                if (Interlocked.Exchange(ref gateReleased, 1) != 0)
                {
                    return;
                }

                try
                {
                    _sdDownloadGate.Release();
                }
                catch (ObjectDisposedException)
                {
                    // The device was disposed while a transfer was still abandoned. Benign
                    // teardown, and this can run from a discarded continuation — never throw.
                }
            }

            var hardDeadline = HardDeadlineFor(budget);

            // hardDeadlineCts runs on its own timer, independent of the Task.Delay race below, so
            // it still reaches the worker if the worker only returns long after the race was
            // decided. linkedCts is what the worker observes: caller cancellation OR the deadline.
            var hardDeadlineCts = new CancellationTokenSource(hardDeadline);
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, hardDeadlineCts.Token);

            // Stops the racing delay the moment the outcome is decided — without it, a download
            // that finishes in a second would leave a 30-minute timer registered behind it.
            var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // LongRunning (a dedicated thread, not a pooled one): the transfer's synchronous
            // prefix — the consumer stop-and-join, PrepareSdInterface's blocking writes — otherwise
            // runs on the CALLING thread up to the first await, which on a UI thread means a
            // wedged device freezes the window, and which would put that prefix outside the very
            // deadline it needs to be inside. A pooled Task.Run would also tie up a worker for the
            // transfer's full blocking duration. Pass CancellationToken.None to StartNew itself:
            // the worker's own token still cancels its waits, and "cancelled before start" must
            // not surface as an operation fault. (Mirrors WifiBridgeActivator, #294/#295/#326.)
            var workerTask = Task.Factory.StartNew(
                () => operation(linkedCts.Token),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();

            try
            {
                var winner = await Task.WhenAny(
                    workerTask,
                    Task.Delay(hardDeadline, raceCts.Token)).ConfigureAwait(false);

                // Only abandon when the worker is genuinely still running: WhenAny can hand back
                // the delay even though the worker completed at that same boundary, and awaiting
                // it below honors that result instead of discarding it.
                if (winner != workerTask && !workerTask.IsCompleted)
                {
                    // Cancel explicitly instead of relying on the deadline timer having fired: the
                    // delay above and hardDeadlineCts are two separate timers of the same duration,
                    // so the delay can win by a hair and leave a late-returning worker running one
                    // more state-changing step after the caller already threw. Idempotent.
                    hardDeadlineCts.Cancel();

                    // The worker may be parked in native serial I/O that no token can interrupt, so
                    // it is ABANDONED, not awaited — waiting for it is the hang being bounded here.
                    // Observe its eventual fault so it cannot resurface as an UnobservedTaskException,
                    // and dispose the sources only once it is done with them (disposing early would
                    // turn its pending waits into ObjectDisposedException instead of cancellation).
                    _ = workerTask.ContinueWith(
                        t =>
                        {
                            _ = t.Exception;
                            linkedCts.Dispose();
                            hardDeadlineCts.Dispose();

                            // Only now is the transport genuinely free for another download.
                            ReleaseGate();
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);

                    // Prefer surfacing caller cancellation over a generic timeout when both raced.
                    cancellationToken.ThrowIfCancellationRequested();

                    throw new TimeoutException(
                        $"SD card download of '{fileName}' did not complete within " +
                        $"{hardDeadline.TotalSeconds:0.#}s and was abandoned. The device's SD " +
                        "subsystem is not responding; reconnect (or power-cycle) before retrying.");
                }

                // Propagate success or the transfer's own exception unchanged.
                await workerTask.ConfigureAwait(false);
            }
            finally
            {
                raceCts.Cancel();
                raceCts.Dispose();

                // The abandon path hands disposal and the gate to its continuation instead; do it
                // here only when the worker actually finished (the common, non-hung case).
                if (workerTask.IsCompleted)
                {
                    linkedCts.Dispose();
                    hardDeadlineCts.Dispose();
                    ReleaseGate();
                }
            }
        }

        /// <summary>
        /// Checks whether any line in the response contains a SCPI error indicator.
        /// These errors (e.g., "**ERROR: -200") can occur transiently when the device
        /// firmware has not finished switching the SPI bus interface.
        /// </summary>
        /// <param name="lines">The response lines to check.</param>
        /// <returns>True if any line contains a SCPI error, false otherwise.</returns>
        private static bool ContainsScpiError(IReadOnlyList<string> lines)
        {
            return lines.Any(IsScpiErrorLine);
        }

        // Strict SCPI error format: "**ERROR" or bare "ERROR" followed by a SCPI delimiter
        // (":", space, tab, or end-of-line). Distinguishes a true SCPI error from firmware
        // status text like "Error !! No SD Card Detected", which should not be surfaced as
        // SdCardOperationException.LastScpiError. Shared with ScpiInitializationErrorException
        // classification in DaqifiDevice.InitializeAsync so both sites recognize the same set
        // of delimiter-separated error formats (closes a gap where "ERROR -200,..." or
        // "ERROR\t-200,..." without a colon went undetected).
        private static bool IsScpiErrorLine(string line)
        {
            return ScpiResponseClassifier.IsScpiErrorLine(line);
        }

        // Permissive: any line that looks like a device error or status message,
        // including firmware text such as "Error !! ...". Used to recognize that
        // the parser would yield no result, without polluting LastScpiError with
        // non-SCPI text. Shared classifier so the SD-response rule (closes #190
        // — filenames starting with "error_" must NOT match) stays in lockstep
        // across both call sites.
        private static bool IsNonResultLine(string line)
        {
            return ScpiResponseClassifier.IsErrorResponseLine(line);
        }

        /// <summary>
        /// Inspects the final response from a <c>SYSTem:STORage:SD:LISt?</c> exchange
        /// and throws a typed <see cref="SdCardOperationException"/> when the device
        /// reported a real failure (no SD card, filesystem error, generic SCPI error).
        /// If any non-error/non-empty line is present, callers proceed to parse — even
        /// if SCPI error lines are interleaved — so a successful directory listing is
        /// never masked by stray transient errors.
        /// </summary>
        private static void ThrowIfSdCardListError(IReadOnlyList<string> lines)
        {
            // LastScpiError must only carry a real SCPI-formatted error so callers
            // can rely on its shape. Firmware status text ("Error !! ...") is
            // surfaced via the exception's Message and RawDeviceResponse instead.
            var lastScpiError = lines.LastOrDefault(IsScpiErrorLine)?.Trim();

            // Specific firmware-emitted error markers take precedence over generic
            // content/error checks. They're plain text (not SCPI-shaped), so a
            // simple "is there any content line?" check would otherwise miss them
            // and pass garbage to the parser.
            if (lines.Any(l => l.IndexOf("No SD Card Detected", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                throw new SdCardNotPresentException(lines, lastScpiError);
            }

            var filesystemErrorLine = lines.FirstOrDefault(l =>
                l.IndexOf("Failed to open directory", StringComparison.OrdinalIgnoreCase) >= 0);
            if (filesystemErrorLine != null)
            {
                throw new SdCardFilesystemException(lines, lastScpiError, filesystemErrorLine.Trim());
            }

            // If any line looks like a real result (non-empty, not an error or
            // firmware status line), hand off to the parser. Stray interleaved
            // error lines are still parsed away by SdCardFileListParser.
            var hasContentLine = lines.Any(line =>
                !string.IsNullOrWhiteSpace(line) && !IsNonResultLine(line));
            if (hasContentLine)
            {
                return;
            }

            if (lastScpiError != null)
            {
                throw new SdCardOperationException(
                    "The SD card list operation failed: " + lastScpiError,
                    lines,
                    lastScpiError);
            }

            // Defensive fallback: firmware status text ("Error !! ...") with no
            // SCPI error and no recognized marker. Shouldn't happen for known
            // firmware paths, but surfacing it as a typed exception is far
            // better than silently returning an empty list.
            var nonResultLine = lines.FirstOrDefault(l =>
                !string.IsNullOrWhiteSpace(l) && IsNonResultLine(l))?.Trim();
            if (nonResultLine != null)
            {
                throw new SdCardOperationException(
                    "The SD card list operation failed: " + nonResultLine,
                    lines,
                    lastScpiError: null);
            }

            // No error lines and no content lines — empty directory. Caller continues.
            // Safe to treat as empty rather than as a lost reply: GetSdCardFilesAsync only reaches
            // this point once the device has answered the end-of-listing terminator (#396).
        }

        /// <summary>
        /// Validates an SD card filename to prevent SCPI command injection.
        /// </summary>
        /// <param name="fileName">The filename to validate.</param>
        /// <exception cref="ArgumentException">Thrown when the filename contains invalid characters.</exception>
        private static void ValidateSdCardFileName(string fileName)
        {
            if (fileName.IndexOfAny(new[] { '"', '\n', '\r', ';' }) >= 0)
            {
                throw new ArgumentException(
                    "Filename contains invalid characters. Quotes, newlines, and semicolons are not allowed.",
                    nameof(fileName));
            }
        }

        /// <inheritdoc />
        public async Task<LanChipInfo?> GetLanChipInfoAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            var lines = await ExecuteTextCommandAsync(
                () => Send(ScpiMessageProducer.GetLanChipInfo),
                responseTimeoutMs: 2000,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (LanChipInfoParser.TryParseLines(lines, out var info))
            {
                return info;
            }

            // Closes #203: LAN:ENAbled=1 in saved settings but the WINC1500 state
            // machine hasn't reached INITIALIZED yet (steady-state, not the
            // post-reboot transient #144 already retries for) makes GETChipInfo?
            // return this specific SCPI error instead of JSON. Surface it distinctly
            // so the caller's retry loop can react (kick LAN:APPLY) instead of just
            // waiting out a blind delay.
            var errorLine = lines.LastOrDefault(IsScpiErrorLine);
            if (errorLine != null && ScpiResponseClassifier.TryExtractErrorCode(errorLine, out var errorCode) && errorCode == -200)
            {
                throw new LanNotInitializedException(errorLine.Trim());
            }

            return null;
        }

        // -----------------------------------------------------------------
        // IDeviceDiagnostics
        //
        // Each method issues a single SCPI query/command as a text command
        // (the protobuf consumer is paused for the exchange, same as the SD
        // and LAN-chip queries) and hands the response to a tolerant parser.
        // Unlike the SD operations these do not switch the SPI bus, so there
        // is no PrepareSdInterface / settle delay; and they intentionally do
        // not stop streaming, so callers can sample live counters — though
        // parsing is most reliable when the device is not actively streaming.
        // -----------------------------------------------------------------

        /// <summary>Time allowed for the first diagnostics response line. Generous because
        /// <c>SYSTem:LOG?</c> and the stats queries can emit dozens of lines.</summary>
        private const int DIAGNOSTICS_RESPONSE_TIMEOUT_MS = 2000;

        /// <summary>
        /// Throws a <see cref="DeviceDiagnosticsException"/> when a diagnostics command produced no
        /// usable result and the device's response consisted solely of SCPI error/status lines —
        /// i.e. the command failed (commonly an unsupported header on below-floor firmware) rather
        /// than legitimately returning nothing. A truly empty response (no lines) is treated as
        /// success so callers can distinguish "empty log" from "command failed".
        /// </summary>
        private static void ThrowIfErrorOnlyResponse(int parsedResultCount, IReadOnlyList<string> lines, string operation)
        {
            if (parsedResultCount == 0 && IsErrorOnlyResponse(lines))
            {
                throw new DeviceDiagnosticsException(
                    $"The device returned an error while attempting to {operation}.",
                    lines);
            }
        }

        /// <summary>
        /// Returns true when the response contains at least one non-empty line and every non-empty
        /// line is a SCPI error/status line (per <see cref="ScpiResponseClassifier"/>).
        /// </summary>
        private static bool IsErrorOnlyResponse(IReadOnlyList<string> lines)
        {
            var sawContent = false;
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                sawContent = true;
                if (!IsNonResultLine(line))
                {
                    return false;
                }
            }

            return sawContent;
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<SystemLogEntry>> GetSystemLogAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            cancellationToken.ThrowIfCancellationRequested();

            var lines = await ExecuteTextCommandAsync(
                () => Send(ScpiMessageProducer.GetSystemLog),
                responseTimeoutMs: DIAGNOSTICS_RESPONSE_TIMEOUT_MS,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var entries = SystemLogParser.Parse(lines);

            // The parser drops error/status lines, so an error-only response would
            // otherwise be indistinguishable from a genuinely empty log buffer.
            // Surface a command failure (e.g. unsupported on below-floor firmware)
            // rather than returning a misleading empty list.
            ThrowIfErrorOnlyResponse(entries.Count, lines, "read the system log");

            return entries;
        }

        /// <inheritdoc />
        public async Task ClearSystemLogAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            cancellationToken.ThrowIfCancellationRequested();

            var lines = await ExecuteTextCommandAsync(
                () => Send(ScpiMessageProducer.ClearSystemLog),
                responseTimeoutMs: DIAGNOSTICS_RESPONSE_TIMEOUT_MS,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // On success the device echoes a short ack ("Log cleared"); an error-only
            // response means the command failed and must not be swallowed.
            ThrowIfErrorOnlyResponse(0, lines, "clear the system log");
        }

        /// <inheritdoc />
        public async Task<LogLevelSetting> SetLogLevelAsync(string module, int level, CancellationToken cancellationToken = default)
        {
            // Build the command first so argument validation (ArgumentException /
            // ArgumentOutOfRangeException) surfaces the same way regardless of
            // connection state, matching SetAnalogOutput / SetDioDirection.
            var command = ScpiMessageProducer.SetLogLevel(module, level);

            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            cancellationToken.ThrowIfCancellationRequested();

            var lines = await ExecuteTextCommandAsync(
                () => Send(command),
                responseTimeoutMs: DIAGNOSTICS_RESPONSE_TIMEOUT_MS,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (ContainsScpiError(lines))
            {
                throw new DeviceDiagnosticsException(
                    $"The device rejected log level {level} for module '{module}'.",
                    lines);
            }

            if (LogLevelParser.TryParseLines(lines, out var setting))
            {
                return setting;
            }

            throw new DeviceDiagnosticsException(
                $"Setting the log level for module '{module}' returned an unparseable response.",
                lines);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<string>> GetCommandHistoryAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            cancellationToken.ThrowIfCancellationRequested();

            var lines = await ExecuteTextCommandAsync(
                () => Send(ScpiMessageProducer.GetCommandHistory),
                responseTimeoutMs: DIAGNOSTICS_RESPONSE_TIMEOUT_MS,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var commands = CommandHistoryParser.Parse(lines);

            // An empty list is valid ("No command history"), but an error-only
            // response is a failure — distinguish the two. The "No command history"
            // marker is not an error line, so it never trips this check.
            ThrowIfErrorOnlyResponse(commands.Count, lines, "read the command history");

            return commands;
        }

        /// <inheritdoc />
        public async Task TestSystemLogAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            cancellationToken.ThrowIfCancellationRequested();

            var lines = await ExecuteTextCommandAsync(
                () => Send(ScpiMessageProducer.TestSystemLog),
                responseTimeoutMs: DIAGNOSTICS_RESPONSE_TIMEOUT_MS,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // On success the device echoes "Added test log messages"; an error-only
            // response means the command failed and must not be swallowed.
            ThrowIfErrorOnlyResponse(0, lines, "run the system-log self-test");
        }

        /// <inheritdoc />
        public async Task<int> GetSystemErrorCountAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            cancellationToken.ThrowIfCancellationRequested();

            var lines = await ExecuteTextCommandAsync(
                () => Send(ScpiMessageProducer.GetSystemErrorCount),
                responseTimeoutMs: DIAGNOSTICS_RESPONSE_TIMEOUT_MS,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (int.TryParse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
                {
                    return count;
                }
            }

            throw new DeviceDiagnosticsException(
                "The error-count query returned an unparseable response.",
                lines);
        }

        /// <inheritdoc />
        public async Task<StreamStats> GetStreamStatsAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            cancellationToken.ThrowIfCancellationRequested();

            var lines = await ExecuteTextCommandAsync(
                () => Send(ScpiMessageProducer.GetStreamStats),
                responseTimeoutMs: DIAGNOSTICS_RESPONSE_TIMEOUT_MS,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (StreamStatsParser.TryParse(lines, out var stats))
            {
                return stats;
            }

            throw new DeviceDiagnosticsException(
                "The streaming-stats query returned an unparseable response.",
                lines);
        }

        /// <inheritdoc />
        public async Task<MemoryDiagnostics> GetMemoryDiagnosticsAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            cancellationToken.ThrowIfCancellationRequested();

            var lines = await ExecuteTextCommandAsync(
                () => Send(ScpiMessageProducer.GetMemoryDiagnostics),
                responseTimeoutMs: DIAGNOSTICS_RESPONSE_TIMEOUT_MS,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (MemoryDiagnosticsParser.TryParse(lines, out var diagnostics))
            {
                return diagnostics;
            }

            throw new DeviceDiagnosticsException(
                "The memory-diagnostics query returned an unparseable response.",
                lines);
        }
    }
}
