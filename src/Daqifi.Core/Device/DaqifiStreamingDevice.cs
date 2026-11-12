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
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using static Daqifi.Core.Internal.DiagnosticGuard;

namespace Daqifi.Core.Device;

/// <summary>
/// Represents a DAQiFi device that supports data streaming functionality.
/// Extends the base DaqifiDevice with streaming-specific operations.
/// </summary>
public class DaqifiStreamingDevice : DaqifiDevice, IStreamingDevice, ILiveSampleSource, INetworkConfigurable, ISdCardOperations, ILanChipInfoProvider, IDeviceDiagnostics, IDeviceOperationHost, ISdCardOperationHost, IStreamingSessionHost
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
    /// How often <see cref="WaitForFirstStreamFrameAsync"/> looks for the first frame.
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
    /// How many analog inputs were enabled when <see cref="StartStreamingAsync"/> last read the
    /// capability document, or <c>-1</c> before its first read. The device computes its rate cap
    /// for the set enabled at the moment of the read, so this is what says whether that figure
    /// still describes the device — see <see cref="RefreshSampleRateCapAsync"/>. A read made by
    /// anyone else does not update it, which can cost one redundant read and cannot cost
    /// correctness: a count that is too low only ever asks again.
    /// </summary>
    private int _capabilityReadAnalogInputCount = -1;

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

    /// <inheritdoc cref="IStreamingDevice.EnforceStreamingFrequencyCap" />
    [Obsolete("Starting a stream applies the cap itself, so this call is no longer needed. "
              + "Read MaximumStreamingFrequencyHz to see the limit. This member will be removed "
              + "in a future major version.")]
    public int? EnforceStreamingFrequencyCap() => SampleRateCap.EnforceOn(this);

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
        //
        // The session controller is built first because IsStreaming and StreamingFrequency now
        // read through it, and both the StreamingFrequency assignment at the bottom of this
        // method and anything a collaborator's constructor might read would otherwise find it
        // null.
        _session = new StreamingSessionController(this);

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

                // The same goes for which channels are in PWM mode (#945).
                _channelControl.ResetPwmEnabled();
            }
        };

        // The stream session ends with the connection too, for the same reason: nothing stopped
        // it, and what the device does from here is unknown. See #940.
        StatusChanged += (_, e) =>
        {
            if (e.Status != ConnectionStatus.Connected)
            {
                _session.EndSessionOnConnectionEnd();
            }
        };
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
    protected override void OnSendingInitializationCommands()
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

    /// <summary>
    /// Starts streaming data from the device without blocking the calling thread, and confirms
    /// with the device that the stream actually started.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three steps, and the first two are what keep the third from ever firing. The capability
    /// document is re-read so <see cref="MaximumStreamingFrequencyHz"/> describes the channel
    /// set about to stream rather than the one enabled at the last read;
    /// <see cref="StartStreaming"/> then lowers the rate to it. Only after that is the device's
    /// error queue read, so a start the firmware refused for any other reason reaches the
    /// caller as a <see cref="DeviceCommandFailedException"/> instead of being left on the
    /// device for the next connect to trip over (issues #899 and #898).
    /// </para>
    /// <para>
    /// Reading the error queue is a text exchange, which stops the reader decoding protobuf
    /// frames for its duration — so it runs only once the device has had its chance to send a
    /// frame and has sent none. A stream that is delivering data is never interrupted by it. See
    /// <see cref="ConfirmStreamStartedAsync"/>.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">A cancellation token to observe.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
    /// <exception cref="DeviceCommandFailedException">
    /// Thrown when the device sent no frame and its error queue holds a refusal of the start.
    /// </exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
    public async Task StartStreamingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var alreadyStreaming = IsStreaming;

        await RefreshSampleRateCapAsync(cancellationToken).ConfigureAwait(false);

        StartStreaming();

        if (!alreadyStreaming)
        {
            await ConfirmStreamStartedAsync(cancellationToken).ConfigureAwait(false);
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

        if (EnabledAnalogInputCount() <= _capabilityReadAnalogInputCount)
        {
            return;
        }

        try
        {
            await ReadCapabilityDocumentAsync(cancellationToken).ConfigureAwait(false);

            // Recorded whether or not the device answered. A device that supplied no document
            // has still been asked with this channel set, and asking it again with the same set
            // would cost the same second and learn the same nothing. Not recorded when the read
            // threw, so a transient failure is retried on the next start.
            _capabilityReadAnalogInputCount = EnabledAnalogInputCount();
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
    /// Waits for the device to prove the stream started, and reads its error queue when it does
    /// not.
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
    /// sample period or two: three periods, held between
    /// <see cref="MinStartConfirmationMs"/> and <see cref="MaxStartConfirmationMs"/>. A
    /// delivering stream returns from here as soon as its first frame lands, which at any rate
    /// above a few hertz is a handful of milliseconds.
    /// </para>
    /// <para>
    /// Only a reply that parses as a non-zero SCPI error counts as a refusal. A device with no
    /// analog channels enabled does not always start a stream and does not always say why, and
    /// a queue read that comes back with nothing readable is not evidence of anything — neither
    /// is turned into an exception.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">A cancellation token to observe while waiting.</param>
    /// <exception cref="DeviceCommandFailedException">Thrown when the device refused the start.</exception>
    private async Task ConfirmStreamStartedAsync(CancellationToken cancellationToken)
    {
        // Without a transport there is no error queue to read: the device is being driven by a
        // test double or an in-process stand-in, and there is nothing to confirm against.
        if (Transport == null)
        {
            return;
        }

        if (await WaitForFirstStreamFrameAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        IReadOnlyList<string> queued;
        try
        {
            queued = await DrainErrorQueueAsync(StartConfirmationErrorQueueReads, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A queue that cannot be read says nothing about the start. Report the silent
            // stream through the trace and leave the session as the caller set it up.
            SafeTrace(() =>
                $"[{nameof(StartStreamingAsync)}] The device sent no stream frame and its error "
                + $"queue could not be read: {ex.Message}");
            return;
        }

        var refusal = LastScpiErrorReply(queued);
        if (refusal == null)
        {
            return;
        }

        // The device is not streaming, whatever the flag says, so put the session back before
        // the exception leaves: a device reported as streaming while it sits idle also makes
        // the next StartStreaming() a silent no-op.
        _session.MarkStartRefused();

        var command = ScpiMessageProducer.StartStreaming(StreamingFrequency).Data.Trim();
        throw ScpiResponseClassifier.TryParseSystemErrorReplyCode(refusal, out var code) && code != 0
            ? new DeviceCommandFailedException(command, code, refusal)
            : new DeviceCommandFailedException(command, refusal);
    }

    /// <summary>
    /// Waits until the device has sent at least one stream frame, or the confirmation budget for
    /// the current rate runs out.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token to observe while waiting.</param>
    /// <returns><c>true</c> when a frame arrived; <c>false</c> when the budget ran out first.</returns>
    private async Task<bool> WaitForFirstStreamFrameAsync(CancellationToken cancellationToken)
    {
        var budgetMs = Math.Clamp(
            3000.0 / Math.Max(1, StreamingFrequency),
            MinStartConfirmationMs,
            MaxStartConfirmationMs);

        var startedAt = Clock.GetTimestamp();

        while (true)
        {
            if (_frameDecoder.ReceivedStreamFrameCount > 0)
            {
                return true;
            }

            // A stream stopped from another thread while this waited is not a refused start, so
            // stop asking the device about it.
            if (!IsStreaming)
            {
                return true;
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
    /// How many analog <i>input</i> channels are enabled right now — the one figure the device's
    /// sample-rate cap moves with.
    /// </summary>
    /// <remarks>
    /// Analog outputs are not streamed and digital pins are captured on the analog sample tick
    /// rather than driving one of their own, which is why neither counts here; see
    /// <see cref="SampleRateCap"/>.
    /// </remarks>
    private int EnabledAnalogInputCount()
    {
        var count = 0;

        foreach (var channel in GetChannelsSnapshot())
        {
            if (channel.Type == ChannelType.Analog
                && channel.Direction == ChannelDirection.Input
                && channel.IsEnabled)
            {
                count++;
            }
        }

        return count;
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
    /// define a session, and <see cref="StreamingSessionController.TrackSessionCommand"/>
    /// applies what they imply — the same state <see cref="StartStreaming"/> /
    /// <see cref="StopStreaming"/> and <see cref="EnableChannel"/> would have set. See that
    /// method for exactly what equivalence with the typed API covers.
    /// </para>
    /// <para>
    /// Only those commands are interpreted, and only after the send itself has succeeded.
    /// Everything else passes through untouched.
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
            _session.TrackSessionCommand(textCommand.Data);
        }
    }

    #endregion

    #region Session restore after an automatic reconnect (issue #379)

    /// <inheritdoc />
    protected override void CaptureSessionSnapshot() => _session.CaptureSessionSnapshot();

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

    /// <summary>
    /// Starts or stops flashing both of the device's LEDs, so the user can tell which physical
    /// board this connection is talking to (<c>SYSTem:IDENtify 1</c> / <c>0</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// While on, the LEDs repeat a pattern of about one second of fast flashing followed by about
    /// one second off, and that pattern replaces every other LED indication (power, charging,
    /// streaming, errors) until it is turned off. The firmware puts <b>no time limit</b> on it:
    /// it flashes until <c>SetIdentifyAsync(false)</c> or a reboot, so a caller that starts it
    /// owns turning it off. Disconnecting does not stop it.
    /// </para>
    /// <para>
    /// A single write with no reply, like <see cref="SetFriendlyNameAsync"/>: the task completes
    /// once the command is sent, and it is safe to call while streaming. Use
    /// <see cref="GetIdentifyAsync"/> to read the state back.
    /// </para>
    /// <para>Requires firmware v3.9.0 or newer (<see cref="DeviceFeature.Identify"/>).</para>
    /// </remarks>
    /// <param name="enabled"><c>true</c> to start flashing, <c>false</c> to stop.</param>
    /// <param name="cancellationToken">A cancellation token observed before the command is sent.</param>
    /// <returns>A task that completes once the command has been sent.</returns>
    /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
    /// <exception cref="FeatureNotSupportedException">
    /// Thrown when the device's firmware is older than v3.9.0, or its version is unknown.
    /// </exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
    public Task SetIdentifyAsync(bool enabled, CancellationToken cancellationToken = default)
        => _administration.SetIdentifyAsync(enabled, cancellationToken);

    /// <summary>
    /// Asks the device whether it is currently flashing its LEDs to identify itself
    /// (<c>SYSTem:IDENtify?</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reads the device's own flag, so it also reports an identify started by another
    /// connection or an earlier session.
    /// </para>
    /// <para>
    /// This runs a text-mode exchange, which stops decoding protobuf frames for its duration.
    /// Do not call it while streaming.
    /// </para>
    /// <para>Requires firmware v3.9.0 or newer (<see cref="DeviceFeature.Identify"/>).</para>
    /// </remarks>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns><c>true</c> while the device is identifying; otherwise <c>false</c>.</returns>
    /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
    /// <exception cref="FeatureNotSupportedException">
    /// Thrown when the device's firmware is older than v3.9.0, or its version is unknown.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the device rejected the query or answered with something other than <c>1</c> or <c>0</c>.
    /// </exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
    public Task<bool> GetIdentifyAsync(CancellationToken cancellationToken = default)
        => _administration.GetIdentifyAsync(cancellationToken);

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
    /// <exception cref="FeatureNotSupportedException">The known board does not support analog output.</exception>
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
    /// <exception cref="FeatureNotSupportedException">The known board does not support analog output.</exception>
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
    /// <exception cref="FeatureNotSupportedException">The known board does not support analog output.</exception>
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

    bool IDeviceOperationHost.IsStreaming => IsStreaming;

    void IDeviceOperationHost.EndStreamingSession() => _session.EndSession();

    void IDeviceOperationHost.MarkStreamingStartRefused() => _session.MarkStartRefused();

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

    bool IDeviceOperationHost.Supports(DeviceFeature feature) => Supports(feature);

    void IDeviceOperationHost.EnsureSupported(DeviceFeature feature) => EnsureSupported(feature);

    FeatureNotSupportedException IDeviceOperationHost.CreateFeatureNotSupportedException(DeviceFeature feature)
        => CreateFeatureNotSupportedException(feature);

    bool ISdCardOperationHost.IsUsbConnection => IsUsbConnection;

    bool ISdCardOperationHost.IsLoggingToSdCard => IsLoggingToSdCard;

    void ISdCardOperationHost.BeginSdCardLogSession() => _session.BeginSdCardLogSession();

    TimeSpan ISdCardOperationHost.SdCardDownloadTimeout => SdCardDownloadTimeout;

    TimeSpan ISdCardOperationHost.SdCardTransferIdleTimeout => SdCardTransferIdleTimeout;

    TimeSpan ISdCardOperationHost.SdInterfaceSettleDelay => SdInterfaceSettleDelay;

    /// <inheritdoc />
    TimeProvider ISdCardOperationHost.TimeProvider => Clock;

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

    #region IStreamingSessionHost

    // The seam the streaming-session controller reaches the device through. Same arrangement as
    // the block above and for the same reason: every member forwards to the member it names, so
    // a subclass override of Send{T} still intercepts every command the session issues.

    void IStreamingSessionHost.EnsureConnected() => EnsureConnected();

    bool IStreamingSessionHost.PreserveActiveStream => PreserveActiveStream;

    void IStreamingSessionHost.Send<T>(IOutboundMessage<T> message) => Send(message);

    int IStreamingSessionHost.MaxSamplingRate => Metadata.Capabilities.MaxSamplingRate;

    int IStreamingSessionHost.MaximumStreamingFrequencyHz => MaximumStreamingFrequencyHz;

    // TimestampFrequency is supplied here rather than exposed on the seam: it is the device's
    // own report of its tick rate, read at exactly the moment the old BeginStreamingSession
    // read it.
    void IStreamingSessionHost.BeginDecoderSession(int streamingFrequencyHz)
        => _frameDecoder.BeginSession(TimestampFrequency, streamingFrequencyHz);

    void IStreamingSessionHost.EndSdCardLogSession() => _sdCardOperations.EndLoggingSession();

    void IStreamingSessionHost.WithChannelsLock(Action action) => WithChannelsLock(action);

    IReadOnlyList<IChannel> IStreamingSessionHost.SnapshotChannels() => GetChannelsSnapshot();

    void IStreamingSessionHost.DisableAllChannels() => DisableAllChannels();

    void IStreamingSessionHost.EnableChannels(IEnumerable<IChannel> channels) => EnableChannels(channels);

    #endregion
}
