using Daqifi.Core.Channel;
using Daqifi.Core.Communication;
using Daqifi.Core.Communication.Consumers;
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
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using static Daqifi.Core.Internal.DiagnosticGuard;

namespace Daqifi.Core.Device;

/// <summary>
/// Represents a DAQiFi device that supports data streaming functionality.
/// Extends the base DaqifiDevice with streaming-specific operations.
/// </summary>
public class DaqifiStreamingDevice : DaqifiDevice, IStreamingDevice, IDeviceIdentification, ILiveSampleSource, INetworkConfigurable, IWifiLinkStatusProvider, ISdCardOperations, ILanChipInfoProvider, IDeviceDiagnostics, IDeviceOperationHost, ISdCardOperationHost, IStreamingSessionHost
{
    /// <summary>
    /// Shortest time <see cref="StartStreamingAsync"/> gives the device to send its first stream
    /// frame before it asks the device why it has not. Covers a fast stream's first frame with
    /// room for host-side scheduling jitter.
    /// </summary>
    private const double MinStartConfirmationMs = 150;

    /// <summary>
    /// Longest that wait, whatever the rate. A 1 Hz stream has not necessarily failed after two
    /// seconds, but a caller must not be made to wait three sample periods for a start either;
    /// the cost of asking early is one text exchange on a stream that was going to be slow
    /// anyway.
    /// </summary>
    /// <remarks>
    /// Measured on the bench NQ1 at firmware 3.8.0, two analog channels, as the time
    /// <see cref="StartStreamingAsync"/> itself takes: 10–23 ms at 100 Hz and above, 213 ms at
    /// 10 Hz. Those starts see their first frame immediately and never reach the queue read. A
    /// 1 Hz start does not: the device's first frame took longer than this bound, so such a
    /// start spends the full budget and then one <c>SYSTem:ERRor?</c> exchange, finds the queue
    /// clean, and carries on — about 2.7 s in all, on top of the capability read. Raising the
    /// bound to cover 1 Hz would buy that one case a faster start and cost every genuinely dead
    /// start a slower diagnosis, which is the case this exists for.
    /// </remarks>
    private const double MaxStartConfirmationMs = 2000;

    /// <summary>
    /// How often <see cref="WaitForStreamFrameAsync"/> looks for the first frame.
    /// </summary>
    private static readonly TimeSpan StartConfirmationPollInterval = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// How many <c>SYSTem:ERRor?</c> queries <see cref="StartStreamingAsync"/> will spend
    /// looking for a refusal of the start. Far below <see cref="DaqifiDevice.DrainErrorQueueAsync"/>'s
    /// own default: this runs on a device that may yet begin streaming, so the exchange count is
    /// bounded tightly rather than generously.
    /// </summary>
    private const int StartConfirmationErrorQueueReads = 4;

    /// <summary>
    /// How often a device that gave no readable answer is asked whether it is streaming.
    /// </summary>
    private const int StreamingStateQueryAttempts = 2;

    /// <summary>
    /// How long to wait for the device to say whether it is streaming after a start. The firmware
    /// reads a flag held in RAM, so this only needs to cover the round trip.
    /// </summary>
    private static readonly TimeSpan StartedStateResponseTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long to wait for the first answer after a stop. The firmware answers nothing until the
    /// stop has finished, and gives itself 5 s to close an SD log, plus 0.2 s for its encoder and
    /// 0.5 s to flush USB. Not shortened when no log is known to be open here: another connection
    /// may have opened one.
    /// </summary>
    private static readonly TimeSpan StoppedStateResponseTimeout = TimeSpan.FromSeconds(7);

    /// <summary>
    /// How long to wait for the acknowledgement of a reboot sent to an idle device. The firmware
    /// writes it before doing anything else; measured at 23 ms over USB (FW 3.9.1).
    /// </summary>
    private static readonly TimeSpan RebootResponseTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long the reader is left decoding frames after a stop, before the device is asked
    /// anything: frames already on their way would otherwise be read as reply lines. Measured over
    /// USB (FW 3.9.1, 100 and 1000 Hz), the last one lands within 25 ms of the stop.
    /// </summary>
    private static readonly TimeSpan StoppedStreamSettleDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// The line the firmware writes when it accepts <c>SYSTem:REboot</c> (<c>SCPI_Reset</c>),
    /// before it begins the reset.
    /// </summary>
    private const string RebootAcknowledgement = "System reset initiated";

    /// <summary>
    /// What <see cref="StartStreamingAsync"/>'s last capability read left behind: the document it
    /// put in <see cref="DeviceMetadata.CapabilityDocument"/> and how many analog inputs were
    /// enabled when it was read. <c>null</c> before the first read.
    /// </summary>
    /// <remarks>
    /// The device computes its rate cap for the set enabled at the moment of the read, so the
    /// count says whether the cap still describes the device — see
    /// <see cref="RefreshSampleRateCapAsync"/>. The document is kept beside it because any other
    /// read (initialization on a reconnect, or a caller's own
    /// <see cref="DaqifiDevice.ReadCapabilityDocumentAsync"/>) replaces the cap without updating
    /// the count; a count left describing a larger set than the cap was read for would then
    /// wave through a start the cap no longer covers. When the metadata holds a different
    /// document from the one recorded here, the record is about a cap that is gone.
    /// </remarks>
    private CapabilityRead? _lastCapabilityRead;

    /// <summary>
    /// How many capability reads one start makes while the channel set keeps changing under them.
    /// </summary>
    private const int MaxCapabilityReadsPerStart = 3;

    private sealed record CapabilityRead(CapabilityDocument? Document, int AnalogInputCount);

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
    /// <remarks>
    /// Goes false when the connection ends, by a disconnect or a lost link, as well as when the
    /// stream is stopped.
    /// </remarks>
    public bool IsStreaming => _session.IsStreaming;

    /// <summary>
    /// Gets or sets the streaming frequency in Hz (samples per second). The value is
    /// validated against the device's advertised maximum sampling rate
    /// (<see cref="DeviceCapabilities.MaxSamplingRate"/>) so a silently-wrong rate never
    /// reaches the hardware — consistent with the client-side guards Core already applies
    /// to PWM (#306) and channel bounds (#300).
    /// </summary>
    /// <remarks>
    /// A new device object starts at 100 Hz. On firmware v3.9.0 and newer
    /// (<see cref="DeviceFeature.StoredStreamingRateQuery"/>), initialization replaces that with
    /// the rate the device has stored — the one another client, an SD log, or an earlier session
    /// last started it at — so this reports the device's rate rather than Core's guess (#960).
    /// That happens only while no rate has been chosen on this object: once it is set here or a
    /// stream is started, the chosen rate stands, and an automatic reconnect resumes at it whatever
    /// the device reports. A device that has not been started since power-up reports a 1 Hz
    /// placeholder, which is not adopted: the 100 Hz default stands for it, as it does below
    /// v3.9.0, where the device cannot be asked. A 1 Hz that somebody did start at is adopted; the
    /// capability document tells the two apart. A stored rate was accepted for the stream that set
    /// it, so while the rate is still the inherited one, an SD log start lowers it to the most the
    /// device says the card can take before sending it, or to 100 Hz when the device does not
    /// say; a chosen rate is sent as it is.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the value is less than 1 or greater than the device's maximum sampling rate.
    /// </exception>
    public int StreamingFrequency
    {
        get => _session.StreamingFrequency;
        set => _session.StreamingFrequency = value;
    }

