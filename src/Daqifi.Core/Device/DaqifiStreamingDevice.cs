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
        /// <exception cref="InvalidOperationException">Thrown when the device is not connected.</exception>
        public void StartStreaming()
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Device is not connected.");
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

            IsStreaming = true;
            Send(ScpiMessageProducer.StartStreaming(StreamingFrequency));
        }

        /// <summary>
        /// Stops streaming data from the device.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when the device is not connected.</exception>
        public void StopStreaming()
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Device is not connected.");
            }

            if (!IsStreaming) return;

            IsStreaming = false;
            Send(ScpiMessageProducer.StopStreaming);
        }

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
            catch (Exception)
            {
                // A single malformed frame must never tear down the stream or starve other
                // consumers; decoding is best-effort per frame.
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
                throw new InvalidOperationException("Device is not connected.");
            }

            foreach (var channel in SnapshotChannels())
            {
                channel.IsEnabled = false;
            }

            // Push the cleared state for whichever channel types this device actually has.
            SendAdcEnableMask();
            SendDioEnableState();
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
                throw new InvalidOperationException("Device is not connected.");
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
                throw new InvalidOperationException("Device is not connected.");
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
                throw new InvalidOperationException("Device is not connected.");
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
                throw new InvalidOperationException("Device is not connected.");
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
                throw new InvalidOperationException("Device is not connected.");
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
        /// <exception cref="InvalidOperationException">Thrown when the device is not connected.</exception>
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
                throw new InvalidOperationException("Device is not connected.");
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
                throw new InvalidOperationException("Device is not connected.");
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
                throw new InvalidOperationException("Device is not connected.");
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
                throw new InvalidOperationException("Device is not connected.");
            }

            Send(ScpiMessageProducer.SaveAdcCalibration);
        }

        /// <inheritdoc />
        public void LoadAdcCalibration()
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Device is not connected.");
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
                throw new InvalidOperationException("Device is not connected.");
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
                throw new InvalidOperationException("Device is not connected.");
            }

            Send(ScpiMessageProducer.SetAdcCalibrationOffset(channelNumber, calB));
        }

        /// <inheritdoc />
        public void SaveFactoryAdcCalibration()
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Device is not connected.");
            }

            Send(ScpiMessageProducer.SaveFactoryAdcCalibration);
        }

        /// <inheritdoc />
        public void LoadFactoryAdcCalibration()
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Device is not connected.");
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
                throw new InvalidOperationException("Device is not connected.");
            }

            Send(ScpiMessageProducer.UseAdcCalibration(bank));
        }

        /// <inheritdoc />
        public void SaveVoltagePrecision()
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Device is not connected.");
            }

            Send(ScpiMessageProducer.SaveVoltagePrecision);
        }

        /// <inheritdoc />
        public void LoadVoltagePrecision()
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Device is not connected.");
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
                throw new InvalidOperationException("Device is not connected.");
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
                SendAdcEnableMask();
            }

            if (touchedDigital)
            {
                SendDioEnableState();
            }
        }

        /// <summary>
        /// Recomputes the ADC enable bitmask over all currently-enabled analog channels and sends it.
        /// Does nothing when the device has no analog channels. The firmware treats the value as a
        /// set-replace, so the full mask of enabled analog channels is sent every time.
        /// </summary>
        private void SendAdcEnableMask()
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

            if (!hasAnalogChannels)
            {
                return;
            }

            Send(ScpiMessageProducer.EnableAdcChannels(mask.ToString(CultureInfo.InvariantCulture)));
        }

        /// <summary>
        /// Sends the global DIO enable command reflecting whether any digital channel is enabled.
        /// Does nothing when the device has no digital channels. The firmware exposes only a global
        /// DIO enable, so per-channel digital enabling is collapsed to this aggregate state.
        /// </summary>
        private void SendDioEnableState()
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
        /// <param name="configuration">The new network configuration to apply.</param>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the device is not connected.</exception>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="configuration"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when an unsupported WiFi mode or security type is specified.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
        public async Task UpdateNetworkConfigurationAsync(NetworkConfiguration configuration, CancellationToken cancellationToken = default)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!IsConnected)
            {
                throw new InvalidOperationException("Device is not connected.");
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

            // Apply configuration
            Send(ScpiMessageProducer.ApplyNetworkLan);

            // Wait for WiFi module to restart
            await Task.Delay(WIFI_MODULE_RESTART_DELAY_MS, cancellationToken);

            // Re-enable the LAN interface after the reconfig. ApplyNetworkLan restarts the WiFi
            // module, so this is a network-configuration step that OWNS the LAN state and must
            // bring LAN back up regardless of the control transport. It deliberately does NOT call
            // PrepareLanInterface() — that is the transport-aware SD-operation restore, which
            // leaves the LAN alone over WiFi (where #598/#599 keep it up). Here the LAN enable is
            // unconditional.
            Send(ScpiMessageProducer.DisableStorageSd);
            Send(ScpiMessageProducer.EnableNetworkLan);

            // Save configuration to persist across restarts
            Send(ScpiMessageProducer.SaveNetworkLan);

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
        /// <exception cref="InvalidOperationException">Thrown when the device is not connected.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
        public Task LoadNetworkConfigurationAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsConnected)
            {
                throw new InvalidOperationException("Device is not connected.");
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
        /// <exception cref="InvalidOperationException">Thrown when the device is not connected.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
        public Task FactoryResetNetworkAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsConnected)
            {
                throw new InvalidOperationException("Device is not connected.");
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
        /// <exception cref="InvalidOperationException">Thrown when the device is not connected.</exception>
        public void PrepareSdInterface()
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Device is not connected.");
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
        /// <exception cref="InvalidOperationException">Thrown when the device is not connected.</exception>
        public void PrepareLanInterface()
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Device is not connected.");
            }

            Send(ScpiMessageProducer.DisableStorageSd);

            if (IsUsbConnection)
            {
                Send(ScpiMessageProducer.EnableNetworkLan);
            }
        }

        /// <summary>
        /// Minimum firmware for SD-card file transfer (LIST / GET / DELETE) over a WiFi/TCP
        /// connection. Firmware <c>#598/#599</c> (first released <b>v3.7.0</b>) route the SD reply
        /// to the requesting interface; before that the SD card and WiFi contend for the shared SPI
        /// bus, so these operations are USB-only on older firmware. See
        /// <see cref="DeviceFeature.SdFileTransferOverWifi"/> and ADR 0001.
        /// </summary>
        internal static readonly FirmwareVersion SdOverWifiMinFirmware = new(3, 7, 0, null, 0);

        /// <summary>
        /// Guards an SD-card file operation (LIST / GET / DELETE) against the transport it will run
        /// on. Over USB (serial) these are always available on SD-capable firmware. Over WiFi/TCP
        /// they require firmware &gt;= <see cref="SdOverWifiMinFirmware"/> — an unparseable or older
        /// reported version is treated as unsupported and throws a typed, actionable
        /// <see cref="FeatureNotSupportedException"/> up front (ADR 0001) rather than dispatching a
        /// command the firmware cannot service over WiFi (which would stall on the shared SPI bus).
        /// </summary>
        /// <exception cref="FeatureNotSupportedException">
        /// Thrown when the active transport is not USB and the device firmware predates
        /// <see cref="SdOverWifiMinFirmware"/>.
        /// </exception>
        private void EnsureSdFileTransferSupportedOnTransport()
        {
            if (IsUsbConnection)
            {
                return;
            }

            if (!FirmwareVersion.TryParse(Metadata.FirmwareVersion, out var firmware)
                || firmware < SdOverWifiMinFirmware)
            {
                throw new FeatureNotSupportedException(
                    DeviceFeature.SdFileTransferOverWifi,
                    SdOverWifiMinFirmware,
                    Metadata.FirmwareVersion,
                    Metadata.DeviceType == DeviceType.Unknown ? null : Metadata.DeviceType);
            }
        }

        /// <summary>
        /// Retrieves the list of files stored on the device's SD card.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>A task that represents the asynchronous operation, containing the list of files.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the device is not connected.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
        /// <exception cref="SdCardNotPresentException">Thrown when no SD card is installed in the device.</exception>
        /// <exception cref="SdCardFilesystemException">Thrown when the SD card filesystem cannot satisfy the request (corrupt card, unreadable directory).</exception>
        /// <exception cref="SdCardOperationException">Thrown when the device returned an SCPI error that did not match a more specific condition. Empty directories return an empty list rather than throwing.</exception>
        public async Task<IReadOnlyList<SdCardFileInfo>> GetSdCardFilesAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Device is not connected.");
            }

            EnsureSdFileTransferSupportedOnTransport();

            cancellationToken.ThrowIfCancellationRequested();

            // Defensive: always send stop command even if IsStreaming is stale (see issue #118)
            Send(ScpiMessageProducer.StopStreaming);
            IsStreaming = false;

            IReadOnlyList<string> lines;
            try
            {
                lines = await ExecuteTextCommandAsync(async ct =>
                {
                    PrepareSdInterface();

                    // Allow the device firmware to complete the SPI bus switch
                    // before querying the SD card. Without this delay, the device
                    // can return SCPI error -200 (Execution error).
                    await Task.Delay(SD_INTERFACE_SETTLE_DELAY_MS, ct).ConfigureAwait(false);

                    Send(ScpiMessageProducer.GetSdFileList);
                }, responseTimeoutMs: 3000, cancellationToken: cancellationToken);

                // If the response contains a SCPI error (transient timing issue),
                // retry once after an additional settle delay.
                if (ContainsScpiError(lines))
                {
                    for (var retry = 0; retry < SD_LIST_MAX_RETRIES; retry++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        await Task.Delay(SD_INTERFACE_SETTLE_DELAY_MS, cancellationToken);

                        lines = await ExecuteTextCommandAsync(async ct =>
                        {
                            PrepareSdInterface();
                            await Task.Delay(SD_INTERFACE_SETTLE_DELAY_MS, ct).ConfigureAwait(false);
                            Send(ScpiMessageProducer.GetSdFileList);
                        }, responseTimeoutMs: 3000, cancellationToken: cancellationToken);

                        if (!ContainsScpiError(lines))
                        {
                            break;
                        }
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

            ThrowIfSdCardListError(lines);

            var files = SdCardFileListParser.ParseFileList(lines);
            _sdCardFiles = files;
            return files;
        }

        /// <summary>
        /// Retrieves the free and total byte counts of the device's SD card.
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>A task that represents the asynchronous operation, containing the SD card storage info.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the device is not connected or is currently logging to SD card.</exception>
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
                throw new InvalidOperationException("Device is not connected.");
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
            if (lastScpiError != null
                && ScpiResponseClassifier.TryExtractErrorCode(lastScpiError, out var scpiErrorCode)
                && scpiErrorCode == ScpiErrorCodeUndefinedHeader)
            {
                throw new FeatureNotSupportedException(
                    DeviceFeature.SdStorageQuery,
                    MinSupportedFirmware,
                    Metadata.FirmwareVersion,
                    Metadata.DeviceType == DeviceType.Unknown ? null : Metadata.DeviceType);
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
                throw new InvalidOperationException("Device is not connected.");
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
        /// <exception cref="InvalidOperationException">Thrown when the device is not connected.</exception>
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
        /// <exception cref="InvalidOperationException">Thrown when the device is not connected.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
        public async Task<SdCardLoggingSession> StartSdCardLoggingSessionAsync(string? fileName = null, string? channelMask = null, SdCardLogFormat format = SdCardLogFormat.Protobuf, CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Device is not connected.");
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
        /// <exception cref="InvalidOperationException">Thrown when the device is not connected.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
        public Task StopSdCardLoggingAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Device is not connected.");
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
        /// <exception cref="InvalidOperationException">Thrown when the device is not connected or is currently logging to SD card.</exception>
        /// <exception cref="ArgumentException">Thrown when the filename is null, empty, or contains invalid characters.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
        public async Task DeleteSdCardFileAsync(string fileName, CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Device is not connected.");
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
                lines = await ExecuteTextCommandAsync(async ct =>
                {
                    PrepareSdInterface();
                    await Task.Delay(SD_INTERFACE_SETTLE_DELAY_MS, ct).ConfigureAwait(false);
                    Send(ScpiMessageProducer.DeleteSdFile(fileName));
                    Send(ScpiMessageProducer.GetSdFileList);
                }, responseTimeoutMs: 3000, cancellationToken: cancellationToken);

                if (ContainsScpiError(lines))
                {
                    for (var retry = 0; retry < SD_LIST_MAX_RETRIES; retry++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        await Task.Delay(SD_INTERFACE_SETTLE_DELAY_MS, cancellationToken);

                        lines = await ExecuteTextCommandAsync(async ct =>
                        {
                            PrepareSdInterface();
                            await Task.Delay(SD_INTERFACE_SETTLE_DELAY_MS, ct).ConfigureAwait(false);
                            Send(ScpiMessageProducer.DeleteSdFile(fileName));
                            Send(ScpiMessageProducer.GetSdFileList);
                        }, responseTimeoutMs: 3000, cancellationToken: cancellationToken);

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
        /// <exception cref="InvalidOperationException">Thrown when the device is not connected or is currently logging to SD card.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is canceled.</exception>
        public Task FormatSdCardAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Device is not connected.");
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
        /// <exception cref="InvalidOperationException">Thrown when the device is not connected or is not using a USB/serial transport.</exception>
        /// <exception cref="ArgumentException">Thrown when the filename is null, empty, or contains invalid characters.</exception>
        /// <exception cref="SdCardEmptyTransferException">
        /// Thrown when the device serves a marker-only (0-byte) transfer for the file across all
        /// retry attempts, indicating its SD subsystem is not ready rather than the file being
        /// legitimately empty.
        /// </exception>
        public async Task<SdCardDownloadResult> DownloadSdCardFileAsync(
            string fileName,
            Stream destinationStream,
            IProgress<SdCardTransferProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Device is not connected.");
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

            try
            {
                await ExecuteRawCaptureAsync(async (stream, ct) =>
                {
                    // Prepare SD card interface
                    PrepareSdInterface();

                    // Small delay to let the interface switch settle
                    await Task.Delay(50, ct).ConfigureAwait(false);

                    // Send the SCPI command to request the file
                    Send(ScpiMessageProducer.GetSdFile(fileName));

                    // Receive the file data. A marker-only (0-byte) transfer means the device's
                    // SD subsystem wasn't ready when it opened the file - the same kind of
                    // transient condition GetSdCardFilesAsync's LIST retry already absorbs - so
                    // retry the GET a bounded number of times before giving up (see #264).
                    var receiver = new SdCardFileReceiver(stream);
                    long bytesReceived;
                    var attempt = 0;
                    while (true)
                    {
                        try
                        {
                            bytesReceived = await receiver.ReceiveAsync(
                                destinationStream,
                                fileName,
                                progress,
                                timeout: TimeSpan.FromMinutes(30),
                                cancellationToken: ct).ConfigureAwait(false);
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
                }, cancellationToken).ConfigureAwait(false);
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
        /// Downloads a file from the device's SD card over USB to a temporary file.
        /// </summary>
        /// <param name="fileName">The name of the file to download.</param>
        /// <param name="progress">Optional progress reporting.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Metadata about the downloaded file, including the local file path.</returns>
        /// <exception cref="InvalidOperationException">Thrown when the device is not connected or is not using a USB/serial transport.</exception>
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
                throw new InvalidOperationException("Device is not connected.");
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
                throw new InvalidOperationException("Device is not connected.");
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
                throw new InvalidOperationException("Device is not connected.");
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
                throw new InvalidOperationException("Device is not connected.");
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
                throw new InvalidOperationException("Device is not connected.");
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
                throw new InvalidOperationException("Device is not connected.");
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
                throw new InvalidOperationException("Device is not connected.");
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
                throw new InvalidOperationException("Device is not connected.");
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
                throw new InvalidOperationException("Device is not connected.");
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
