using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Capabilities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Daqifi.Core.Device.Internal;

/// <summary>
/// The channel-control half of <see cref="IStreamingDevice"/> — analog/digital enable, DIO
/// direction and level, PWM, and the analog output — extracted from
/// <see cref="DaqifiStreamingDevice"/> (#344) so the device delegates rather than hosts it.
/// Owns the PWM frequency bookkeeping that goes with those commands.
/// </summary>
/// <remarks>
/// <para>
/// Everything here reaches the device through <see cref="IDeviceOperationHost"/>, so each
/// command still passes through the device's own virtual <c>Send</c> and the same channels
/// lock the status-frame resync uses (#409).
/// </para>
/// <para>
/// Each command also runs as one step under <see cref="_commandGate"/>: what it reads or
/// records in channel state and the command it sends happen with no other channel command in
/// between. See that field for the interleavings it closes.
/// </para>
/// </remarks>
internal sealed class ChannelControlOperations
{
    /// <summary>
    /// The maximum analog channel number that can be encoded in the ADC enable bitmask.
    /// The mask is a 32-bit value (<c>1u &lt;&lt; ChannelNumber</c>), so channel numbers must be 0-31.
    /// Shared with the device's tracking of a raw ADC-enable command, which decodes the same mask.
    /// </summary>
    internal const int MaxAdcBitmaskChannel = 31;

    /// <summary>
    /// How long to wait for the device's answer to an analog-output readback. The query reads
    /// a value already held in RAM, so this only needs to cover the round trip.
    /// </summary>
    private static readonly TimeSpan AnalogOutputResponseTimeout = TimeSpan.FromSeconds(2);

    private readonly IDeviceOperationHost _host;

    /// <summary>
    /// Held by every channel command from the state it computes or checks, through its send, to
    /// the state it records — so the last command sent is always the one whose state Core holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without it, two calls on different threads could each be right on their own and wrong
    /// together. An enable computes its ADC mask from every channel, so one call enabling
    /// channel 1 computes {1}, a second enabling channel 2 computes {1,2}, and if the first send
    /// lands last the device ends on {1} while Core says channel 2 is on. A send-then-record
    /// command (direction, level, PWM, analog output) could send first, have a second call send
    /// and record, and then record its own value over the one the device took last. And a digital
    /// write could pass the PWM guard just before a PWM enable recorded, then go out to a pin the
    /// firmware ignores it on (#449).
    /// </para>
    /// <para>
    /// A separate lock rather than the channels lock, because <see cref="IDeviceOperationHost.Send{T}"/>
    /// must not run under that one (#409): the status-frame resync takes it on the consumer
    /// thread. This one is taken only by the commands here — from a caller's thread, or the
    /// reconnect's session restore — never by the consumer thread, and always before the
    /// channels lock, never inside it. <c>Send</c> queues rather than waits, so nothing held
    /// under it blocks on another thread. The disconnect resets
    /// (<see cref="ResetPwmEnabled"/>, <see cref="ResetSentPwmFrequency"/>) stay off it: they
    /// run from the state-change hook, which must not wait behind a caller's send.
    /// </para>
    /// </remarks>
    private readonly Lock _commandGate = new();

    /// <summary>
    /// The state Core records for the commands sent here, once they have gone out.
    /// </summary>
    private readonly CommandedState<HashSet<int>> _enabledAnalog;

    /// <inheritdoc cref="_enabledAnalog"/>
    private readonly CommandedState<HashSet<int>> _enabledDigital;

    /// <inheritdoc cref="_enabledAnalog"/>
    private readonly CommandedState<int> _pwmFrequency;

    /// <inheritdoc cref="_enabledAnalog"/>
    private readonly ConditionalWeakTable<DigitalChannel, PinState> _pins = new();

    /// <summary>The commanded state of one digital pin.</summary>
    private sealed class PinState(IDeviceOperationHost host, DigitalChannel channel)
    {
        // Under the channels lock, which is the lock the status-frame resync of these properties
        // writes under — the discipline SetChannelsEnabled already follows for IsEnabled (#409).
        internal CommandedState<ChannelDirection> Direction { get; } = new(
            () => channel.StoredDirection,
            direction => host.WithChannelsLock(() =>
            {
                // The firmware ignores the write while PWM drives the pin. It was checked when the
                // command was sent, but a PWM-off it was held back behind can have been dropped.
                if (!channel.IsPwmEnabled)
                {
                    channel.ApplyDirection(direction);
                }
            }));

