using System;
using System.Globalization;
using Daqifi.Core.Communication.Producers;

#nullable enable

namespace Daqifi.Core.Device.Internal;

/// <summary>
/// What a command that has just been sent means for the device's view of its streaming session.
/// </summary>
internal enum SessionCommandEffectKind
{
    /// <summary>The command says nothing about the session; the device state is untouched.</summary>
    None,

    /// <summary>The session has ended.</summary>
    StopStreaming,

    /// <summary>
    /// A session is starting, or a running one is changing rate, at
    /// <see cref="SessionCommandEffect.StreamingFrequency"/>.
    /// </summary>
    StartStreaming,

    /// <summary>
    /// A start-streaming command carrying a rate this device cannot model. Reported rather than
    /// folded into <see cref="None"/> so the caller can still trace it — see
    /// <see cref="SessionCommandInterpreter.Interpret"/> for why it changes no state.
    /// </summary>
    UnusableStreamingStart,

    /// <summary>
    /// An ADC enable bitmask was sent; <see cref="SessionCommandEffect.AdcEnableMask"/> carries it.
    /// </summary>
    SetAdcEnableMask,
}

/// <summary>
/// The decision <see cref="SessionCommandInterpreter"/> reached about one sent command, together
/// with the value it carries.
/// </summary>
internal readonly struct SessionCommandEffect
{
    private SessionCommandEffect(
        SessionCommandEffectKind kind,
        int streamingFrequency,
        uint adcEnableMask,
        string? rejectedRate)
    {
        Kind = kind;
        StreamingFrequency = streamingFrequency;
        AdcEnableMask = adcEnableMask;
        RejectedRate = rejectedRate;
    }

    /// <summary>Gets what the command means for the session.</summary>
    public SessionCommandEffectKind Kind { get; }

    /// <summary>
    /// Gets the validated rate, in Hz, when <see cref="Kind"/> is
    /// <see cref="SessionCommandEffectKind.StartStreaming"/>. Zero otherwise.
    /// </summary>
    public int StreamingFrequency { get; }

    /// <summary>
    /// Gets the bitmask when <see cref="Kind"/> is
    /// <see cref="SessionCommandEffectKind.SetAdcEnableMask"/>. Zero otherwise.
    /// </summary>
    public uint AdcEnableMask { get; }

    /// <summary>
    /// Gets the rate argument as it was written, when <see cref="Kind"/> is
    /// <see cref="SessionCommandEffectKind.UnusableStreamingStart"/>. Null otherwise. Present
    /// only so the rejection can be traced with the text that caused it.
    /// </summary>
    public string? RejectedRate { get; }

    /// <summary>The command carries nothing this device tracks.</summary>
    public static SessionCommandEffect None { get; } = new(SessionCommandEffectKind.None, 0, 0, null);

    /// <summary>The session has ended.</summary>
    public static SessionCommandEffect StopStreaming { get; } = new(SessionCommandEffectKind.StopStreaming, 0, 0, null);

    /// <summary>A session is running at <paramref name="frequency"/> Hz.</summary>
    public static SessionCommandEffect StartStreaming(int frequency) =>
        new(SessionCommandEffectKind.StartStreaming, frequency, 0, null);

    /// <summary>A start command whose rate argument, <paramref name="rejectedRate"/>, is unusable.</summary>
    public static SessionCommandEffect UnusableStreamingStart(string rejectedRate) =>
        new(SessionCommandEffectKind.UnusableStreamingStart, 0, 0, rejectedRate);

    /// <summary>An ADC enable bitmask of <paramref name="mask"/> was sent.</summary>
    public static SessionCommandEffect SetAdcEnableMask(uint mask) =>
        new(SessionCommandEffectKind.SetAdcEnableMask, 0, mask, null);
}

