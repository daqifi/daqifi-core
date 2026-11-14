using System;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Channel;
using Daqifi.Core.Device.Capabilities;
using Daqifi.Core.Communication.Producers;
using static Daqifi.Core.Internal.DiagnosticGuard;

namespace Daqifi.Core.Device.Internal;

/// <summary>
/// The session a streaming device is in. There is one acquisition on the device, so there is one
/// value: it is what <see cref="DaqifiStreamingDevice.IsStreaming"/> and
/// <see cref="DaqifiStreamingDevice.IsLoggingToSdCard"/> are both read from, which is what stops
/// them disagreeing (issue #932).
/// </summary>
internal enum StreamingSessionKind
{
    /// <summary>Nothing is running.</summary>
    None,

    /// <summary>The stream is running and its data comes to this machine.</summary>
    LiveStream,

    /// <summary>The stream is running and its data goes to the SD card.</summary>
    SdCardLog,
}

/// <summary>
/// Owns what a streaming device knows about the streaming session it is in: whether one is
/// running and of which kind, the rate it runs at, how a session begins and ends, how a session
/// driven by raw commands is kept in step, and what is restored after an automatic reconnect.
/// </summary>
/// <remarks>
/// <para>
/// These were four separate concerns scattered through <see cref="DaqifiStreamingDevice"/> —
/// the <c>IsStreaming</c>/<c>StreamingFrequency</c> pair, the start/stop pair, the
/// <c>Send</c>-tracking region for issue #379, and the reconnect snapshot region — but they are
/// one piece of state and one set of rules about moving it. Splitting the session flag from the
/// code that decides when a session begins is exactly how the two drift, and the tracking
/// region's own documentation says as much: a raw-started stream that skipped
/// <see cref="BeginStreamingSession"/> would decode frames against the previous session's
/// timestamp anchor, silently.
/// </para>
/// <para>
/// The device keeps every public member; each one forwards here. Extracted as part of #344.
/// </para>
/// </remarks>
internal sealed class StreamingSessionController
{
    private readonly IStreamingSessionHost _host;

    internal StreamingSessionController(IStreamingSessionHost host)
    {
        _host = host;
    }

    private StreamingSessionKind _kind;

    /// <inheritdoc cref="DaqifiStreamingDevice.IsStreaming"/>
    /// <remarks>
    /// Read-only: the session changes only through the methods of this class, each of which
    /// names why it changes (issue #940).
    /// </remarks>
    internal bool IsStreaming => _kind != StreamingSessionKind.None;

    /// <inheritdoc cref="DaqifiStreamingDevice.IsLoggingToSdCard"/>
    internal bool IsLoggingToSdCard => _kind == StreamingSessionKind.SdCardLog;

    /// <summary>
    /// Records that the stream now running is going to the SD card.
    /// </summary>
    internal void BeginSdCardLogSession() => _kind = StreamingSessionKind.SdCardLog;

    /// <summary>
    /// Records that the session, of either kind, has been stopped: a stop command went to the
    /// device.
    /// </summary>
    /// <remarks>
    /// Also what the SD operations call after the stop they send defensively before they touch
    /// the card (issue #118).
    /// </remarks>
    internal void EndSession() => _kind = StreamingSessionKind.None;

    /// <summary>
    /// Records that a start did not take effect — the device refused it, or the command could
    /// not be sent — so no session is running, whatever was recorded when the start went out.
    /// </summary>
    internal void MarkStartRefused() => _kind = StreamingSessionKind.None;

    /// <summary>
    /// Ends the session because the connection ended. Sends nothing: there is no link to send a
    /// stop on, and <c>Disconnect()</c> does not send one either.
    /// </summary>
    /// <remarks>
    /// Without this the session outlived the connection: <see cref="IsStreaming"/> stayed
    /// <c>true</c> on a disconnected device, and after a reconnect that sends no stop
    /// (<see cref="DaqifiDevice.PreserveActiveStream"/>) <see cref="StartStreaming"/> returned
    /// without sending anything (issue #940). The snapshot for an automatic reconnect is taken
    /// before the status leaves <c>Connected</c>, so it still records the session as it was at
    /// the drop.
    /// </remarks>
    internal void EndSessionOnConnectionEnd() => _kind = StreamingSessionKind.None;