    /// <summary>
    /// Gets the highest streaming frequency in Hz this device can actually sustain with the
    /// channels it has enabled right now.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from the <see cref="StreamingFrequency"/> setter's own validation,
    /// which stays on the absolute board ceiling: this figure moves with the channel set, and a
    /// setter that rejected against it would fail a perfectly reasonable
    /// "set the rate, then enable the channels" ordering. Nothing needs to be called to have it
    /// applied — <see cref="StartStreaming"/> lowers the rate to this figure itself, so a rate
    /// above it can no longer reach the device.
    /// </remarks>
    public int MaximumStreamingFrequencyHz => SampleRateCap.ComputeForDevice(this);

    /// <summary>
    /// Adopts the rate the device reported it has stored, unless a rate has been chosen on this
    /// device; see <see cref="StreamingFrequency"/>.
    /// </summary>
    internal override void OnStoredStreamingRateRead(int rateHz, bool? rateConfigured) =>
        _session.TryAdoptStoredFrequency(rateHz, rateConfigured);

    /// <summary>
    /// Gets a value indicating whether the device is currently logging data to the SD card.
    /// </summary>
    /// <remarks>
    /// A logging session is also a stream, so <see cref="IsStreaming"/> is <c>true</c> whenever
    /// this is. It goes false when the session ends, by <see cref="StopSdCardLoggingAsync"/>,
    /// <see cref="StopStreaming"/>, or a stop command sent any other way, including the one
    /// <see cref="DaqifiDevice.InitializeAsync"/> sends. It also goes false when the connection
    /// ends, by a disconnect or a lost link: this object then knows nothing of what the device is
    /// doing, although nothing has told the device to stop.
    /// </remarks>
    public bool IsLoggingToSdCard => _session.IsLoggingToSdCard;

    /// <summary>
    /// Gets a value indicating whether the device is connected over USB (serial transport).
    /// The SD card and the WiFi/LAN module share one SPI bus, so this decides how every SD
    /// operation prepares that bus, what a silent transport means to a file transfer, and
    /// whether SD logging can be started at all.
    /// </summary>
    /// <remarks>
    /// Asks the transport (<see cref="IStreamTransport.IsUsbConnection"/>) rather than testing its
    /// concrete type, so a custom <see cref="IStreamTransport"/> over a USB link gets the right
    /// answer by overriding that member. <c>false</c> when the device has no transport.
    /// </remarks>
    public virtual bool IsUsbConnection => Transport?.IsUsbConnection == true;

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
    /// Initializes a new instance of the <see cref="DaqifiStreamingDevice"/> class with no transport.
    /// </summary>
    /// <remarks><inheritdoc cref="DaqifiDevice(string)" path="/remarks/node()"/></remarks>
    /// <param name="name">The name of the device.</param>
    protected DaqifiStreamingDevice(string name)
        : this(name, logger: null)
    {
    }

    /// <inheritdoc cref="DaqifiStreamingDevice(string)"/>
    /// <param name="name">The name of the device.</param>
    /// <param name="logger">Logger for device diagnostics; a no-op logger is used when null.</param>
    protected DaqifiStreamingDevice(string name, ILogger? logger)
        : base(name, logger)
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
        //
        // The session controller is built first because IsStreaming and StreamingFrequency now
        // read through it, and both the default-rate assignment at the bottom of this
        // method and anything a collaborator's constructor might read would otherwise find it
        // null.
        _session = new StreamingSessionController(this);

        _frameDecoder = new StreamFrameDecoder(this);
        _liveSampleStream = new LiveSampleStream(this);
        _channelControl = new ChannelControlOperations(this);
        _administration = new DeviceAdministrationOperations(this);
        _networkOperations = new NetworkConfigurationOperations(this);
        _wifiLinkStatusOperations = new WifiLinkStatusOperations(this);
        _sdCardOperations = new SdCardOperations(this);
        _lanChipInfoOperations = new LanChipInfoOperations(this);
        _diagnosticsOperations = new DeviceDiagnosticsOperations(this);