        internal CommandedState<bool> Pwm { get; } = new(
            () => channel.IsPwmEnabled,
            enabled => host.WithChannelsLock(() =>
            {
                if (!enabled)
                {
                    channel.ExitPwm();
                }

                // The link can drop between the send and this record. The state-change hook has then
                // already reset PWM (ResetPwmEnabled, under this same lock); entering PWM now would
                // mark a disconnected device's pin as PWM and keep it so across the reconnect. A
                // status message can have withdrawn the capability too.
                else if (host.IsConnected && channel.IsPwmCapable)
                {
                    channel.EnterPwm();
                }
            }));

        internal CommandedState<int> PwmDutyCycle { get; } = new(
            () => channel.PwmDutyCyclePercent,
            percent => channel.PwmDutyCyclePercent = percent);
    }

    internal ChannelControlOperations(IDeviceOperationHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _enabledAnalog = EnabledChannels(ChannelType.Analog);
        _enabledDigital = EnabledChannels(ChannelType.Digital);
        _pwmFrequency = new CommandedState<int>(() => PwmFrequencyHz, hz => PwmFrequencyHz = hz);
    }

    private CommandedState<HashSet<int>> EnabledChannels(ChannelType type) => new(
        () => ReadEnabled(type),
        enabled => _host.WithChannelsLock(() =>
        {
            foreach (var channel in _host.SnapshotChannels())
            {
                if (channel.Type == type)
                {
                    SetEnabled(channel, enabled.Contains(channel.ChannelNumber));
                }
            }
        }));

    private HashSet<int> ReadEnabled(ChannelType type) => _host.SnapshotChannels()
        .Where(channel => channel.Type == type && channel.IsEnabled)
        .Select(channel => channel.ChannelNumber)
        .ToHashSet();

    private static void SetEnabled(IChannel channel, bool enabled)
        => (channel as IChannelEnablementNotifier)?.SetEnabled(enabled);

    /// <summary>
    /// The state there will be when what this flow sends next goes out: what has been requested, or
    /// what the device has for the flow whose operation the requests are held back behind, because
    /// its own commands go out ahead of them.
    /// </summary>
    private T AheadOfNextSend<T>(CommandedState<T> state)
        => _host.SendsAreNeverHeldBack ? state.Recorded : state.Requested;

    /// <summary>Whether a channel will be enabled when what this flow sends next goes out.</summary>
    internal bool IsEnabledAheadOfNextSend(IChannel channel)
        => _host.SendsAreNeverHeldBack ? channel.IsEnabled : IsEnableRequested(channel);

    /// <summary>
    /// Whether a channel is enabled, or has been asked to be by a command that is held back: what its
    /// caller wants, whether or not that goes out.
    /// </summary>
    internal bool IsEnableRequested(IChannel channel) => channel.Type switch
    {
        ChannelType.Analog => _enabledAnalog.Requested.Contains(channel.ChannelNumber),
        ChannelType.Digital => _enabledDigital.Requested.Contains(channel.ChannelNumber),
        _ => channel.IsEnabled,
    };

    /// <summary>
    /// A raw ADC enable mask, which is recorded and built on like a typed one. Every bit of it is
    /// kept: a channel it names can become known before it goes out.
    /// </summary>
    internal TrackedCommand RawAdcMask(uint mask)
    {
        var enabled = Enumerable.Range(0, MaxAdcBitmaskChannel + 1)
            .Where(number => (mask & (1u << number)) != 0)
            .ToHashSet();

        return _enabledAnalog.Command(enabled, _ => enabled);
    }

    private PinState Pin(DigitalChannel channel)
        => _pins.GetValue(channel, pin => new PinState(_host, pin));

    /// <inheritdoc cref="IStreamingDevice.PwmFrequencyHz" />
    internal int PwmFrequencyHz { get; private set; } = DaqifiStreamingDevice.DefaultPwmFrequencyHz;

    /// <summary>
    /// The PWM frequency actually sent to the device this connection, or <c>null</c> if none has
    /// been sent yet (also reset to <c>null</c> on disconnect). Distinct from
    /// <see cref="PwmFrequencyHz"/>, which carries a session default before anything is sent —
    /// this drives the skip-if-unchanged guard so a fresh connection always sends. See #345.
    /// </summary>
    private int? _lastSentPwmFrequencyHz;