    private int _streamingFrequency;

    /// <summary>
    /// Whether <see cref="_streamingFrequency"/> is a rate somebody chose — a caller set it, or a
    /// stream was started at it — rather than the default this device object was built with.
    /// </summary>
    /// <remarks>
    /// Only an unchosen rate gives way to the one the device reports it has stored
    /// (<see cref="TryAdoptStoredFrequency"/>). A chosen one is the caller's intent, and a
    /// reconnect has to restore it: a device that was power-cycled while it was away reports its
    /// 1 Hz boot default, and adopting that would resume the stream at 1 Hz.
    /// </remarks>
    private bool _frequencyChosen;

    /// <summary>
    /// Whether <see cref="_streamingFrequency"/> was taken from the device
    /// (<see cref="TryAdoptStoredFrequency"/>) rather than being Core's default.
    /// </summary>
    private bool _frequencyAdopted;

    /// <summary>
    /// Whether the rate is one this device object inherited from the device and nobody has
    /// chosen since. The device accepted it for whatever stream last set it, which says nothing
    /// about the interface and format the next start uses; see
    /// <see cref="LowerInheritedFrequency"/>.
    /// </summary>
    internal bool IsFrequencyInherited => _frequencyAdopted && !_frequencyChosen;

    /// <inheritdoc cref="DaqifiStreamingDevice.StreamingFrequency"/>
    internal int StreamingFrequency
    {
        get => _streamingFrequency;
        set
        {
            // MaxSamplingRate is a mutable, unvalidated public property; sanitize the ceiling so
            // an uninitialized/invalid capabilities value (0 or negative) can't produce an
            // impossible range like "1..0" that rejects every valid frequency.
            var maxSamplingRate = Math.Max(1, _host.MaxSamplingRate);
            if (value < 1 || value > maxSamplingRate)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(StreamingFrequency),
                    value,
                    DescribeRejectedFrequency(value, maxSamplingRate));
            }