        _session.SetDefaultFrequency(StreamingSessionController.CoreDefaultFrequencyHz);

    }

    /// <summary>
    /// Routes the stream to USB, as part of the commands <see cref="DaqifiDevice.InitializeAsync"/>
    /// uses to put the device into a known state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The firmware keeps the stream interface it was last given, across sessions. A device a
    /// WiFi session left on <c>SYSTem:STReam:INTerface 1</c> still sends everything over WiFi,
    /// so a USB connect receives nothing — not even the status reply the connect waits for. The
    /// command therefore has to go out with the rest of the setup block, before the device is
    /// asked to describe itself, or the connect times out before the routing is ever repaired
    /// (#897).
    /// </para>
    /// <para>
    /// The command writes global stream state, so the base class does not call this at all for
    /// an observing initialization (#385). A connection that is not USB has nothing to route.
    /// </para>
    /// </remarks>
    private protected override void OnSendingInitializationCommands()
    {
        if (IsUsbConnection)
        {
            Send(ScpiMessageProducer.SetStreamInterface(StreamInterface.Usb));
        }
    }

    /// <summary>
    /// Starts streaming data from the device at the configured frequency, lowered first to what
    /// the enabled channel set can sustain (<see cref="MaximumStreamingFrequencyHz"/>).
    /// </summary>
    /// <remarks>
    /// The cap is applied from the figures the device has already reported. Prefer
    /// <see cref="StartStreamingAsync"/>, which re-reads them for the channel set about to
    /// stream and then reads the device's verdict on the start; this overload sends no text
    /// command and so can do neither.
    /// </remarks>
    /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
    /// <exception cref="Daqifi.Core.Communication.Consumers.ConsumerThreadNotExitedException">
    /// Thrown when an earlier SD file download left the device's message reader stopped and it
    /// cannot start again, because its stream is stuck. The start command is not sent, since
    /// nothing would read the stream; reconnect the device (issue #975).
    /// </exception>
    public void StartStreaming() => _session.StartStreaming();

    /// <summary>
    /// Stops streaming data from the device.
    /// </summary>
    /// <remarks>
    /// During an SD card logging session this ends the session completely, the same as
    /// <see cref="StopSdCardLoggingAsync"/>: it sends the same commands in the same order, so the
    /// card is free and the device is configured for live streaming again.
    /// </remarks>
    /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
    public void StopStreaming() => _session.StopStreaming();

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Three steps, and the first two are what keep the third from ever firing. The capability
    /// document is re-read so <see cref="MaximumStreamingFrequencyHz"/> describes the channel
    /// set about to stream rather than the one enabled at the last read;
    /// <see cref="StartStreaming"/> then lowers the rate to it. Only after that is the device
    /// asked anything, so a start the firmware refused for any other reason reaches the caller
    /// as a <see cref="DeviceCommandFailedException"/> instead of being left on the device for
    /// the next connect to trip over (issues #899 and #898).
    /// </para>
    /// <para>
    /// Asking is a text exchange, which stops the reader decoding protobuf frames for its
    /// duration, so it runs only once the device has had its chance to send a frame and has sent
    /// none. A stream that is delivering data is never interrupted by it. When the device refused
    /// the start, its error code is in the exception; when it accepted it but reports that it is
    /// not streaming, <see cref="DeviceCommandFailedException.ErrorCode"/> is <c>null</c>.
    /// </para>
    /// <para>
    /// A stream another caller stops while this is waiting is left stopped, and this ends as
    /// cancelled: the device never confirmed it, and nothing failed. The same goes for one that is
    /// stopped and started again: the new stream is the other caller's, and is left alone.
    /// </para>
    /// </remarks>
    /// <exception cref="Daqifi.Core.Communication.Consumers.ConsumerThreadNotExitedException">
    /// Thrown when the device's message reader is stopped and cannot start again; see
    /// <see cref="StartStreaming"/>.
    /// </exception>
    public async Task StartStreamingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await RefreshSampleRateCapAsync(cancellationToken).ConfigureAwait(false);

        // Evidence that the device is streaming has to be newer than this call. A new session
        // counts its frames from zero, so what must not be there is a frame of the stream before
        // it; a running one has frames already, and those say only that it was streaming once.
        var stopWaitedFor = _session.LastStopSent;
        if (!IsStreaming
            && _session.RemainingSettleTime(StoppedStreamSettleDelay) is var settle
            && settle > TimeSpan.Zero)
        {
            await Task.Delay(settle, Clock, cancellationToken).ConfigureAwait(false);
        }

        var (session, startSent) = _session.StartStreaming();
        var framesBefore = startSent || _session.IsStartHeldBack(session) ? 0L : _frameDecoder.AdmittedStreamFrameCount;

        Exception? confirmationFailure = null;
        try
        {
            await ConfirmStreamStartedAsync(session, startSent, stopWaitedFor, framesBefore, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            confirmationFailure = ex;
        }

        // A start parked behind an operation on another flow is dropped, not sent, when the backlog
        // overflows, or when that operation was a raw capture that cannot restart the reader. The wait then sees the session end, or the exchange
        // that follows it fails on the same stuck reader; neither is a refusal, so the reason the
        // start never reached the device goes out in place of either (issue #975).
        if (session.StartNotSent is { } notSent)
        {
            if (notSent is not (ConsumerThreadNotExitedException or DeviceNotConnectedException))
            {
                throw DeviceCommandFailedException.NotSent(
                    ScpiMessageProducer.StartStreaming(StreamingFrequency).Data.Trim(), notSent);
            }

            ExceptionDispatchInfo.Capture(notSent).Throw();
        }

        if (confirmationFailure != null)
        {
            ExceptionDispatchInfo.Capture(confirmationFailure).Throw();
        }
    }

    /// <summary>
    /// Re-reads the capability document so the sample-rate cap describes the channel set that
    /// is about to stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The device computes <see cref="CapabilityStreaming.CurrentMaximumRateHz"/> for the set
    /// enabled at the moment of the read, so the figure goes stale the moment a caller enables
    /// or disables a channel — which is exactly what a caller does just before starting a
    /// stream. Without this the cap applied at the start could be the one from connect time.
    /// </para>
    /// <para>
    /// Best-effort, and silent when it cannot be done: a device whose firmware predates the
    /// document (v3.5.0) does not even send the query, and a read that fails leaves the
    /// previously-known figures in place, which is a worse cap than a fresh one but a far
    /// better one than none. The start itself still runs, and still applies whatever cap is
    /// known. Skipped entirely while a stream is running, both because the start is then a
    /// no-op and because the read is a text exchange that would interrupt the live stream.
    /// </para>
    /// <para>
    /// <b>Why only when the analog-input count has grown.</b> A text exchange on the bench NQ1
    /// costs about a second, which is far too much to add to every start. It is also only ever
    /// needed in one direction: the cap falls as analog inputs are enabled, so a set no larger
    /// than the one the document was read for has a true cap no lower than the known one, and
    /// clamping to the known figure is safe — at worst conservative. A set that has <i>grown</i>
    /// is the one case where the known figure can be too high, which is exactly the case that
    /// produces a dead stream, and that is when the read is paid for. In practice that is the
    /// first start after channels are enabled, and not the ones after it.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">A cancellation token to observe while reading.</param>
    private async Task RefreshSampleRateCapAsync(CancellationToken cancellationToken)
    {
        if (IsStreaming || Transport == null)
        {
            return;
        }

        if (_lastCapabilityRead is { } lastRead
            && ReferenceEquals(lastRead.Document, Metadata.CapabilityDocument)
            && EnabledAnalogInputCount() <= lastRead.AnalogInputCount)
        {
            return;
        }

        try
        {
            // The device computes the cap for whatever set is enabled at the moment it answers,
            // which is unknowable if the set changed while the read was out, in either direction.
            // Such a read vouches for nothing, and since this start is about to rely on the cap
            // it is asked again for the set as it is now — a few times, then the start goes ahead
            // on the last answer rather than never starting while a caller keeps reconfiguring.
            for (var read = 1; read <= MaxCapabilityReadsPerStart; read++)
            {
                var channelStateBefore = ChannelStateVersion;
                var analogInputsQueried = EnabledAnalogInputCount();
                var document = await ReadCapabilityDocumentAsync(cancellationToken).ConfigureAwait(false);
                if (ChannelStateVersion != channelStateBefore)
                {
                    continue;
                }

                // Recorded when the read produced the cap now in force: a fresh document, or no
                // document on a device that has none at all, which asking again with the same set
                // would only fail to produce again. Not recorded when the read returned nothing
                // but an older document is still in the metadata (a malformed or incomplete
                // reply), since that cap describes an earlier channel set and must not be vouched
                // for under this one. Not recorded when the read threw either; both are retried
                // on the next start.
                if (document is not null || Metadata.CapabilityDocument is null)
                {
                    _lastCapabilityRead = new CapabilityRead(document, analogInputsQueried);
                }

                break;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SafeTrace(() =>
                $"[{nameof(StartStreamingAsync)}] Could not re-read the capability document; "
                + $"the sample-rate cap may describe an earlier channel set: {ex.Message}");
        }
    }

    /// <summary>
    /// Waits for the device to prove it is streaming, and asks it when it does not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The start command is fire-and-forget, so a refusal used to be invisible: the device
    /// answered SCPI <c>-222</c>, Core reported <see cref="IsStreaming"/> as <c>true</c>, and
    /// not one sample ever arrived (issue #899). The refusal then sat in the queue and broke the
    /// next connect (issue #898).
    /// </para>
    /// <para>
    /// The queue read cannot simply follow the start command, because it is a text exchange and
    /// a text exchange switches the device's reader from decoding protobuf to framing reply
    /// lines — on a healthy stream that would throw away the samples the caller asked for, and
    /// the binary frames arriving meanwhile would be read as reply lines. So the arrival of a
    /// single frame is taken as proof and ends the wait, and only a device that sent nothing at
    /// all is questioned. A refused start sends nothing, so it is always questioned.
    /// </para>
    /// <para>
    /// This ordering survived the removal of the consumer swap (issue #900) on purpose. One
    /// reader now serves both kinds of traffic, but it frames one kind at a time: the exchange
    /// tells it which, rather than letting it guess from the bytes, because a wrong guess on this
    /// wire destroys data (#268). So the reader still cannot decode a stream frame and a reply
    /// line in the same instant, and an error-queue read on a delivering stream would still cost
    /// that stream its frames for the exchange's duration.
    /// </para>
    /// <para>
    /// The budget scales with the rate, because "nothing yet" only means something after a
    /// sample period or two, and a quick restart can have its first two frames discarded as
    /// possible leftovers (<see cref="StreamFrameGate.LeftoverWindowSamplePeriods"/>): four periods,
    /// held between <see cref="MinStartConfirmationMs"/> and <see cref="MaxStartConfirmationMs"/>. A
    /// delivering stream returns from here as soon as its first frame lands, which at any rate
    /// above a few hertz is a handful of milliseconds.
    /// </para>
    /// <para>
    /// Two questions, in this order. The error queue first, because it says why: only a reply
    /// that parses as a non-zero SCPI error counts as a refusal. A clean queue, or one that could
    /// not be read, says nothing either way, so then the device is asked outright whether it is
    /// streaming. A slow stream answers yes here long before its first frame; a device with no
    /// analog channels enabled, which does not always start and does not always say why, answers
    /// no.
    /// </para>
    /// </remarks>
    /// <param name="session">
    /// The session being confirmed. Evidence counts, and a refusal ends a session, only while this
    /// is still the current one.
    /// </param>
    /// <param name="startSent">
    /// Whether this call sent the start. When it did not, the error queue is not this call's to
    /// judge: whatever it holds came from something else.
    /// </param>
    /// <param name="stopWaitedFor">The last stop before this call's start, whose frames it has waited for.</param>
    /// <param name="framesBefore">How many frames of the session predate the call and so prove nothing.</param>
    /// <param name="cancellationToken">A cancellation token to observe while waiting.</param>
    /// <exception cref="DeviceCommandFailedException">
    /// Thrown when the device refused the start, reports that it is not streaming, or did not answer.
    /// </exception>
    private async Task ConfirmStreamStartedAsync(
        StreamingSession session,
        bool startSent,
        object? stopWaitedFor,
        long framesBefore,
        CancellationToken cancellationToken)
    {
        if (await WaitForStreamFrameAsync(
                session, startSent, stopWaitedFor, framesBefore, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var command = ScpiMessageProducer.StartStreaming(StreamingFrequency).Data.Trim();

        if (startSent && await ReadStartRefusalAsync(cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            // The device is not streaming, whatever the flag says, so put the session back before
            // the exception leaves: a device reported as streaming while it sits idle also makes
            // the next StartStreaming() a silent no-op.
            MarkStartRefusedOrThrowIfSessionEnded(session);

            throw ScpiResponseClassifier.TryParseSystemErrorReplyCode(refusal, out var code) && code != 0
                ? new DeviceCommandFailedException(command, code, refusal)
                : new DeviceCommandFailedException(command, refusal);
        }

        var (isStreaming, reply) = await QueryStreamingStateAsync(
            StartedStateResponseTimeout, cancellationToken).ConfigureAwait(false);

        // Stopped from another thread while the device was being asked: its "no" is that stop, not
        // a refused start.
        ThrowIfSessionEnded(session);

        if (isStreaming == true)
        {
            return;
        }

        if (isStreaming == null)
        {
            throw DeviceCommandFailedException.Unconfirmed(command, reply);
        }

        // An SD log the device has ended by itself still owns the card's bus on this side;
        // StopSdCardLoggingAsync is what hands that back.
        if (session.Kind != StreamingSessionKind.SdCardLog)
        {
            MarkStartRefusedOrThrowIfSessionEnded(session);
        }

        throw DeviceCommandFailedException.Contradicted(
            command, "reports that it is not streaming", reply!);
    }

    /// <summary>
    /// Throws when the session being confirmed is no longer the current one.
    /// </summary>
    /// <exception cref="DeviceNotConnectedException">The connection ended, and the session with it.</exception>
    /// <exception cref="OperationCanceledException">Another caller stopped the stream, or stopped and restarted it.</exception>
    private void ThrowIfSessionEnded(StreamingSession session)
    {
        if (!ReferenceEquals(_session.Current, session))
        {
            ThrowSessionEnded();
        }
    }

    /// <summary>
    /// Ends <paramref name="session"/> because the device is not streaming it, or throws as
    /// <see cref="ThrowIfSessionEnded"/> does when it is no longer the one to end.
    /// </summary>
    private void MarkStartRefusedOrThrowIfSessionEnded(StreamingSession session)
    {
        if (!_session.TryMarkStartRefused(session))
        {
            ThrowSessionEnded();
        }
    }

    private void ThrowSessionEnded()
    {
        EnsureConnected();

        throw new OperationCanceledException(
            "The stream was stopped before the device had confirmed that it started.");
    }

    /// <summary>
    /// Reads the device's error queue for a refusal of the start that has just been sent.
    /// </summary>
    /// <returns>The refusal, or <c>null</c> when the queue holds none or could not be read.</returns>
    private async Task<string?> ReadStartRefusalAsync(CancellationToken cancellationToken)
    {
        try
        {
            return LastScpiErrorReply(
                await DrainErrorQueueAsync(StartConfirmationErrorQueueReads, cancellationToken)
                    .ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Says nothing about the start, and the device is about to be asked directly. If the
            // link is the problem, that question reports it.
            SafeTrace(() =>
                $"[{nameof(StartStreamingAsync)}] The device sent no stream frame and its error "
                + $"queue could not be read: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Asks the device whether it is streaming (<c>SYSTem:StreamData?</c>).
    /// </summary>
    /// <remarks>
    /// Asked again when no answer could be read. Stream frames that are still arriving are framed
    /// as text during the exchange, and one that runs into the reply makes that reply unreadable;
    /// a lost reply is then worth one more question, and a device that is really silent is not
    /// made to look like it.
    /// </remarks>
    /// <param name="responseTimeout">How long to wait for the first line of each answer.</param>
    /// <param name="cancellationToken">A cancellation token to observe.</param>
    /// <returns>
    /// The device's answer and the line it was in; <c>null</c> and the last SCPI error the device
    /// reported, if any, when it gave none.
    /// </returns>
    private async Task<(bool? IsStreaming, string? Reply)> QueryStreamingStateAsync(
        TimeSpan responseTimeout,
        CancellationToken cancellationToken)
    {
        string? error = null;

        for (var attempt = 1; attempt <= StreamingStateQueryAttempts; attempt++)
        {
            var lines = await ExecuteTextCommandAsync(
                () => Send(ScpiMessageProducer.GetStreamingState),
                responseTimeout: responseTimeout,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // The last one, because a reply to an earlier question can only precede this one's.
            for (var i = lines.Count - 1; i >= 0; i--)
            {
                // The firmware answers exactly 1 or 0 (SCPI_IsStreaming).
                switch (lines[i].Trim())
                {
                    case "1":
                        return (true, "1");
                    case "0":
                        return (false, "0");
                }
            }

            error = ScpiResponseClassifier.GetLastScpiErrorLine(lines) ?? error;
        }

        return (null, error);
    }

    /// <summary>
    /// Waits until the device has sent a stream frame beyond the first
    /// <paramref name="framesBefore"/> of the session, or the confirmation budget for the current
    /// rate runs out.
    /// </summary>
    /// <param name="session">The session whose frame is waited for.</param>
    /// <param name="startSent">Whether the caller sent its start.</param>
    /// <param name="stopWaitedFor">The stop whose frames the caller has waited for before that.</param>
    /// <param name="framesBefore">How many of the session's frames do not count.</param>
    /// <param name="cancellationToken">A cancellation token to observe while waiting.</param>
    /// <returns><c>true</c> when a frame arrived; <c>false</c> when the budget ran out first.</returns>
    private async Task<bool> WaitForStreamFrameAsync(
        StreamingSession session,
        bool startSent,
        object? stopWaitedFor,
        long framesBefore,
        CancellationToken cancellationToken)
    {
        var budgetMs = Math.Clamp(
            4000.0 / Math.Max(1, StreamingFrequency),
            MinStartConfirmationMs,
            MaxStartConfirmationMs);

        var startedAt = Clock.GetTimestamp();

        while (true)
        {
            // Read before the count, which starts again when a held-back start goes out: until then
            // the frames arriving are those of the stream before it. So can those be that arrive
            // right after a stop. This call has waited for them before its own start, but a stop
            // that was held back can go out right ahead of that.
            var proveNothing = _session.IsStartHeldBack(session)
                || ((!startSent || !ReferenceEquals(_session.LastStopSent, stopWaitedFor))
                    && _session.RemainingSettleTime(StoppedStreamSettleDelay) > TimeSpan.Zero);
            var admitted = _frameDecoder.AdmittedStreamFrameCount;

            // Checked after the count is read, so a frame of a later session cannot be taken for
            // one of this. A stream stopped from another thread while this waited is not a refused
            // start, so there is nothing to ask the device about either.
            ThrowIfSessionEnded(session);

            if (proveNothing)
            {
                framesBefore = admitted;
            }
            else if (admitted > framesBefore)
            {
                return true;
            }
            else
            {
                // The count starts again when a held-back start goes out.
                framesBefore = Math.Min(framesBefore, admitted);
            }

            if (Clock.GetElapsedTime(startedAt).TotalMilliseconds >= budgetMs)
            {
                return false;
            }

            await Task.Delay(StartConfirmationPollInterval, Clock, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The last line in <paramref name="lines"/> that is a <c>SYSTem:ERRor?</c> reply reporting
    /// a real error, or <c>null</c> when none is.
    /// </summary>
    /// <remarks>
    /// Every line is classified rather than the first one taken, because a drained queue can
    /// hand back more than one entry and because an unreadable line must never be reported as a
    /// refusal. The last is chosen for the same reason
    /// <see cref="ScpiResponseClassifier.GetLastScpiErrorLine"/> does: the most recent entry is
    /// the one closest to the command that was just sent.
    /// </remarks>
    private static string? LastScpiErrorReply(IReadOnlyList<string> lines)
    {
        string? last = null;

        foreach (var line in lines)
        {
            if (ScpiResponseClassifier.IsSystemErrorReplyLine(line)
                && ScpiResponseClassifier.TryParseSystemErrorReplyCode(line, out var code)
                && code != 0)
            {
                last = line;
            }
        }

        return last;
    }

    /// <summary>
    /// How many analog <i>input</i> channels are enabled right now, counting an enable that is held
    /// back, which a start goes out behind — the one figure the device's sample-rate cap moves with.
    /// </summary>
    /// <remarks>
    /// Analog outputs are not streamed and digital pins are captured on the analog sample tick
    /// rather than driving one of their own, which is why neither counts here; see
    /// <see cref="SampleRateCap"/>.
    /// </remarks>
    private int EnabledAnalogInputCount() => GetChannelsSnapshot().Count(channel =>
        channel.Type == ChannelType.Analog && IsEnabledAheadOfNextSend(channel));

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Sends what <see cref="StopStreaming"/> sends, including the whole of the sequence that ends
    /// an SD card log, then asks the device whether it is streaming. The firmware runs commands
    /// one at a time and answers the question only when the stop has finished, so the answer is
    /// about the stop and not about a moment before it.
    /// </para>
    /// <para>
    /// If the device says it is still streaming, which takes another connection restarting it,
    /// <see cref="IsStreaming"/> goes back to what it was. If it does not answer, the stop was
    /// sent and <see cref="IsStreaming"/> is <c>false</c>; call this again to ask again.
    /// </para>
    /// <para>
    /// If another caller of this object starts the stream again while this is waiting and the
    /// device answers that it is streaming, this ends as cancelled: the answer is about the new
    /// stream, which is left alone.
    /// </para>
    /// </remarks>
    public async Task StopStreamingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureConnected();

        await RunExclusiveAsync(
            async ct =>
            {
                var (running, stopped) = _session.StopStreamingInAnyCase();

                await Task.Delay(StoppedStreamSettleDelay, Clock, ct).ConfigureAwait(false);

                var (isStreaming, reply) = await QueryStreamingStateAsync(
                    StoppedStateResponseTimeout, ct).ConfigureAwait(false);

                var command = ScpiMessageProducer.StopStreaming.Data;
                if (isStreaming == null)
                {
                    throw DeviceCommandFailedException.Unconfirmed(command, reply);
                }

                if (isStreaming.Value)
                {
                    if (!_session.TryMarkStopNotTaken(stopped, running))
                    {
                        EnsureConnected();

                        throw new OperationCanceledException(
                            "Another caller used the stream before the device had confirmed that it stopped.");
                    }

                    throw DeviceCommandFailedException.Contradicted(
                        command, "reports that it is still streaming", reply!);
                }
            },
            cancellationToken).ConfigureAwait(false);
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
    /// define a session, and <see cref="StreamingSessionController.SendTracked"/>
    /// applies what they imply — the same state <see cref="StartStreaming"/> /
    /// <see cref="StopStreaming"/> and <see cref="EnableChannel"/> would have set. See that
    /// method for exactly what equivalence with the typed API covers.
    /// </para>
    /// <para>
    /// Only those commands are interpreted, and only after the send itself has succeeded.
    /// Everything else passes through untouched.
    /// </para>
    /// <para>
    /// A stream start or stop that is held back behind an operation on another flow keeps its place
    /// among the ones that operation sends: a start that a later stop has overtaken is not sent, and
    /// a stop goes out ahead of a later start.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The type of the message data payload.</typeparam>
    /// <param name="message">The message to send.</param>
    public override void Send<T>(IOutboundMessage<T> message)
    {
        if (message is TrackedMessage tracked)
        {
            // A channel command, whose state its sender keeps.
            SendOrHoldBack(message, tracked.Command);
        }
        else if (message is IOutboundMessage<string> textCommand)
        {
            _session.SendTracked(textCommand.Data, session => SendOrHoldBack(message, session));
        }
        else
        {
            base.Send(message);
        }
    }

    #endregion

    #region Session restore after an automatic reconnect (issue #379)

    /// <inheritdoc />
    private protected override void CaptureSessionSnapshot() => _session.CaptureSessionSnapshot();

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
    private protected override Task<bool> RestoreSessionSnapshotAsync(
        ReconnectOptions options,
        CancellationToken cancellationToken)
        => _session.RestoreSessionSnapshotAsync(options, cancellationToken);

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
    /// Enumerations currently subscribed to this device's channels. Internal test seam: it lets
    /// a test wait for a live consumer to actually be listening rather than sleep for a guessed
    /// interval, which is a race on a loaded machine.
    /// </summary>
    internal int LiveSubscriptionCount => _liveSampleStream.LiveSubscriptionCount;

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
    /// session starts a new one once <see cref="DaqifiDevice.State"/> reads
    /// <see cref="DeviceState.Ready"/> again. Ending is what makes the outcome the same
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
    /// <param name="bufferCapacity">
    /// Bounded buffer capacity; defaults to <see cref="DefaultLiveSampleBufferCapacity"/> when null.
    /// </param>
    /// <param name="cancellationToken">Ends enumeration when cancelled.</param>
    /// <returns>An async stream of <see cref="LiveSample"/> (channel + decoded sample).</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bufferCapacity"/> is less than 1.</exception>
    /// <exception cref="DeviceNotConnectedException">
    /// The device is not connected when enumeration starts, or the connection was lost while it
    /// was running. Both are raised from <c>MoveNextAsync</c>, after any buffered samples.
    /// </exception>
    public IAsyncEnumerable<LiveSample> StreamSamplesAsync(
        int? bufferCapacity = null,
        CancellationToken cancellationToken = default)
        => _liveSampleStream.StreamSamplesAsync(bufferCapacity, cancellationToken);

    /// <summary>
    /// Ends what belongs to a session when the device stops being connected: the live-sample
    /// enumerations (issue #496), the recorded stream session, and the PWM state last sent.
    /// </summary>
    /// <remarks>
    /// Everything else the streaming device owns is either driven by inbound frames — which stop
    /// on their own — or torn down by the disconnect itself.
    /// </remarks>
    internal override void OnStateChanged(DeviceState previous, DeviceState current)
    {
        base.OnStateChanged(previous, current);

        _liveSampleStream.OnStateChanged(current);

        if (current.IsConnected())
        {
            return;
        }

        // Clear the "already-sent" PWM frequency cache on any transition to a state without a
        // session — an intentional Disconnected as well as an unexpected drop (which sets Lost, not
        // Disconnected) and the Retrying/Failed states. After any of these the device's runtime
        // PWM state is no longer trustworthy, so a reconnect on the same instance must re-send.
        // See #345.
        _channelControl.ResetSentPwmFrequency();

        // The same goes for which channels are in PWM mode (#945).
        _channelControl.ResetPwmEnabled();

        // The stream session ends with the connection too, for the same reason: nothing stopped
        // it, and what the device does from here is unknown. See #940.
        _session.EndSessionOnConnectionEnd();
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
            // Compose inside the guard. `ex` came from a subscriber, so rendering it is no safer
            // than raising to it: an eager SafeTrace($"...{ex}") lets a throwing ToString escape
            // this catch. Same reason as SerialStreamTransport.HandleConnectionLost.
            SafeTrace(() => $"[{nameof(StreamFrameDiscarded)}] Subscriber threw: {ex}");
        }
    }

    /// <summary>
    /// Raises <see cref="GapDetected"/>, isolating the decode pipeline from a subscriber
    /// exception so a throwing handler cannot skip this frame's per-channel decode (which the
    /// per-frame catch in <see cref="StreamFrameDecoder.EmitStreamFrame"/> would then drop). Mirrors
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
            // Same as RaiseStreamFrameDiscarded: format inside the guard so ToString cannot escape.
            SafeTrace(() => $"[{nameof(GapDetected)}] Subscriber threw: {ex}");
        }
    }

    /// <inheritdoc />
    public void EnableChannel(IChannel channel) => _channelControl.EnableChannel(channel);

    /// <inheritdoc />
    public void EnableChannels(IEnumerable<IChannel> channels) => _channelControl.EnableChannels(channels);

    /// <inheritdoc />
    public void DisableChannel(IChannel channel) => _channelControl.DisableChannel(channel);

    /// <inheritdoc />
    public void DisableAllChannels() => _channelControl.DisableAllChannels();

    /// <inheritdoc />
    public void SetDioDirection(IDigitalChannel channel, ChannelDirection direction)
        => _channelControl.SetDioDirection(channel, direction);

    /// <inheritdoc />
    public void SetDioValue(IDigitalChannel channel, bool value) => _channelControl.SetDioValue(channel, value);

    /// <summary>
    /// The PWM frequency floor Core assumes when the device does not state a trustworthy one:
    /// firmware before v3.9.0, or a device whose capability document has not been read. Below
    /// this the firmware's 16-bit period register silently wraps and the output runs in the
    /// kilohertz range.
    /// </summary>
    /// <remarks>
    /// The real floor depends on the peripheral clock the unit runs at
    /// (ceil(PBCLK3 / 256 / 65535)), so this fixed value can be off by a hertz or two in either
    /// direction on some units. Firmware v3.9.0 and newer report the real value; use
    /// <see cref="PwmFrequencyFloorHz"/> for the floor that applies to the connected device.
    /// </remarks>
    public const int MinPwmFrequencyHz = 6;

    /// <inheritdoc />
    public int PwmFrequencyFloorHz => _channelControl.PwmFrequencyFloorHz;

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
    public void SetPwmEnabled(IDigitalChannel channel, bool enabled) => _channelControl.SetPwmEnabled(channel, enabled);

    /// <inheritdoc />
    public void SetPwmDutyCycle(IDigitalChannel channel, int dutyCyclePercent)
        => _channelControl.SetPwmDutyCycle(channel, dutyCyclePercent);

    /// <inheritdoc />
    public void SetPwmFrequency(int frequencyHz) => _channelControl.SetPwmFrequency(frequencyHz);

    /// <inheritdoc />
    public Task SetFriendlyNameAsync(string name, CancellationToken cancellationToken = default)
        => _administration.SetFriendlyNameAsync(name, cancellationToken);

    /// <inheritdoc />
    public Task SetIdentifyAsync(bool enabled, CancellationToken cancellationToken = default)
        => _administration.SetIdentifyAsync(enabled, cancellationToken);

    /// <inheritdoc />
    public Task<bool> GetIdentifyAsync(CancellationToken cancellationToken = default)
        => _administration.GetIdentifyAsync(cancellationToken);

    /// <inheritdoc />
    public void SetAnalogOutput(IAnalogOutputChannel channel, double voltage)
        => _channelControl.SetAnalogOutput(channel, voltage);

    /// <inheritdoc />
    public void StageAnalogOutput(IAnalogOutputChannel channel, double voltage)
        => _channelControl.StageAnalogOutput(channel, voltage);

    /// <inheritdoc />
    public void LatchAnalogOutputs() => _channelControl.LatchAnalogOutputs();

    /// <inheritdoc />
    public Task<double> GetAnalogOutputAsync(IAnalogOutputChannel channel, CancellationToken cancellationToken = default)
        => _channelControl.GetAnalogOutputAsync(channel, cancellationToken);

    /// <inheritdoc />
    public void Reboot() => _administration.Reboot();

    /// <inheritdoc />
    /// <remarks>
    /// The device writes its acknowledgement before it begins the reset and drops its link
    /// 0.6 to 0.7 s later, so over USB the acknowledgement is read with time to spare. A device
    /// that is asked anything after accepting a reboot never answers, so an answer to the error
    /// queue query sent behind the reboot means the reboot did not happen.
    /// </remarks>
    public async Task RebootAsync(CancellationToken cancellationToken = default)
    {
        // Cancellation is checked before validation, matching every other ...Async member on
        // this class: a pre-cancelled token must surface as OperationCanceledException even on
        // a disconnected device, not be masked by DeviceNotConnectedException.
        cancellationToken.ThrowIfCancellationRequested();

        EnsureConnected();

        var command = ScpiMessageProducer.RebootDevice;
        var sent = false;
        string? refusal = null;
        string? answer = null;
        var acknowledged = false;

        try
        {
            await RunExclusiveAsync(
                async ct =>
                {
                    var responseTimeout = RebootResponseTimeout;
                    if (IsStreaming)
                    {
                        // Only the stop itself: the rest of what ends an SD log configures a
                        // device that is about to lose that configuration.
                        Send(ScpiMessageProducer.StopStreaming);
                        await Task.Delay(StoppedStreamSettleDelay, Clock, ct).ConfigureAwait(false);
                        responseTimeout = StoppedStateResponseTimeout;
                    }

                    var lines = await ExecuteTextCommandAsync(
                        () =>
                        {
                            Send(command);
                            sent = true;
                            Send(ScpiMessageProducer.GetSystemError);
                        },
                        responseTimeout: responseTimeout,
                        cancellationToken: ct).ConfigureAwait(false);

                    acknowledged = lines.Any(line =>
                        line.Contains(RebootAcknowledgement, StringComparison.OrdinalIgnoreCase));
                    refusal = ScpiResponseClassifier.GetLastScpiErrorLine(lines);
                    answer = lines.LastOrDefault(ScpiResponseClassifier.IsSystemErrorReplyLine)?.Trim();
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch when (sent)
        {
            // The command is out, so the device is most likely restarting, and what this object
            // has is a connection to a device that will not be there.
            await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        if (!acknowledged && (refusal ?? answer) is { } stillThere)
        {
            // Only the complaint the device volunteers is about this command. The queue entry may
            // be an older one's, so it says the device is still there and nothing about why.
            throw refusal != null && ScpiResponseClassifier.TryExtractErrorCode(refusal, out var code) && code != 0
                ? new DeviceCommandFailedException(command.Data, code, refusal)
                : DeviceCommandFailedException.Contradicted(
                    command.Data, "answered a query instead of restarting", stillThere);
        }

        // The device drops its link while restarting, so tear down the local connection —
        // without blocking the caller, unlike Reboot()'s synchronous Disconnect().
        await DisconnectAsync(cancellationToken).ConfigureAwait(false);

        if (!acknowledged)
        {
            throw DeviceCommandFailedException.Unconfirmed(command.Data, deviceResponse: null);
        }
    }

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
    public Task UseAdcCalibrationAsync(AdcCalibrationBank bank, CancellationToken cancellationToken = default)
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

    /// <summary>The streaming session: the flag, the rate, start/stop, raw-command tracking and reconnect restore.</summary>
    private StreamingSessionController _session = null!;

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

    /// <summary>Live WiFi link state and active mode (<see cref="IWifiLinkStatusProvider"/>).</summary>
    private WifiLinkStatusOperations _wifiLinkStatusOperations = null!;

    /// <summary>SD card operations (<see cref="ISdCardOperations"/>) and the shared-SPI handover.</summary>
    private SdCardOperations _sdCardOperations = null!;

    /// <summary>WiFi module chip info (<see cref="ILanChipInfoProvider"/>).</summary>
    private LanChipInfoOperations _lanChipInfoOperations = null!;

    /// <summary>Device diagnostics (<see cref="IDeviceDiagnostics"/>).</summary>
    private DeviceDiagnosticsOperations _diagnosticsOperations = null!;

    #region INetworkConfigurable

    /// <inheritdoc />
    /// <remarks>
    /// Runs under <see cref="DaqifiDevice.RunExclusiveAsync(Func{CancellationToken, Task}, CancellationToken)"/>:
    /// the reconfiguration is a dozen sends that only make sense as one sequence — another
    /// caller's commands must not land between the staged settings, the save and the apply, nor
    /// restart the stream it stopped — followed by the WiFi module's restart window, which other
    /// callers' queries would otherwise run into. Their sends are deferred and their text queries
    /// wait until it finishes, about two seconds.
    /// </remarks>
    public async Task UpdateNetworkConfigurationAsync(NetworkConfiguration configuration, CancellationToken cancellationToken = default)
    {
        // Checked before the lock wait so they surface exactly as they did before it: a
        // pre-cancelled token as OperationCanceledException rather than the lock wait's
        // TaskCanceledException, and a null configuration ahead of either.
        ArgumentNullException.ThrowIfNull(configuration);
        cancellationToken.ThrowIfCancellationRequested();

        await RunExclusiveAsync(ct => _networkOperations.UpdateNetworkConfigurationAsync(configuration, ct), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task LoadNetworkConfigurationAsync(CancellationToken cancellationToken = default)
        => _networkOperations.LoadNetworkConfigurationAsync(cancellationToken);

    /// <inheritdoc />
    public Task FactoryResetNetworkAsync(CancellationToken cancellationToken = default)
        => _networkOperations.FactoryResetNetworkAsync(cancellationToken);

    #endregion

    #region IWifiLinkStatusProvider

    /// <inheritdoc />
    /// <remarks>
    /// Runs under <see cref="DaqifiDevice.RunExclusiveAsync{TResult}(Func{CancellationToken, Task{TResult}}, CancellationToken)"/>:
    /// on the standby path the query, the error-count read and the conditional pop of its own
    /// <c>-200</c> are three exchanges, and the pop is only safe if no other caller can drain the
    /// queue or add to it in between.
    /// </remarks>
    public Task<WifiLinkState> GetWifiLinkStateAsync(CancellationToken cancellationToken = default)
        => RunExclusiveAsync(ct => _wifiLinkStatusOperations.GetWifiLinkStateAsync(ct), cancellationToken);

    /// <inheritdoc />
    public Task<WifiMode?> GetActiveWifiModeAsync(CancellationToken cancellationToken = default)
        => _wifiLinkStatusOperations.GetActiveWifiModeAsync(cancellationToken);

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
    /// <remarks>
    /// Runs under <see cref="DaqifiDevice.RunExclusiveAsync(Func{CancellationToken, Task}, CancellationToken)"/>,
    /// because it takes the same gate as <see cref="SetSdCardCrcProtectionAsync"/> and has to
    /// take it in the same order relative to the operation lock.
    /// </remarks>
    public Task SetSdCardMinimumFreeSpaceAsync(long bytes, CancellationToken cancellationToken = default)
        => RunExclusiveAsync(ct => _sdCardOperations.SetSdCardMinimumFreeSpaceAsync(bytes, ct), cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Runs under <see cref="DaqifiDevice.RunExclusiveAsync{TResult}(Func{CancellationToken, Task{TResult}}, CancellationToken)"/>:
    /// the set and each mount attempt read the error count in one exchange and may pop their own
    /// refusal in the next, and that pop is only safe if no other caller can drain the queue or
    /// add to it in between. Other callers' text queries wait for the change, a second or two.
    /// </remarks>
    public Task<SdCardCrcProtectionResult> SetSdCardCrcProtectionAsync(bool enabled, CancellationToken cancellationToken = default)
        => RunExclusiveAsync(ct => _sdCardOperations.SetSdCardCrcProtectionAsync(enabled, ct), cancellationToken);

    /// <inheritdoc />
    public Task<bool> GetSdCardCrcProtectionAsync(CancellationToken cancellationToken = default)
        => _sdCardOperations.GetSdCardCrcProtectionAsync(cancellationToken);

    /// <inheritdoc />
    public Task<SdCardRecording> StartSdCardLoggingAsync(string? fileName = null, SdCardLogFormat format = SdCardLogFormat.Protobuf, CancellationToken cancellationToken = default)
        => _sdCardOperations.StartSdCardLoggingAsync(fileName, format, cancellationToken);

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

    /// <inheritdoc />
    public void PrepareSdInterface() => _sdCardOperations.PrepareSdInterface();

    /// <inheritdoc />
    public void PrepareLanInterface() => _sdCardOperations.PrepareLanInterface();

    /// <summary>
    /// Raises the <see cref="LowSdSpaceWarning"/> event.
    /// </summary>
    /// <param name="e">The warning event arguments.</param>
    /// <remarks>
    /// Stays on the device rather than moving with the space check that triggers it: the event
    /// is part of this device's public surface, so subscribers must keep seeing this device as
    /// the <c>sender</c>, and a subclass override must keep intercepting it.
    /// </remarks>
    private protected virtual void OnLowSdSpaceWarning(LowSdSpaceWarningEventArgs e)
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

    /// <summary>
    /// How long to wait after switching the shared SPI bus between the LAN and the SD card, so
    /// the firmware can finish the switch before the next command arrives.
    /// </summary>
    /// <remarks>
    /// Virtual as a test seam: an SD operation pays this twice (switch to the card, switch
    /// back), so a suite that exercises the command sequences pays it on every test while
    /// asserting nothing about it. A test double sets it to <see cref="TimeSpan.Zero"/>.
    /// </remarks>
    internal virtual TimeSpan SdInterfaceSettleDelay => TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// The step of the recheck schedule after the device reports no SD card (#896): the
    /// <c>n</c>th recheck waits <c>n</c> times this.
    /// </summary>
    /// <remarks>
    /// The firmware answers "is a card present?" from its last card-detect poll, which backs
    /// off to once every 5 s while no card is seen, and it starts a fresh poll as it answers
    /// rather than waiting for one. A negative answer can therefore be seconds out of date, and
    /// the firmware leaves it to the host to ask again. Virtual as a test seam, for the same
    /// reason as <see cref="SdInterfaceSettleDelay"/>.
    /// </remarks>
    internal virtual TimeSpan SdNoCardRecheckDelay => TimeSpan.FromSeconds(1);

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
    public Task<LogLevelSetting> SetLogLevelAsync(string module, DeviceLogLevel level, CancellationToken cancellationToken = default)
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

    bool IDeviceOperationHost.IsStreaming => IsStreaming;

    bool IDeviceOperationHost.SendStreamStop() => _session.SendStreamStop();

    int IDeviceOperationHost.StreamingFrequency => StreamingFrequency;

    void IDeviceOperationHost.StopStreaming() => StopStreaming();

    void IDeviceOperationHost.StartStreaming() => StartStreaming();

    void IDeviceOperationHost.Send<T>(IOutboundMessage<T> message) => Send(message);

    bool IDeviceOperationHost.HasDeferredSends => HasDeferredSends;

    DeviceMetadata IDeviceOperationHost.Metadata => Metadata;

    void IDeviceOperationHost.UpdateMetadata(Func<DeviceMetadata, DeviceMetadata> change) =>
        UpdateMetadata(change);

    void IDeviceOperationHost.Disconnect() => Disconnect();

    IReadOnlyList<IChannel> IDeviceOperationHost.SnapshotChannels() => SnapshotChannels();

    /// <inheritdoc />
    long IDeviceOperationHost.ChannelStateVersion => ChannelStateVersion;

    void IDeviceOperationHost.WithChannelsLock(Action action) => WithChannelsLock(action);

    Task<IReadOnlyList<string>> IDeviceOperationHost.ExecuteTextCommandAsync(
        Action setupAction,
        TimeSpan? responseTimeout,
        TimeSpan? completionTimeout,
        Func<CancellationToken, Task>? prepareAsync,
        Func<Task>? finalizeAsync,
        bool keepBlankLines,
        CancellationToken cancellationToken)
        => ExecuteTextCommandAsync(
            setupAction, responseTimeout, completionTimeout, prepareAsync, finalizeAsync,
            keepBlankLines, cancellationToken);

    Task<IReadOnlyList<string>> IDeviceOperationHost.DrainErrorQueueAsync(
        int maxIterations,
        CancellationToken cancellationToken)
        => DrainErrorQueueAsync(maxIterations, cancellationToken);

    Task IDeviceOperationHost.ExecuteRawCaptureAsync(
        Func<Stream, CancellationToken, Task> rawAction,
        CancellationToken cancellationToken)
        => ExecuteRawCaptureAsync(rawAction, cancellationToken);

    bool IDeviceOperationHost.Supports(DeviceFeature feature) => Supports(feature);

    void IDeviceOperationHost.EnsureSupported(DeviceFeature feature) => EnsureSupported(feature);

    FeatureNotSupportedException IDeviceOperationHost.CreateFeatureNotSupportedException(DeviceFeature feature, IReadOnlyList<string> rawDeviceResponse)
        => CreateFeatureNotSupportedException(feature, rawDeviceResponse);

    bool ISdCardOperationHost.IsUsbConnection => IsUsbConnection;

    bool ISdCardOperationHost.IsLoggingToSdCard => IsLoggingToSdCard;

    void ISdCardOperationHost.StartSdCardLogStream(int rateHz) => _session.StartSdCardLogStream(rateHz);

    async Task ISdCardOperationHost.LowerInheritedStreamingFrequencyAsync(CancellationToken cancellationToken)
    {
        // Not while a stream is running: a text exchange would misframe its frames, and its rate
        // is one the device is already streaming at.
        if (!_session.IsFrequencyInherited || IsStreaming)
        {
            return;
        }

        // A cap that cannot be read lowers the rate to Core's default rather than sending a rate
        // nothing has shown the card can take.
        _session.LowerInheritedFrequency(
            await ReadCurrentMaximumStreamingRateAsync(cancellationToken).ConfigureAwait(false));
    }

    TimeSpan ISdCardOperationHost.SdCardDownloadTimeout => SdCardDownloadTimeout;

    TimeSpan ISdCardOperationHost.SdCardTransferIdleTimeout => SdCardTransferIdleTimeout;

    TimeSpan ISdCardOperationHost.SdInterfaceSettleDelay => SdInterfaceSettleDelay;

    TimeSpan ISdCardOperationHost.SdNoCardRecheckDelay => SdNoCardRecheckDelay;

    /// <inheritdoc />
    TimeProvider ISdCardOperationHost.TimeProvider => Clock;

    void ISdCardOperationHost.RaiseLowSdSpaceWarning(LowSdSpaceWarningEventArgs e) => OnLowSdSpaceWarning(e);

    Task<ErrorQueueDrainResult> ISdCardOperationHost.DrainErrorQueueUntilEmptyAsync(
        int maxIterations,
        CancellationToken cancellationToken)
        => DrainErrorQueueUntilEmptyAsync(maxIterations, cancellationToken);

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

    #region IStreamingSessionHost

    // The seam the streaming-session controller reaches the device through. Same arrangement as
    // the block above and for the same reason: every member forwards to the member it names, so
    // a subclass override of Send{T} still intercepts every command the session issues.

    void IStreamingSessionHost.EnsureConnected() => EnsureConnected();

    void IStreamingSessionHost.EnsureMessageReaderRunning() => EnsureMessageReaderRunning(nameof(StartStreaming));

    bool IStreamingSessionHost.PreserveActiveStream => PreserveActiveStream;

    void IStreamingSessionHost.Send<T>(IOutboundMessage<T> message) => Send(message);

    void IStreamingSessionHost.SendNow<T>(IOutboundMessage<T> message) =>
        ((IOperationSerializationHost)this).SendNow(message);

    bool IStreamingSessionHost.ReaderRunsForHeldBackStart(Action<Exception> onNotSent) =>
        ReaderRunsForHeldBackSend(nameof(StartStreaming), onNotSent);

    bool IStreamingSessionHost.SendsAreNeverHeldBack => SendsAreNeverHeldBack;

    bool IStreamingSessionHost.SendsAreHeldBack => SendsAreHeldBack;

    int IStreamingSessionHost.MaxSamplingRate => Metadata.Capabilities.MaxSamplingRate;

    // A start that is held back goes out behind the enables that are.
    int IStreamingSessionHost.MaximumStreamingFrequencyHz => SampleRateCap.ComputeForDevice(this, IsEnabledAheadOfNextSend);

    bool IStreamingSessionHost.IsEnableRequested(IChannel channel) => _channelControl.IsEnableRequested(channel);

    /// <summary>Whether a channel will be enabled when what this flow sends next goes out.</summary>
    private bool IsEnabledAheadOfNextSend(IChannel channel)
        => SendsAreNeverHeldBack ? channel.IsEnabled : _channelControl.IsEnableRequested(channel);

    // TimestampFrequency is supplied here rather than exposed on the seam: it is the device's
    // own report of its tick rate, read at exactly the moment the old BeginStreamingSession
    // read it.
    void IStreamingSessionHost.BeginDecoderSession(int streamingFrequencyHz)
        => _frameDecoder.BeginSession(TimestampFrequency, streamingFrequencyHz);

    TimeProvider IStreamingSessionHost.Clock => Clock;

    void IStreamingSessionHost.EndSdCardLogSession() => _sdCardOperations.EndLoggingSession();

    ITrackedSend IStreamingSessionHost.TrackAdcEnableMask(uint mask) => _channelControl.RawAdcMask(mask);

    IReadOnlyList<IChannel> IStreamingSessionHost.SnapshotChannels() => GetChannelsSnapshot();

    void IStreamingSessionHost.DisableAllChannels() => DisableAllChannels();

    void IStreamingSessionHost.EnableChannels(IEnumerable<IChannel> channels) => EnableChannels(channels);

    #endregion
}