    /// <summary>
    /// Clears the "already-sent" PWM frequency cache, so the next <see cref="SetPwmFrequency"/>
    /// sends even if the value is unchanged. Called on any transition away from Connected —
    /// after one the device's runtime PWM state is no longer trustworthy. See #345.
    /// </summary>
    /// <remarks>
    /// Under the channels lock, the lock <see cref="SetPwmFrequency"/> records under, for the
    /// reason <see cref="ResetPwmEnabled"/> gives: a frequency whose send returned just as the
    /// link dropped either records before this reset, which then clears it, or after it, where
    /// it sees the device is no longer connected and records nothing.
    /// </remarks>
    internal void ResetSentPwmFrequency() => _host.WithChannelsLock(() => _lastSentPwmFrequencyHz = null);

    /// <summary>
    /// Records PWM as off on every digital channel. Called on any transition away from
    /// Connected, together with <see cref="ResetSentPwmFrequency"/>: a device that restarted
    /// while the link was down has PWM off, and a channel still marked PWM would make Core
    /// refuse digital commands for a pin that is not in PWM mode. A device that only lost the
    /// link keeps PWM running, so the caller enables (or disables) PWM again after a reconnect.
    /// See #945.
    /// </summary>
    /// <remarks>
    /// Runs under the channels lock, the lock <see cref="SetPwmEnabled"/> records under, so a
    /// reset and a record cannot interleave: an enable whose send returned just as the link
    /// dropped either records before this reset, which then clears it, or after it, where it
    /// sees the device is no longer connected and records nothing. Nothing here waits on the
    /// connection or raises an event, so the order stays channels lock, then a channel's own.
    /// </remarks>
    internal void ResetPwmEnabled()
    {
        _host.WithChannelsLock(() =>
        {
            foreach (var channel in _host.SnapshotChannels())
            {
                if (channel is DigitalChannel { Mode: DigitalPinMode.Pwm } digitalChannel)
                {
                    digitalChannel.ExitPwm();
                }
            }
        });
    }

    /// <inheritdoc cref="IStreamingDevice.EnableChannel" />
    internal void EnableChannel(IChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        SetChannelsEnabled(new[] { channel }, enabled: true);
    }

    /// <inheritdoc cref="IStreamingDevice.EnableChannels" />
    internal void EnableChannels(IEnumerable<IChannel> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        SetChannelsEnabled(channels as IReadOnlyList<IChannel> ?? channels.ToList(), enabled: true);
    }

    /// <inheritdoc cref="IStreamingDevice.DisableChannel" />
    internal void DisableChannel(IChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        SetChannelsEnabled(new[] { channel }, enabled: false);
    }

    /// <inheritdoc cref="IStreamingDevice.DisableAllChannels" />
    internal void DisableAllChannels()
    {
        _host.EnsureConnected();

        // Push the cleared state for whichever channel types this device actually has.
        ApplyEnabled(_host.SnapshotChannels, enabled: false, all: true);
    }

    /// <inheritdoc cref="IStreamingDevice.SetDioDirection" />
    internal void SetDioDirection(IDigitalChannel channel, ChannelDirection direction)
    {
        // Argument validation precedes the connection (state) check so misuse surfaces
        // the same exception type regardless of connection state.
        ArgumentNullException.ThrowIfNull(channel);

        if (direction != ChannelDirection.Input && direction != ChannelDirection.Output)
        {
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "Direction must be Input or Output.");
        }

        _host.EnsureConnected();

        var pin = EnsureBelongs<DigitalChannel>(channel);