            _streamingFrequency = value;
            _frequencyChosen = true;
        }
    }

    /// <summary>
    /// Sets the rate a new device object starts with. It is Core's assumption, not anyone's
    /// choice, so the device's own stored rate replaces it once that is read.
    /// </summary>
    /// <param name="frequencyHz">The default rate, validated as the setter validates it.</param>
    internal void SetDefaultFrequency(int frequencyHz)
    {
        StreamingFrequency = frequencyHz;
        _frequencyChosen = false;
        _frequencyAdopted = false;
    }

    /// <summary>
    /// The rate a new device object starts at, and the rate every start sent before Core could
    /// read the device's stored one (#960).
    /// </summary>
    internal const int CoreDefaultFrequencyHz = 100;

    /// <summary>
    /// The rate the firmware stores from power-up until something starts a stream: 1 Hz
    /// (<c>COMMON_STREAMING_RUNTIME_DEFAULTS</c>, <c>CommonRuntimeDefaults.h</c> at v3.9.0).
    /// </summary>
    /// <remarks>
    /// The firmware itself calls it a placeholder that describes no real rate
    /// (<c>streaming.h</c>, <c>Streaming_NoteRateConfigured</c>): it is the one rate legal in every
    /// configuration, not one anybody asked for.
    /// </remarks>
    internal const int FirmwareBootDefaultStoredRateHz = 1;

    /// <summary>
    /// Takes the streaming rate the device reports it has stored (<c>SYSTem:STReam:START?</c>,
    /// firmware v3.9.0) as this device's rate, unless a rate has already been chosen here or the
    /// device has never had one configured.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this, a device that another client — or an SD log — had set to 1000 Hz was
    /// reported at Core's 100 Hz default, and the next start silently changed its rate (#960).
    /// </para>
    /// <para>
    /// A device nothing has started since power-up stores the
    /// <see cref="FirmwareBootDefaultStoredRateHz"/> placeholder, which is not adopted: doing so
    /// would make every freshly powered device stream at 1 Hz where Core has always started at
    /// 100 Hz. <paramref name="rateConfigured"/> is what tells it apart from a 1 Hz somebody did
    /// start at. When it is unknown — no trusted capability document, or one without the
    /// timing block — a reported 1 Hz is taken to be the placeholder, which reports a deliberate
    /// 1 Hz at Core's rate: what every connect reported before the device could be asked.
    /// </para>
    /// <para>
    /// Never throws: it runs inside the connect sequence, and a reply Core cannot use must not
    /// fail a connect that is otherwise fine. A rate outside <c>1..MaxSamplingRate</c> is left
    /// alone, and the ceiling is read once and the backing field assigned directly, for the same
    /// reason <see cref="TrackSessionCommand"/> gives. Adopting it does not count as choosing it,
    /// so a later read can still bring the rate up to date.
    /// </para>
    /// </remarks>
    /// <param name="frequencyHz">The rate the device reported, in Hz.</param>
    /// <param name="rateConfigured">
    /// Whether the device has had a rate configured since power-up, as its capability document
    /// states it; <c>null</c> when that is unknown.
    /// </param>
    /// <returns><c>true</c> when the rate was adopted.</returns>
    internal bool TryAdoptStoredFrequency(int frequencyHz, bool? rateConfigured)
    {
        if (_frequencyChosen)
        {
            return false;
        }

        var isPlaceholder = rateConfigured == false
                            || (rateConfigured == null && frequencyHz == FirmwareBootDefaultStoredRateHz);
        if (isPlaceholder)
        {
            SafeTrace(
                $"[{nameof(TryAdoptStoredFrequency)}] The device reports {frequencyHz} Hz but has had no rate "
                + $"configured since power-up; keeping {_streamingFrequency} Hz.");
            return false;
        }

        var maxSamplingRate = Math.Max(1, _host.MaxSamplingRate);
        if (frequencyHz < 1 || frequencyHz > maxSamplingRate)
        {
            SafeTrace(
                $"[{nameof(TryAdoptStoredFrequency)}] Ignoring the device's stored streaming rate of {frequencyHz} Hz, "
                + $"which is outside 1..{maxSamplingRate} Hz; keeping {_streamingFrequency} Hz.");
            return false;
        }

        _streamingFrequency = frequencyHz;
        _frequencyAdopted = true;
        return true;
    }

    /// <summary>
    /// Lowers an inherited rate to the most the device says it can stream in the configuration
    /// it is now set up for, or to <see cref="CoreDefaultFrequencyHz"/> when the device does not
    /// say.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An inherited rate was accepted for the stream that last set it, often a USB one, and an SD
    /// log can have a lower ceiling: on an NQ1 on firmware 3.9.0, one channel with onboard
    /// diagnostics off logs protobuf to the card at no more than 7,900 Hz
    /// (<c>Streaming_SdAdditiveCap_NQ1</c>, <c>streaming.h</c>), under what USB sustains. Sent as
    /// it is, the device refuses the start with <c>-222</c> and records nothing. Before #960 such
    /// a caller sent Core's 100 Hz default, so this keeps an inherited rate from turning a log
    /// that used to start into one that silently does not.
    /// </para>
    /// <para>
    /// With no usable ceiling — the reply was lost or cut short, it has no
    /// <c>current_max_rate_hz</c>, or that is <c>0</c> — nothing shows the inherited rate is safe,
    /// so a rate above <see cref="CoreDefaultFrequencyHz"/> is lowered to it: the rate this
    /// caller sent before #960. A rate at or below it is kept, since it asks less of the device.
    /// </para>
    /// <para>
    /// Only an inherited rate is lowered. A chosen one is the caller's to keep. Lowering leaves
    /// the rate inherited, not chosen: the start that sends it is what makes it the session's
    /// rate.
    /// </para>
    /// </remarks>
    /// <param name="maximumHz">
    /// The device's current maximum rate, in Hz; <c>null</c> when it could not be read.
    /// </param>
    /// <returns><c>true</c> when the rate was lowered.</returns>
    internal bool LowerInheritedFrequency(int? maximumHz)
    {
        if (!IsFrequencyInherited)
        {
            return false;
        }

        var capKnown = maximumHz is > 0;
        var ceilingHz = capKnown ? maximumHz!.Value : CoreDefaultFrequencyHz;
        if (_streamingFrequency <= ceilingHz)
        {
            return false;
        }

        SafeTrace(
            $"[{nameof(LowerInheritedFrequency)}] Lowered the inherited streaming frequency from "
            + $"{_streamingFrequency} Hz to {ceilingHz} Hz, "
            + (capKnown
                ? "the most the device can stream as it is now configured. The device would have refused the higher rate and streamed nothing."
                : "Core's default, because the device did not report a usable maximum for how it is now configured."));
        _streamingFrequency = ceilingHz;
        return true;
    }

    /// <summary>
    /// The message for a rate the setter refuses, quoting the limit that the value actually
    /// broke rather than a figure that happens to be at hand.
    /// </summary>
    /// <remarks>
    /// Issue #899: a rejected <c>0</c> used to be reported as "must be between 1 and 22000 Hz
    /// (the device's maximum sampling rate)" while the live cap was 5713 Hz — two numbers, one
    /// of them irrelevant to the value given and the other not the one that governs a start. A
    /// value below 1 is refused by the lower bound alone, so the ceiling has no bearing on it;
    /// a value above the ceiling is refused by the ceiling, and the message adds the cap the
    /// enabled channels impose because that is the figure a caller choosing a rate needs next.
    /// </remarks>
    private string DescribeRejectedFrequency(int value, int maxSamplingRate)
    {
        if (value < 1)
        {
            return "Streaming frequency must be at least 1 Hz.";
        }

        var message =
            $"Streaming frequency must be at most {maxSamplingRate} Hz, the device's absolute sampling-rate ceiling.";

        // A cap of 0 means nothing is enabled, so there is no sustainable rate to quote; a cap
        // equal to the ceiling means the device has said nothing that narrows it. Neither adds
        // anything the caller can act on.
        var capHz = _host.MaximumStreamingFrequencyHz;
        return capHz > 0 && capHz < maxSamplingRate
            ? message
              + $" The channels enabled right now sustain at most {capHz} Hz, which is what"
              + " starting a stream lowers the rate to."
            : message;
    }

    /// <inheritdoc cref="DaqifiStreamingDevice.StartStreaming"/>
    internal void StartStreaming()
    {
        _host.EnsureConnected();

        if (IsStreaming) return;

        ApplyRateCap();

        // The rate a stream ran at is the one a reconnect resumes it at, whatever the device
        // reports afterwards.
        _frequencyChosen = true;

        BeginStreamingSession();

        _kind = StreamingSessionKind.LiveStream;
        _host.Send(ScpiMessageProducer.StartStreaming(StreamingFrequency));
    }

    /// <summary>
    /// Lowers the live rate to what the channel set about to stream can sustain, when it does
    /// not already fit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the one place the cap cannot be forgotten, which is why it is here and not left
    /// to the caller (issue #899). The rate the setter validates against is the board's
    /// absolute ceiling — 22000 Hz on an NQ1, reachable with almost nothing enabled — so a rate
    /// that is perfectly legal to <i>set</i> can still be more than the enabled channels
    /// sustain. Since firmware v3.5.0 the device does not degrade gracefully there: it answers
    /// the start with SCPI <c>-222</c> and streams nothing, leaving a caller watching a session
    /// that reports itself as running and never delivers a sample. Measured on an NQ1 running
    /// firmware 3.8.0 with two analog channels enabled: 6924 Hz gave 69,198 samples in 5 s,
    /// 10000 Hz gave none.
    /// </para>
    /// <para>
    /// Lowering rather than throwing is deliberate. The rate is an upper bound a caller asks
    /// for, not a contract the device ever promised to honour, and it is readable afterwards
    /// through <see cref="StreamingFrequency"/> — so a caller who cares can check what it got,
    /// while one who does not still gets data. Throwing would turn every ordinary
    /// "set a high rate, then enable channels" sequence into an exception every caller has to
    /// handle, to arrive at the same rate this picks for them.
    /// </para>
    /// <para>
    /// The adjusted value is assigned to the backing field rather than through the validating
    /// <see cref="StreamingFrequency"/> setter, for the reason
    /// <see cref="TrackSessionCommand"/> spells out: that setter re-reads
    /// <see cref="DeviceCapabilities.MaxSamplingRate"/>, and a cap already bounded by one read
    /// of it must not be able to fail against another. A cap of <c>0</c> — nothing enabled —
    /// leaves the rate alone; see <see cref="SampleRateCap.Enforce"/>.
    /// </para>
    /// </remarks>
    private void ApplyRateCap()
    {
        var (newRateHz, adjustedFromHz) =
            SampleRateCap.Enforce(_streamingFrequency, _host.MaximumStreamingFrequencyHz);

        if (!adjustedFromHz.HasValue)
        {
            return;
        }

        _streamingFrequency = newRateHz;

        SafeTrace(
            $"[{nameof(StartStreaming)}] Lowered the streaming frequency from {adjustedFromHz.Value} Hz "
            + $"to {newRateHz} Hz, the most the enabled channel set can sustain. The device would have "
            + "refused the higher rate and streamed nothing.");
    }

    /// <summary>
    /// Resets everything that is scoped to one streaming session, so the frames that follow are
    /// decoded against this session rather than the last one.
    /// </summary>
    /// <remarks>
    /// Shared by <see cref="StartStreaming"/> and by the tracking of a start-streaming command
    /// sent directly through <c>Send</c>. It is one method precisely so the two cannot
    /// drift: a raw-started stream that skipped this would reconstruct timestamps from the
    /// previous session's anchor and re-use its gap detector, producing samples stamped with
    /// times that never happened — silently.
    /// </remarks>
    private void BeginStreamingSession() =>
        _host.BeginDecoderSession(StreamingFrequency);

    /// <inheritdoc cref="DaqifiStreamingDevice.StopStreaming"/>
    internal void StopStreaming()
    {
        _host.EnsureConnected();

        if (!IsStreaming) return;

        if (IsLoggingToSdCard)
        {
            _host.EndSdCardLogSession();
            return;
        }

        EndSession();
        _host.Send(ScpiMessageProducer.StopStreaming);
    }

    #region Session state tracking for commands sent directly (issue #379)

    /// <summary>
    /// Updates the streaming-session view from a command that has just been sent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Send</c> is public on the device and is a perfectly ordinary way to drive it — the
    /// example CLI does the whole job that way — but a session driven through it used to be
    /// completely invisible: <see cref="IsStreaming"/> stayed <c>false</c> while data poured in,
    /// and the enabled-channel set stayed empty. That mattered once reconnect arrived (issue
    /// #379). <see cref="SessionCommandInterpreter"/> reads the commands that define a session;
    /// this method applies what they imply, so the same state is updated that
    /// <see cref="StartStreaming"/> / <see cref="StopStreaming"/> and
    /// <see cref="DaqifiStreamingDevice.EnableChannel"/> would have set.
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
    /// <see cref="StopStreaming"/> — for a live stream, does nothing beyond ending the session, so
    /// ending it here is complete. For an SD log it also restores the device's configuration, and
    /// that is deliberately not replayed: a raw command sends what it was given and no more. The
    /// session still ends, because a stop that reached the device has closed the log and left the
    /// card free (issue #932), and <see cref="DaqifiDevice.InitializeAsync"/> relies on that: it
    /// sends its stop through <c>Send</c> and repairs the format and routing itself.
    /// </description></item>
    /// <item><description>
    /// <see cref="DaqifiStreamingDevice.EnableChannels"/> — assigns <see cref="IChannel.IsEnabled"/>
    /// under the channels lock and derives the outbound mask from it. The mask has already been
    /// sent by the time this runs, so only the assignment is replayed, and it is applied to
    /// every analog channel because the firmware treats the mask as a set-replace.
    /// </description></item>
    /// </list>
    /// <para>
    /// Running after the send is safe rather than merely convenient. A string command is handed
    /// to the background producer, so it has not reached the wire when this runs, and the device
    /// cannot answer a command it has not received; on the producer-less path that writes
    /// synchronously there is no message consumer decoding frames at all. Either way no frame
    /// can be decoded between the send and the state it should be decoded against.
    /// </para>
    /// <para>
    /// The sampling ceiling is read exactly once and handed to the interpreter, which validates
    /// the rate against it; the value that comes back is then assigned to the backing field
    /// rather than through the validating <see cref="StreamingFrequency"/> setter. That setter
    /// re-reads <see cref="DeviceCapabilities.MaxSamplingRate"/>, which is a
    /// mutable public property — so validating against one read and assigning through a setter
    /// that takes another would let a concurrent capabilities update throw out of a <c>Send</c>
    /// whose command has already gone to the device. Tracking a command must never be able to
    /// fail the send that carried it.
    /// </para>
    /// </remarks>
    internal void TrackSessionCommand(string? command)
    {
        var effect = SessionCommandInterpreter.Interpret(command, _host.MaxSamplingRate);

        switch (effect.Kind)
        {
            case SessionCommandEffectKind.StopStreaming:
                EndSession();
                break;

            case SessionCommandEffectKind.StartStreaming:
                // Frequency first: anything observing IsStreaming must never catch it true next
                // to a rate belonging to a previous session.
                _streamingFrequency = effect.StreamingFrequency;
                _frequencyChosen = true;

                // A restart while already streaming is not a session boundary — the typed API
                // cannot even express it (StartStreaming returns early) — so there is nothing to
                // re-anchor and recording the new rate is all that is warranted.
                if (!IsStreaming)
                {
                    // A session is beginning, so it gets exactly the preparation StartStreaming
                    // would have given it. Ordering matches too: the state is ready before the
                    // flag flips.
                    BeginStreamingSession();
                    _kind = StreamingSessionKind.LiveStream;
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
    /// Applies a sent ADC enable bitmask to the device's analog channels, so a caller who
    /// enabled channels with a raw command has the same restorable session as one who used
    /// <see cref="DaqifiStreamingDevice.EnableChannels"/>.
    /// </summary>
    /// <remarks>
    /// The mask is a set-replace, exactly as the firmware treats it, so every analog channel is
    /// assigned from it rather than only the set bits. A device-reported mask still wins on the
    /// next status frame (#409) — that is the device's own view, and it outranks what was asked
    /// for.
    /// </remarks>
    private void ApplyAdcEnableMask(uint mask)
    {
        _host.WithChannelsLock(() =>
        {
            foreach (var channel in _host.SnapshotChannels())
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

    /// <inheritdoc cref="DaqifiStreamingDevice.CaptureSessionSnapshot"/>
    internal void CaptureSessionSnapshot() =>
        _sessionSnapshot = StreamingSessionSnapshot.Capture(_host.SnapshotChannels(), IsStreaming, IsLoggingToSdCard);

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
    internal Task<bool> RestoreSessionSnapshotAsync(
        ReconnectOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        // The session ended with the connection (EndSessionOnConnectionEnd), so nothing is
        // recorded as running here and the StartStreaming() below is not a silent no-op. This
        // repeats it for a caller that reaches this method without a status change.
        EndSessionOnConnectionEnd();

        var snapshot = _sessionSnapshot;
        if (snapshot == null)
        {
            return Task.FromResult(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Normalize to a known state before re-applying: whatever the device came back with is
        // not necessarily what it had, and the enable commands are set-replace anyway. The
        // channel list is read afterwards so the plan is built against the post-reset objects.
        _host.DisableAllChannels();

        var plan = snapshot.PlanRestore(_host.SnapshotChannels(), options);
        if (plan.ChannelsToEnable.Count > 0)
        {
            _host.EnableChannels(plan.ChannelsToEnable);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // An SD log that initialization did not stop (PreserveActiveStream) may still be running
        // on the device. A start sent now would go to a device that is writing to the card, and
        // would record a live stream that delivers nothing. So nothing is sent and nothing is
        // reported as resumed. The session stays ended, as on a new device object (issue #940).
        var resumeStreaming =
            plan.ResumeStreaming && !(snapshot.WasLoggingToSdCard && _host.PreserveActiveStream);

        if (resumeStreaming)
        {
            StartStreaming();
        }

        return Task.FromResult(resumeStreaming);
    }

    #endregion
}