/// <summary>
/// Reads a SCPI command that has just been sent and decides what it means for the streaming
/// session, so a session driven through the raw <see cref="DaqifiDevice.Send{T}"/> path stays as
/// visible to Core as one driven through the typed API (issue #379).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="DaqifiDevice.Send{T}"/> is public and is a perfectly ordinary way to drive a device
/// — the example CLI does the whole job that way — but a session driven through it used to be
/// completely invisible: <see cref="DaqifiStreamingDevice.IsStreaming"/> stayed <c>false</c>
/// while data poured in, and the enabled-channel set stayed empty. That mattered once reconnect
/// arrived. A physical cable pull on the bench recovered the link and then reported
/// <c>StreamingResumed: false</c>, because as far as Core was concerned nothing had ever been
/// streaming — while re-initialization had in fact just stopped the stream that was running.
/// The session looked restored and was not.
/// </para>
/// <para>
/// So the commands that define a streaming session are recognized here regardless of which API
/// produced them, and the caller applies the same state the typed methods would have set. This
/// is the same principle as #409, where analog <c>IsEnabled</c> is resynced from the device's own
/// reported mask: what Core believes about a session has to track what is actually true of it.
/// </para>
/// <para>
/// Deciding is separated from applying on purpose. The decision is pure text-and-arithmetic and
/// is pinned directly by <c>SessionCommandInterpreterTests</c>; the effects it implies —
/// re-anchoring the session, flipping the flag, assigning channel state under the device's lock —
/// stay on the device, which is the only thing that owns them.
/// </para>
/// <para>
/// <b>Deliberately outside the scope.</b> The global DIO enable is one switch for the whole port
/// rather than a per-channel mask, so a raw <c>DIO:PORt:ENAble</c> carries no information about
/// <i>which</i> digital channels were wanted and none is inferred. Argument validation is not
/// replayed either: by the time a command is read here it has already gone to the device, and
/// the device is the authority on whether it accepted it.
/// </para>
/// </remarks>
internal static class SessionCommandInterpreter
{
    /// <summary>
    /// Decides what <paramref name="command"/> means for the streaming session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A start command whose argument is missing, unparseable, or outside the device's sampling
    /// range comes back as <see cref="SessionCommandEffectKind.UnusableStreamingStart"/> rather
    /// than as the start of a session. Treating one as a start would be wrong three times over:
    /// the firmware rejects such a command and does not start streaming, so the flag would not
    /// describe the device; the streaming frequency would be left holding a rate from some
    /// earlier session, which a reconnect would then faithfully restore — resuming at a rate
    /// nobody asked for is the silent-wrong-data failure this whole feature exists to prevent;
    /// and a stale streaming flag makes the next legitimate
    /// <see cref="DaqifiStreamingDevice.StartStreaming"/> a silent no-op, which is the same trap
    /// issue #118 and the defensive stops scattered through the SD paths already guard against.
    /// </para>
    /// <para>
    /// The existing state is therefore left alone rather than cleared. A device already streaming
    /// at a good rate goes on doing exactly that when the firmware rejects a malformed start, so
    /// both its flag and its rate remain true of it; forcing them off would swap one inaccuracy
    /// for another. Because of this, the device is never marked streaming alongside a rate that
    /// was not validated — so session restore has no "streaming at an unknown rate" case to
    /// decide what to do about.
    /// </para>
    /// </remarks>
    /// <param name="command">The command text that was just sent; null or blank yields <see cref="SessionCommandEffect.None"/>.</param>
    /// <param name="maxSamplingRate">
    /// The device's advertised maximum sampling rate. Passed in as a single read by the caller
    /// rather than read here twice: it is a mutable public property, so validating against one
    /// read and applying against another would let a concurrent capabilities update reject a
    /// command that has already reached the device. Sanitized with a floor of 1 so an
    /// uninitialized or invalid value (0 or negative) cannot produce an impossible range like
    /// "1..0" that rejects every rate.
    /// </param>
    public static SessionCommandEffect Interpret(string? command, int maxSamplingRate)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return SessionCommandEffect.None;
        }

        var trimmed = command!.Trim();

        if (trimmed.StartsWith(ScpiMessageProducer.StopStreamingCommand, StringComparison.OrdinalIgnoreCase))
        {
            return SessionCommandEffect.StopStreaming;
        }

        if (trimmed.StartsWith(ScpiMessageProducer.StartStreamingCommand, StringComparison.OrdinalIgnoreCase))
        {
            return InterpretStreamingStart(trimmed.AsSpan(ScpiMessageProducer.StartStreamingCommand.Length), maxSamplingRate);
        }

        if (trimmed.StartsWith(ScpiMessageProducer.EnableAdcChannelsCommand, StringComparison.OrdinalIgnoreCase))
        {
            return InterpretAdcEnableMask(trimmed.AsSpan(ScpiMessageProducer.EnableAdcChannelsCommand.Length));
        }

        return SessionCommandEffect.None;
    }

    private static SessionCommandEffect InterpretStreamingStart(ReadOnlySpan<char> argument, int maxSamplingRate)
    {
        var rate = argument.Trim();
        var ceiling = Math.Max(1, maxSamplingRate);

        if (!int.TryParse(rate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frequency)
            || frequency < 1
            || frequency > ceiling)
        {
            return SessionCommandEffect.UnusableStreamingStart(rate.ToString());
        }

        return SessionCommandEffect.StartStreaming(frequency);
    }

    private static SessionCommandEffect InterpretAdcEnableMask(ReadOnlySpan<char> argument)
    {
        return uint.TryParse(argument.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mask)
            ? SessionCommandEffect.SetAdcEnableMask(mask)
            : SessionCommandEffect.None;
    }
}