        // The PWM check, the send and the record as one step, so neither a concurrent PWM
        // enable nor a concurrent direction write can land between them (see _commandGate).
        lock (_commandGate)
        {
            EnsurePwmNotEnabled(pin);
            SendAndRecordDioDirection(pin, direction);
        }
    }

    private void SendAndRecordDioDirection(DigitalChannel channel, ChannelDirection direction)
    {
        // Send first, then record the new direction, because a status frame can write this same
        // property (#685).
        //
        // Recording first would leave a window between the local write and the wire write in
        // which a status frame the device encoded *before* it applied this command can land
        // and revert Core's view. Ordinarily a later frame would repair that, but this device
        // sends no unsolicited status frames at all — measured on an Nq1 (fw 3.7.2): zero over
        // 10s idle and zero over 10s of streaming, they arrive only as replies to
        // SYSTem:SYSInfoPB?. So there may be no later frame, and Core would report the pin
        // backwards indefinitely while the device sat in the commanded direction.
        //
        // Writing after the send closes that: whatever a concurrent status refresh wrote
        // while the command was in flight, the commanded value has the last word, and it is the
        // value the device is now in. It also means a send that throws leaves Core's view
        // untouched rather than claiming a direction the pin never took — the same principle as
        // the PWM guard above (#449).
        Pin(channel).Direction.Command(direction).Send(
            _host, ScpiMessageProducer.SetDioPortDirection(channel.ChannelNumber, direction));
    }

    /// <inheritdoc cref="IStreamingDevice.SetDioValue" />
    internal void SetDioValue(IDigitalChannel channel, bool value)
    {
        ArgumentNullException.ThrowIfNull(channel);

        _host.EnsureConnected();

        var pin = EnsureBelongs<DigitalChannel>(channel);

        // The PWM check and the send as one step (see _commandGate). Nothing is recorded: the
        // pin's level comes back in the stream as IDigitalChannel.IsHigh (#901).
        lock (_commandGate)
        {
            EnsurePwmNotEnabled(pin);
            _host.Send(ScpiMessageProducer.SetDioPortState(channel.ChannelNumber, value));
        }
    }

    /// <summary>
    /// Blocks digital direction/state writes while PWM is active on the channel. The firmware
    /// silently ignores those commands once PWM is running the pin, but Core would otherwise
    /// still mirror the commanded level/direction into local state — telling every caller the
    /// pin is doing something it is not (#449). Symmetric with the capability guard in
    /// <see cref="SetPwmEnabled"/>; the only way past this is <c>SetPwmEnabled(channel, false)</c>.
    /// The check is the channel's <see cref="IDigitalChannel.Mode"/>, the same one the MCP
    /// server makes before it calls in (#945), or the mode it will be in if the write is held back
    /// behind a PWM command.
    /// </summary>
    private void EnsurePwmNotEnabled(DigitalChannel channel)
    {
        if (AheadOfNextSend(Pin(channel).Pwm))
        {
            throw new InvalidOperationException(
                $"Channel {channel.ChannelNumber} has PWM enabled; digital direction/state commands are ignored by the firmware while PWM is running. Disable PWM on this channel first (SetPwmEnabled(channel, false); the MCP server exposes this as the disable_pwm tool).");
        }
    }

    /// <inheritdoc cref="IStreamingDevice.SetPwmEnabled" />
    internal void SetPwmEnabled(IDigitalChannel channel, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(channel);

        // Enabling PWM on a non-capable channel must be blocked here: the firmware flags the
        // channel PWM-active before its capability check fails and never rolls that back,
        // leaving the channel dead to digital writes. Disabling is that state's only recovery
        // command, so it is accepted on any digital channel.
        if (enabled && !channel.IsPwmCapable)
        {
            throw new ArgumentException(
                $"Channel {channel.ChannelNumber} does not support PWM. PWM-capable channels: {PwmCapableChannelList}.",
                nameof(channel));
        }

        _host.EnsureConnected();

        var pin = EnsureBelongs<DigitalChannel>(channel);

        // The send and the record as one step, so a concurrent PWM enable or disable cannot send
        // and record between them, and a digital write cannot pass its PWM check in the gap
        // (see _commandGate).
        lock (_commandGate)
        {
            // Send first, then record — the rule SetDioDirection sets out above. A send that
            // throws must not leave the channel marked PWM when the pin never went there: Core
            // would then refuse every digital command for it (#945).
            //
            // Disabling PWM leaves the pin high-impedance and the firmware zeroes its stored
            // output value. The stored direction is intentionally left as-is: the firmware keeps
            // it and re-applies it (resuming driving) on the next state or direction write, or on
            // the next streaming tick — verified on hardware.
            Pin(pin).Pwm.Command(enabled).Send(
                _host, ScpiMessageProducer.SetPwmChannelEnabled(channel.ChannelNumber, enabled));
        }
    }

    /// <inheritdoc cref="IStreamingDevice.SetPwmDutyCycle" />
    internal void SetPwmDutyCycle(IDigitalChannel channel, int dutyCyclePercent)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (!channel.IsPwmCapable)
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

        _host.EnsureConnected();

        var pin = EnsureBelongs<DigitalChannel>(channel);

        lock (_commandGate)
        {
            // Send first, then record — the rule SetDioDirection sets out above (#945).
            Pin(pin).PwmDutyCycle.Command(dutyCyclePercent).Send(
                _host, ScpiMessageProducer.SetPwmChannelDutyCycle(channel.ChannelNumber, dutyCyclePercent));
        }
    }

    /// <inheritdoc cref="DaqifiStreamingDevice.PwmFrequencyFloorHz" />
    internal int PwmFrequencyFloorHz
    {
        get
        {
            // Below v3.9.0 the document's floor is a fixed 1 Hz the hardware cannot produce, so
            // only a device that passes this gate has one worth reading (#958).
            if (!_host.Supports(DeviceFeature.PwmFrequencyFloor))
            {
                return DaqifiStreamingDevice.MinPwmFrequencyHz;
            }

            var document = _host.Metadata.CapabilityDocument;
            if (document is null)
            {
                return DaqifiStreamingDevice.MinPwmFrequencyHz;
            }

            // One timer drives every PWM pin, so the firmware states the same floor on each of
            // them. Taking the highest is the safe reading should two ever differ: it is the only
            // value every pin accepts. A value outside the range Core can command at all is not a
            // floor Core can use, so it is skipped rather than trusted.
            int? floor = null;
            foreach (var channel in document.Channels)
            {
                if (channel is { Kind: CapabilityChannelKind.DigitalIo, SupportsPwm: true, PwmMinimumFrequencyHz: { } advertised }
                    && advertised is >= 1 and <= DaqifiStreamingDevice.MaxPwmFrequencyHz)
                {
                    floor = Math.Max(floor ?? advertised, advertised);
                }
            }

            return floor ?? DaqifiStreamingDevice.MinPwmFrequencyHz;
        }
    }

    /// <inheritdoc cref="IStreamingDevice.SetPwmFrequency" />
    internal void SetPwmFrequency(int frequencyHz)
    {
        var floorHz = PwmFrequencyFloorHz;
        if (frequencyHz < floorHz || frequencyHz > DaqifiStreamingDevice.MaxPwmFrequencyHz)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frequencyHz), frequencyHz,
                $"PWM frequency must be {floorHz}-{DaqifiStreamingDevice.MaxPwmFrequencyHz} Hz.");
        }

        _host.EnsureConnected();

        // The skip check, the send and the record as one step: a concurrent call recording its
        // frequency between this send and this record would leave the cache naming a frequency
        // the device did not take last, and the skip below would then drop a real change.
        lock (_commandGate)
        {
            // Skip the redundant round-trip when the device already has this frequency (from a
            // send earlier this connection). The cache is cleared on disconnect so a fresh
            // connection always sends. PwmFrequencyHz still reflects the commanded value. See #345.
            int? lastSentHz = null;
            _host.WithChannelsLock(() => lastSentHz = _lastSentPwmFrequencyHz);
            if (frequencyHz == lastSentHz)
            {
                return;
            }

            // The SCPI command is addressed to a channel, but the firmware drives all PWM from
            // one shared timer and applies the frequency to every channel. Channel 0 is used as
            // the address because it is PWM-capable on all supported hardware.
            _pwmFrequency.Command(frequencyHz).Send(
                _host, ScpiMessageProducer.SetPwmChannelFrequency(0, frequencyHz));

            // The cache may only name a frequency the device is known to have taken last, and
            // the gate orders calls, not deliveries. A send from a flow that does not own a
            // running exclusive operation is parked and replayed when it ends (#342) — after
            // anything the owner sent meanwhile, which went straight out. So while parked sends
            // are waiting, whether this send was one of them or went out ahead of them, the
            // device may end on another frequency: clear the cache, and the next call sends.
            var deliveredInOrder = !_host.HasDeferredSends;

            // The link can also drop between the send and this record, and the state-change hook
            // then clears the cache (ResetSentPwmFrequency, under this same lock). Recording
            // after that would make the first call after the reconnect skip a frequency the
            // device may no longer have — the same reasoning as the PWM enable record (#945).
            _host.WithChannelsLock(() =>
            {
                _lastSentPwmFrequencyHz = deliveredInOrder && _host.IsConnected ? frequencyHz : null;
            });
        }
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
            foreach (var ch in _host.SnapshotChannels())
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

    /// <inheritdoc cref="IStreamingDevice.SetAnalogOutput" />
    internal void SetAnalogOutput(IAnalogOutputChannel channel, double voltage)
    {
        StageAnalogOutput(channel, voltage);
        LatchAnalogOutputs();
    }

    /// <inheritdoc cref="DaqifiStreamingDevice.StageAnalogOutput" />
    internal void StageAnalogOutput(IAnalogOutputChannel channel, double voltage)
    {
        ArgumentNullException.ThrowIfNull(channel);

        // Range-check against the device's own description of the channel before anything
        // reaches the wire.
        if (!channel.IsInRange(voltage))
        {
            throw new ArgumentOutOfRangeException(
                nameof(voltage),
                voltage,
                $"Analog output {channel.ChannelNumber} accepts {channel.MinimumVoltage} to {channel.MaximumVoltage} V"
                + (channel.RangeIsAssumed ? " (assumed; the device stated no range)." : "."));
        }

        _host.EnsureConnected();

        _host.EnsureSupported(DeviceFeature.AnalogOutput);

        var output = EnsureBelongs<AnalogOutputChannel>(channel);

        lock (_commandGate)
        {
            new TrackedCommand(() => output.Stage(voltage)).Send(
                _host, ScpiMessageProducer.SetAnalogOutputVoltage(channel.ChannelNumber, voltage));
        }
    }

    /// <inheritdoc cref="DaqifiStreamingDevice.LatchAnalogOutputs" />
    internal void LatchAnalogOutputs()
    {
        _host.EnsureConnected();

        _host.EnsureSupported(DeviceFeature.AnalogOutput);

        // One step with the latch bookkeeping, so a value staged by a concurrent call after this
        // latch went out is not marked as latched by it (see _commandGate).
        lock (_commandGate)
        {
            // The device latches every staged channel at once, so every modelled channel with a
            // pending value then reflects what the hardware is driving.
            new TrackedCommand(() =>
            {
                var latchedAt = DateTime.Now;
                foreach (var channel in _host.SnapshotChannels())
                {
                    (channel as AnalogOutputChannel)?.Latch(latchedAt);
                }
            }).Send(_host, ScpiMessageProducer.UpdateDacOutputs);
        }
    }

    /// <inheritdoc cref="DaqifiStreamingDevice.GetAnalogOutputAsync" />
    internal async Task<double> GetAnalogOutputAsync(
        IAnalogOutputChannel channel, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);

        _host.EnsureConnected();

        cancellationToken.ThrowIfCancellationRequested();

        _host.EnsureSupported(DeviceFeature.AnalogOutput);

        var output = EnsureBelongs<AnalogOutputChannel>(channel);
        var channelNumber = channel.ChannelNumber;
        var query = ScpiMessageProducer.GetAnalogOutputVoltage(channelNumber);

        var lines = await _host.ExecuteTextCommandAsync(
            () => _host.Send(query),
            responseTimeout: AnalogOutputResponseTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (ScpiResponseClassifier.ContainsScpiError(lines))
        {
            throw new InvalidOperationException(
                $"The device rejected a readback of analog output {channelNumber}. It answered: {DescribeLines(lines)}");
        }

        if (LineParsing.TryParseFirst<double>(lines, static (string? line, out double volts) =>
                double.TryParse(line?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out volts)
                && double.IsFinite(volts),
            out var readbackVolts))
        {
            output.SetOutputVoltage(readbackVolts, DateTime.Now);
            return readbackVolts;
        }

        throw new InvalidOperationException(
            $"The device's readback of analog output {channelNumber} was not a voltage. It answered: {DescribeLines(lines)}");
    }

    private static string DescribeLines(IReadOnlyList<string> lines)
        => lines.Count == 0 ? "(nothing)" : string.Join(" | ", lines);

    /// <summary>
    /// Sets the enabled state for a set of channels, then sends one device command per affected
    /// channel type (the ADC enable bitmask for analog, the global DIO enable for digital).
    /// Validation runs before any mutation so an invalid entry leaves device state untouched.
    /// </summary>
    private void SetChannelsEnabled(IReadOnlyList<IChannel> channels, bool enabled)
    {
        _host.EnsureConnected();

        // Validate everything up front so a bad entry can't leave a partially-applied state.
        foreach (var channel in channels)
        {
            if (channel is null)
            {
                throw new ArgumentException("The channel collection contains a null entry.", nameof(channels));
            }

            // An analog output is driven by writing a voltage to it, not by being enabled for
            // acquisition — it appears in neither the ADC enable bitmask nor the DIO enable,
            // so letting it through here would set a flag that no command ever acts on.
            if (channel.Type == ChannelType.AnalogOutput)
            {
                throw new ArgumentException(
                    $"Analog output {channel.ChannelNumber} is not an acquisition channel and cannot be enabled or disabled; write to it with SetAnalogOutput instead.",
                    nameof(channels));
            }

            EnsureChannelBelongs(channel);
        }

        ApplyEnabled(() => channels, enabled, all: false);
    }

    /// <summary>
    /// Sends the commands that enable or disable the channels, or that disable everything the device
    /// streams when they are all of them. Each channel type is recorded when its command goes out.
    /// </summary>
    private void ApplyEnabled(Func<IReadOnlyList<IChannel>> getChannels, bool enabled, bool all)
    {
        // A command names every channel of its type, not just the ones this call touches, so working
        // it out and sending it have to be one step: otherwise a concurrent call can work out a newer
        // one and send it first, and this call's older one then lands last (see _commandGate).
        lock (_commandGate)
        {
            var channels = getChannels();

            if (ChangeOf(ChannelType.Analog, channels, enabled, all) is { } analogChange)
            {
                var analog = analogChange(AheadOfNextSend(_enabledAnalog));
                var mask = ScpiMessageProducer.EnableAdcChannels(AdcEnableMask(analog));
                _enabledAnalog.Command(analog, analogChange).Send(_host, mask);
            }

            if (ChangeOf(ChannelType.Digital, channels, enabled, all) is { } digitalChange)
            {
                var digital = digitalChange(AheadOfNextSend(_enabledDigital));
                _enabledDigital.Command(digital, digitalChange).Send(
                    _host,
                    digital.Count > 0 ? ScpiMessageProducer.EnableDioPorts : ScpiMessageProducer.DisableDioPorts);
            }
        }
    }

    /// <summary>
    /// What enabling or disabling the channels does to the enabled ones of a type; <c>null</c> when
    /// there is nothing to command for that type.
    /// </summary>
    private Func<HashSet<int>, HashSet<int>>? ChangeOf(
        ChannelType type, IReadOnlyList<IChannel> channels, bool enabled, bool all)
    {
        var numbers = channels.Where(channel => channel.Type == type).Select(channel => channel.ChannelNumber).ToHashSet();
        if (numbers.Count == 0)
        {
            return null;
        }

        if (all)
        {
            // Including any a raw mask names that the device has not described yet.
            return _ => [];
        }

        return enabled ? current => [.. current, .. numbers] : current => [.. current.Except(numbers)];
    }

    /// <summary>The ADC enable bitmask for these analog channels.</summary>
    private static uint AdcEnableMask(HashSet<int> channelNumbers)
    {
        var mask = 0u;
        foreach (var channelNumber in channelNumbers)
        {
            if (channelNumber > MaxAdcBitmaskChannel)
            {
                throw new InvalidOperationException(
                    $"Analog channel number {channelNumber} exceeds the maximum ({MaxAdcBitmaskChannel}) that can be encoded in the ADC enable bitmask.");
            }

            mask |= 1u << channelNumber;
        }

        return mask;
    }

    /// <summary>
    /// Throws when the supplied channel is not part of this device's populated channel collection,
    /// which would mean mutating it could not affect the device-level enable state.
    /// </summary>
    private void EnsureChannelBelongs(IChannel channel)
    {
        if (!_host.SnapshotChannels().Contains(channel))
        {
            throw new ArgumentException("The specified channel does not belong to this device.", nameof(channel));
        }
    }

    /// <summary>
    /// The same, for a channel of one kind. A device only holds Core's own channel types, so one
    /// that belongs to it is one of those.
    /// </summary>
    private T EnsureBelongs<T>(IChannel channel)
        where T : class, IChannel
    {
        EnsureChannelBelongs(channel);
        return channel as T
            ?? throw new ArgumentException("The specified channel does not belong to this device.", nameof(channel));
    }
}
