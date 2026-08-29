using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Producers;

#nullable enable

namespace Daqifi.Core.Device.Internal;

/// <summary>
/// Owns what a streaming device knows about the streaming session it is in: whether one is
/// running, the rate it runs at, how a session begins and ends, how a session driven by raw
/// commands is kept in step, and what is restored after an automatic reconnect.
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

    /// <inheritdoc cref="DaqifiStreamingDevice.IsStreaming"/>
    /// <remarks>
    /// Settable from outside because the SD operations defensively stop streaming before they
    /// touch the card and must record that they did (issue #118); the device exposes that
    /// through <see cref="IDeviceOperationHost.IsStreaming"/>.
    /// </remarks>
    internal bool IsStreaming { get; set; }

    private int _streamingFrequency;

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
                    $"Streaming frequency must be between 1 and {maxSamplingRate} Hz (the device's maximum sampling rate).");
            }

            _streamingFrequency = value;
        }
    }

    /// <inheritdoc cref="DaqifiStreamingDevice.StartStreaming"/>
    internal void StartStreaming()
    {
        _host.EnsureConnected();

        if (IsStreaming) return;

        BeginStreamingSession();

        IsStreaming = true;
        _host.Send(ScpiMessageProducer.StartStreaming(StreamingFrequency));
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

        IsStreaming = false;
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
    /// <see cref="StopStreaming"/> — does nothing beyond clearing the flag, so clearing it here
    /// is complete.
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
        _sessionSnapshot = StreamingSessionSnapshot.Capture(_host.SnapshotChannels(), IsStreaming);

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
        _host.DisableAllChannels();

        var plan = snapshot.PlanRestore(_host.SnapshotChannels(), options);
        if (plan.ChannelsToEnable.Count > 0)
        {
            _host.EnableChannels(plan.ChannelsToEnable);
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
    /// Writes a diagnostic line, swallowing anything a misbehaving <see cref="TraceListener"/>
    /// throws. Byte-identical twin of the device's own private helper.
    /// </summary>
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
}
