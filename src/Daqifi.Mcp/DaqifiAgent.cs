using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Daqifi.Core.Channel;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Capabilities;
using Daqifi.Core.Device.Discovery;
using Daqifi.Core.Device.SdCard;
using Daqifi.Core.Logging.Export;
using Microsoft.Extensions.Logging;

namespace Daqifi.Mcp;

/// <summary>
/// Agent-facing facade over <c>Daqifi.Core</c>. Owns device discovery results and the set of
/// connected devices, and translates the high-level tool surface into the real SDK calls
/// (<see cref="DaqifiDeviceRegistry"/>, <see cref="IStreamingDevice"/> channel APIs,
/// <see cref="ISdCardOperations"/>). One instance is shared by all tool calls.
/// </summary>
/// <remarks>
/// The MCP transport may dispatch tool calls concurrently. Serialization is split by what is
/// actually being protected:
/// <list type="bullet">
/// <item><description>
/// <b>Per device</b> — a tool call that sends more than one command is wrapped in
/// <see cref="DaqifiDevice.RunExclusiveAsync{TResult}"/>, so Core holds that device's operation
/// lock for the whole sequence and nothing from another tool call splits it (issue #342). This
/// used to be <see cref="_gate"/>, which had the side effect of serializing every device against
/// every other one; two devices now run genuinely in parallel.
/// </description></item>
/// <item><description>
/// <b>Across the registry</b> — <see cref="_gate"/> is now only what it needs to be: the lock
/// around adding to and removing from the connection registry, where the thing being protected is
/// this agent's own state rather than any one device.
/// </description></item>
/// </list>
/// Read-only introspection takes neither: it snapshots the channel collection, so it never blocks
/// and never folds the live <c>Channels</c> view while the device's consumer thread repopulates it.
/// The live set of connections is owned by a <see cref="DaqifiDeviceRegistry"/> keyed by our own
/// <c>device_id</c>, which also supplies stale-handle pruning, disposal, and cross-transport
/// duplicate detection (the same unit reached over both USB and WiFi).
/// </remarks>
public sealed class DaqifiAgent
{
    private readonly ServerOptions _options;
    private readonly ILogger<DaqifiAgent> _logger;
    private readonly ConcurrentDictionary<string, IDeviceInfo> _discovered = new(StringComparer.Ordinal);
    private readonly DaqifiDeviceRegistry _registry = new();

    /// <summary>
    /// Serializes changes to the connection registry — connect, disconnect, shutdown. Device-level
    /// serialization is Core's job now (see the class remarks), so this no longer stands between
    /// two tool calls that touch different devices.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Which digital channels have had a PWM duty cycle actually commanded through
    /// <see cref="SetPwmOutputAsync"/> this session, as opposed to Core's uncommanded
    /// <see cref="DigitalChannel"/> default (#450). Keyed by channel identity so a fresh
    /// connection — which gets fresh channel instances — starts clean without explicit eviction.
    /// </summary>
    private readonly ConditionalWeakTable<IChannel, object> _pwmDutyCommanded = new();

    /// <summary>
    /// Which devices have had a PWM frequency actually commanded through
    /// <see cref="SetPwmOutputAsync"/> this session, as opposed to Core's uncommanded session
    /// default (#450). Keyed by device identity for the same reason as <see cref="_pwmDutyCommanded"/>.
    /// </summary>
    private readonly ConditionalWeakTable<IStreamingDevice, object> _pwmFrequencyCommanded = new();

    /// <summary>Presence-only marker value for the two "commanded" tables above.</summary>
    private static readonly object PwmCommandedMarker = new();

    /// <summary>
    /// Floor for <see cref="DiscoverAsync"/>'s <c>timeoutMs</c> clamp. A timeout below the identify
    /// handshake can never succeed and returns an empty list indistinguishable from "no device
    /// attached" (#448), which is why the floor exists at all; it was 250 ms, below the handshake
    /// time on every run.
    /// </summary>
    /// <remarks>
    /// Originally derived from an ~830 ms bench-measured serial identify. That handshake is now
    /// ~320-430 ms (#486), but the floor is deliberately left at 1000 ms rather than tracked down to
    /// it: the measurement is one macOS host with one candidate port and a warm USB-descriptor
    /// cache, while the number this floor has to cover is the slowest supported path — several
    /// candidate ports, and a platform that still runs an uncached descriptor query per port
    /// (#487). Lowering it on the strength of the fast case is how a caller ends up back at #448's
    /// empty-list-means-nothing-attached, so it should follow #487 rather than lead it.
    /// </remarks>
    internal const int MinDiscoveryTimeoutMs = 1000;

    /// <summary>
    /// Bounds on <c>read_channel_values</c>' wait. The floor covers the one thing a caller cannot
    /// see coming: a device that is not streaming yet sends nothing for the first
    /// 85-110 ms after the start command (bench-measured on a Nyquist over USB, firmware 3.7.2,
    /// six consecutive runs), so a shorter budget expires before the first frame could arrive and
    /// reports every channel as silent — the same wrong answer <see cref="MinDiscoveryTimeoutMs"/>
    /// exists to prevent, and it was measured the same way, by watching a 100 ms budget report
    /// nothing on a healthy device. The ceiling keeps a spot check from holding the device's
    /// operation lock for half a minute.
    /// </summary>
    internal const int MinReadTimeoutMs = 500;

    /// <inheritdoc cref="MinReadTimeoutMs"/>
    internal const int MaxReadTimeoutMs = 30_000;

    /// <summary>
    /// Bounds on a <c>capture_samples</c> window. The floor is the same stream-start cost
    /// <see cref="MinReadTimeoutMs"/> covers: a capture that has to start the stream spends its
    /// first ~100 ms with nothing arriving, so a window much shorter than this returns an empty
    /// capture from a working device. A capture holds the device exclusively for its whole duration
    /// (see <see cref="RunLiveCaptureAsync"/>), so the ceiling is what stops one tool call from
    /// owning the device indefinitely; an agent wanting more takes several captures.
    /// </summary>
    internal const int MinCaptureDurationMs = 250;

    /// <inheritdoc cref="MinCaptureDurationMs"/>
    internal const int MaxCaptureDurationMs = 60_000;

    /// <summary>
    /// Ceiling on the rows one capture returns. Every row travels to the agent as JSON, so this is
    /// a limit on the answer's size rather than on the device: 10,000 rows is a 10-second capture
    /// at 1 kHz, and past that an agent wants a file (SD logging plus <c>download_sd_file</c>)
    /// rather than a tool result.
    /// </summary>
    internal const int MaxCaptureRows = 10_000;

    /// <summary>
    /// Write buffer for the CSV a download exports. An exported CSV is several times the size of
    /// the log it came from — a row per timestamp, a column per channel — so it is worth more than
    /// the 4 KB a <see cref="FileStream"/> defaults to.
    /// </summary>
    /// <remarks>
    /// Deliberately its own number rather than <see cref="SdCardParseOptions.BufferSize"/>: that
    /// one is how much of the raw log is read at a time, this one is how much CSV is held before
    /// it goes to disk. They happen to be equal today, and nothing should have to keep them that
    /// way.
    /// </remarks>
    private const int CsvWriteBufferBytes = 64 * 1024;

    /// <summary>
    /// Clamps a caller-supplied discovery timeout to <c>[<see cref="MinDiscoveryTimeoutMs"/>, 30_000]</c>
    /// ms. Anything below the floor returns empty before the device can ever answer —
    /// indistinguishable from "nothing is attached" (#448); see <see cref="MinDiscoveryTimeoutMs"/>
    /// for how that floor is chosen. Serial probing can settle and return before the full budget;
    /// <see cref="WiFiDeviceFinder"/> listens for the whole window regardless (its receive loop runs
    /// until the timeout cancels it), so this floor mainly rejects budgets that could never have
    /// succeeded rather than guaranteeing an early return.
    /// </summary>
    internal static TimeSpan ClampDiscoveryTimeout(int timeoutMs) =>
        TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, MinDiscoveryTimeoutMs, 30_000));

    public DaqifiAgent(ServerOptions options, ILogger<DaqifiAgent>? logger = null)
    {
        _options = options;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DaqifiAgent>.Instance;
    }

    // ---------------------------------------------------------------- discovery

    /// <remarks>
    /// The enabled transports run concurrently, so the call costs the slower transport's window
    /// rather than the sum of both (#488). That matters because <see cref="WiFiDeviceFinder"/>
    /// listens for its whole budget by design, so running it before serial used to add its full
    /// timeout to every pass — on the first tool call of every session.
    /// </remarks>
    public async Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(
        int timeoutMs, bool wifi, bool serial, CancellationToken cancellationToken)
    {
        var timeout = ClampDiscoveryTimeout(timeoutMs);

        var infos = await DiscoverAcrossTransportsAsync(
            CreateTransportFinders(wifi, serial), timeout).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        var result = new List<DiscoveredDevice>();
        foreach (var info in infos)
        {
            var id = MintId(info);
            _discovered[id] = info;
            result.Add(DiscoveredDevice.From(id, info));
        }
        return result;
    }

    /// <summary>
    /// The transport finders a discovery pass runs over, in the order their results are reported.
    /// WiFi comes first so that a device reachable both ways keeps the ordering callers have always
    /// seen. Internal for testing.
    /// </summary>
    internal static IReadOnlyList<IDeviceFinder> CreateTransportFinders(bool wifi, bool serial)
    {
        var finders = new List<IDeviceFinder>(2);
        if (wifi)
        {
            finders.Add(new WiFiDeviceFinder());
        }

        if (serial)
        {
            finders.Add(new SerialDeviceFinder());
        }

        return finders;
    }

    /// <summary>
    /// Runs the supplied transport finders concurrently and returns one deduplicated device set.
    /// Owns the finders: they are disposed before this returns, so the caller passes them in and
    /// forgets them. Internal for testing.
    /// </summary>
    /// <remarks>
    /// The fan-out itself is <see cref="AllTransportsDeviceFinder"/> rather than a local
    /// <c>Task.WhenAll</c> — Core already had the primitive, and with it come two behaviours this
    /// path did not have: intra-transport duplicates collapse (the same unit reported twice by one
    /// finder is one entry, while the same unit reached over two transports stays two, because the
    /// identity is transport-prefixed), and one transport failing no longer sinks the pass. A WiFi
    /// probe on a host with no usable network used to throw straight out of <c>discover_devices</c>
    /// and lose the USB device that was found alongside it.
    /// </remarks>
    internal static async Task<IReadOnlyList<IDeviceInfo>> DiscoverAcrossTransportsAsync(
        IReadOnlyList<IDeviceFinder> finders, TimeSpan timeout)
    {
        // Both transports disabled is a legitimate no-op call, but AllTransportsDeviceFinder
        // requires at least one finder — answer it here rather than let the aggregator throw.
        if (finders.Count == 0)
        {
            return Array.Empty<IDeviceInfo>();
        }

        try
        {
            using var allTransports = new AllTransportsDeviceFinder(finders);
            return (await allTransports.DiscoverAsync(timeout).ConfigureAwait(false)).ToList();
        }
        finally
        {
            // AllTransportsDeviceFinder only disposes finders it created itself, so the ones
            // constructed above are ours to release — including when the pass throws.
            foreach (var finder in finders)
            {
                (finder as IDisposable)?.Dispose();
            }
        }
    }

    // ------------------------------------------------------------- connection

    /// <summary>
    /// Connects to a discovered device and files it in the registry under its <c>device_id</c>.
    /// Reconnecting an id that is already live (or connecting a second transport to a device that
    /// is already connected) is not an error: the registry's default duplicate policy hands back
    /// the existing connection, whose id is the one returned — so always use the returned
    /// <c>device_id</c> for follow-up calls rather than assuming it is the one passed in.
    /// </summary>
    public async Task<ConnectedDeviceInfo> ConnectAsync(string deviceId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_discovered.TryGetValue(deviceId, out var info))
            {
                throw new InvalidOperationException(
                    $"Unknown device_id '{deviceId}'. Call discover_devices first and use a device_id from its result.");
            }

            // The registry prunes stale handles (device dropped since it was registered) before
            // the duplicate check, so a dropped device reconnects instead of returning a dead one.
            var result = await _registry
                .ConnectAsync(info, deviceId, options: null, cancellationToken)
                .ConfigureAwait(false);

            if (result.Registration is null)
            {
                throw new InvalidOperationException(
                    $"Connection to '{deviceId}' was canceled by the duplicate-device policy.");
            }

            return ConnectedDeviceInfo.From(result.Registration.Key, result.Registration.Device);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> DisconnectAsync(string deviceId)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Remove owns the teardown: disconnect then dispose.
            return _registry.Remove(deviceId)
                ? $"Disconnected '{deviceId}'."
                : $"Device '{deviceId}' was not connected.";
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<ConnectedDeviceInfo> ListConnected() =>
        _registry.Devices.Select(r => ConnectedDeviceInfo.From(r.Key, r.Device)).ToList();

    // ----------------------------------------------------------- introspection

    public DeviceStatus GetStatus(string deviceId) => DeviceStatus.From(deviceId, Require(deviceId));

    public IReadOnlyList<ChannelInfo> ListChannels(string deviceId) =>
        Snapshot(Require(deviceId)).Select(ChannelInfo.From).ToList();

    // ----------------------------------------------------------- configuration

    /// <summary>
    /// Enables exactly the requested analog input channels (by channel number) and disables the
    /// rest. Configuration is applied through <see cref="IStreamingDevice.EnableChannels"/> /
    /// <see cref="IStreamingDevice.DisableChannel"/>, which recompute the device ADC enable bitmask.
    /// </summary>
    public async Task<ConfigureResult> ConfigureAnalogChannelsAsync(string deviceId, int[] enabledChannels)
    {
        RequireControl();
        var (device, streaming) = RequireStreaming(deviceId);

        return await device.RunExclusiveAsync(async _ =>
        {
            var analog = Snapshot(device).Where(c => c.Type == ChannelType.Analog).ToList();
            var validNumbers = analog.Select(c => c.ChannelNumber).ToHashSet();

            var wanted = new HashSet<int>(enabledChannels ?? Array.Empty<int>());
            var unknown = wanted.Where(n => !validNumbers.Contains(n)).OrderBy(n => n).ToList();
            if (unknown.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Unknown analog channel(s): {string.Join(", ", unknown)}. " +
                    $"This device has analog channels: {string.Join(", ", validNumbers.OrderBy(n => n))}.");
            }

            var toEnable = analog.Where(c => wanted.Contains(c.ChannelNumber)).ToList();
            foreach (var ch in analog.Where(c => !wanted.Contains(c.ChannelNumber) && c.IsEnabled))
            {
                streaming.DisableChannel(ch);
            }
            if (toEnable.Count > 0)
            {
                streaming.EnableChannels(toEnable);
            }

            // The device's authoritative rate cap (CapabilityStreaming.CurrentMaximumRateHz) is
            // scoped to the channel set enabled when the document was read — refresh it now so
            // set_sample_rate validates against the configuration that is actually live.
            await RefreshCapabilityDocumentAsync(device, streaming).ConfigureAwait(false);

            var adjustedFromHz = EnforceSampleRateCap(streaming);

            return new ConfigureResult(deviceId, EnabledAnalog(device), streaming.StreamingFrequency, adjustedFromHz);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Enables exactly the requested digital channels (by channel number) and disables the rest.
    /// The wire-level DIO enable is global — enabling any digital channel turns the whole port
    /// on — but per-channel enablement determines which channels the streaming decode samples.
    /// </summary>
    public async Task<ConfigureDigitalResult> ConfigureDigitalChannelsAsync(string deviceId, int[] enabledChannels)
    {
        RequireControl();
        var (device, streaming) = RequireStreaming(deviceId);

        return await device.RunExclusiveAsync(async _ =>
        {
            var digital = Snapshot(device).Where(c => c.Type == ChannelType.Digital).ToList();
            var validNumbers = digital.Select(c => c.ChannelNumber).ToHashSet();

            var wanted = new HashSet<int>(enabledChannels ?? Array.Empty<int>());
            var unknown = wanted.Where(n => !validNumbers.Contains(n)).OrderBy(n => n).ToList();
            if (unknown.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Unknown digital channel(s): {string.Join(", ", unknown)}. " +
                    $"This device has digital channels: {string.Join(", ", validNumbers.OrderBy(n => n))}.");
            }

            var toEnable = digital.Where(c => wanted.Contains(c.ChannelNumber)).ToList();
            foreach (var ch in digital.Where(c => !wanted.Contains(c.ChannelNumber) && c.IsEnabled))
            {
                streaming.DisableChannel(ch);
            }
            if (toEnable.Count > 0)
            {
                streaming.EnableChannels(toEnable);
            }

            // See ConfigureAnalogChannelsAsync: keeps CurrentMaximumRateHz current for
            // set_sample_rate even though the rate model itself ignores digital channels.
            await RefreshCapabilityDocumentAsync(device, streaming).ConfigureAwait(false);

            var adjustedFromHz = EnforceSampleRateCap(streaming);

            return new ConfigureDigitalResult(deviceId, EnabledDigital(device), streaming.StreamingFrequency, adjustedFromHz);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets a digital channel's direction (input or output) via
    /// <see cref="IStreamingDevice.SetDioDirection"/>.
    /// </summary>
    public async Task<DigitalPinResult> SetDigitalDirectionAsync(string deviceId, int channel, string direction)
    {
        var parsed = ParseDirection(direction);

        RequireControl();
        var (device, streaming) = RequireStreaming(deviceId);
        var ch = RequireDigitalChannel(device, channel);

        // The PWM check runs inside the same exclusive section as the write it guards, so no
        // concurrent tool call can toggle PWM between the check and the send (#449) — an outer,
        // unlocked check would leave that race open and could let Core's SDK-oriented exception
        // (naming SetPwmEnabled, not an MCP tool) leak through instead of this guard's message.
        return await device.RunExclusiveAsync(_ =>
        {
            RequirePwmDisabled(ch);
            streaming.SetDioDirection(ch, parsed);

            return Task.FromResult(DigitalPinResult.From(deviceId, ch));
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Drives a digital output channel high or low. A channel still in input direction is
    /// switched to output first, so a single call is enough to drive a pin.
    /// </summary>
    public async Task<DigitalPinResult> SetDigitalOutputAsync(string deviceId, int channel, bool high)
    {
        RequireControl();
        var (device, streaming) = RequireStreaming(deviceId);
        var ch = RequireDigitalChannel(device, channel);

        // Direction-then-value is the sequence that must not be split: another tool call landing
        // between them could flip the pin back to input before the value is driven. The PWM check
        // runs inside the same exclusive section for the same reason — see SetDigitalDirectionAsync.
        return await device.RunExclusiveAsync(_ =>
        {
            RequirePwmDisabled(ch);

            if (ch.Direction != ChannelDirection.Output)
            {
                streaming.SetDioDirection(ch, ChannelDirection.Output);
            }

            streaming.SetDioValue(ch, high);

            return Task.FromResult(DigitalPinResult.From(deviceId, ch));
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Configures and starts PWM output on a PWM-capable digital channel: duty first, then the
    /// shared frequency when supplied, then enable. The device's PWM frequency is global (one
    /// hardware timer drives all PWM channels).
    /// </summary>
    public async Task<PwmResult> SetPwmOutputAsync(string deviceId, int channel, int dutyCyclePercent, int frequencyHz)
    {
        RequireControl();
        var (device, streaming) = RequireStreaming(deviceId);
        var ch = RequireDigitalChannel(device, channel);

        return await device.RunExclusiveAsync(_ =>
        {
            // Duty before frequency before enable: the firmware applies a stored duty when the
            // frequency is (re)programmed, so this order never leaves a stale compare value.
            // Core.PwmFrequencyHz always holds a commandable value (a session default when
            // nothing has been set yet), so a caller-supplied frequencyHz of 0 just means "use
            // that value" rather than requiring special-casing here.
            var desiredFrequencyHz = frequencyHz != 0 ? frequencyHz : streaming.PwmFrequencyHz;

            streaming.SetPwmDutyCycle(ch, dutyCyclePercent);
            _pwmDutyCommanded.AddOrUpdate(ch, PwmCommandedMarker);

            // Reprogram the shared device-wide timer. Core skips the SCPI round-trip when the
            // frequency is unchanged from what it last sent this connection (#345), so a duty-only
            // update or re-enable no longer costs an extra round-trip and needs no agent-side cache.
            streaming.SetPwmFrequency(desiredFrequencyHz);
            _pwmFrequencyCommanded.AddOrUpdate(streaming, PwmCommandedMarker);

            streaming.SetPwmEnabled(ch, true);

            return Task.FromResult(PwmResult.From(deviceId, streaming, ch, dutyCommanded: true, frequencyCommanded: true));
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops PWM output on a digital channel. The pin is left high-impedance; use
    /// set_digital_direction / set_digital_output to drive it digitally again. Deliberately sent
    /// for every digital channel, including one that is not <see cref="IDigitalChannel.IsPwmCapable"/>:
    /// that is Core's only recovery command for a channel the firmware flagged PWM-active before
    /// failing its capability check (e.g. via a raw command outside Core's guard), so skipping the
    /// send here would remove the one MCP-level way to clear that wedge. On a channel that was never
    /// actually armed the firmware rejects the command, but that send is fire-and-forget — Core does
    /// not read the device's error queue back, so this call still succeeds and neither throws nor
    /// reports the rejection in its <see cref="PwmResult"/> (#450).
    /// </summary>
    public async Task<PwmResult> DisablePwmAsync(string deviceId, int channel)
    {
        RequireControl();
        var (device, streaming) = RequireStreaming(deviceId);
        var ch = RequireDigitalChannel(device, channel);

        return await device.RunExclusiveAsync(_ =>
        {
            streaming.SetPwmEnabled(ch, false);

            var dutyCommanded = _pwmDutyCommanded.TryGetValue(ch, out var dutyMarker);
            var frequencyCommanded = _pwmFrequencyCommanded.TryGetValue(streaming, out var frequencyMarker);
            return Task.FromResult(PwmResult.From(deviceId, streaming, ch, dutyCommanded, frequencyCommanded));
        }).ConfigureAwait(false);
    }

    public async Task<SampleRateResult> SetSampleRateAsync(string deviceId, int rateHz)
    {
        if (rateHz < 1)
        {
            throw new InvalidOperationException("rate_hz must be >= 1.");
        }

        RequireControl();
        var (device, streaming) = RequireStreaming(deviceId);

        return await device.RunExclusiveAsync(_ =>
        {
            var cap = ComputeSampleRateCapHz(streaming);

            // A cap of 0 is a real answer ("no channels enabled right now"), not a parsing gap —
            // see Daqifi.Core's SampleRateCap. Called out with its own
            // message rather than falling into the generic "exceeds the maximum" rejection below:
            // that message reads as "you asked for too much", which points an agent at lowering
            // rateHz when the actual remedy is enabling a channel first.
            if (cap <= 0)
            {
                throw new InvalidOperationException(
                    "No channels are enabled, so the device has no sample-rate capacity right now. " +
                    "Enable at least one channel with configure_analog_channels or " +
                    "configure_digital_channels before setting a sample rate.");
            }

            if (rateHz > cap)
            {
                throw new InvalidOperationException(
                    $"Requested {rateHz} Hz exceeds the maximum {cap} Hz for the currently enabled channels.");
            }

            streaming.StreamingFrequency = rateHz;
            return Task.FromResult(new SampleRateResult(deviceId, rateHz));
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Computes the effective sample-rate ceiling for <paramref name="streaming"/> right now: the
    /// device's own cap for its currently enabled channels
    /// (<see cref="IStreamingDevice.MaximumStreamingFrequencyHz"/>, kept current by
    /// <see cref="RefreshCapabilityDocumentAsync"/> after every channel-configuration call),
    /// lowered further by <c>--max-sample-rate-hz</c> if one was configured. Shared by
    /// <see cref="SetSampleRateAsync"/> (to validate a request) and
    /// <see cref="ConfigureAnalogChannelsAsync"/>/<see cref="ConfigureDigitalChannelsAsync"/> (to
    /// re-validate the rate that is already live once the channel set — and therefore the cap —
    /// changes underneath it).
    /// </summary>
    /// <remarks>
    /// The device half of this arithmetic lives in Core (<see cref="SampleRateCap"/>) so every
    /// consumer of the library gets it, not only this server (#481). What stays here is the one
    /// part that is genuinely the server's: its own operator-configured clamp. Zero is a real
    /// answer (no channels enabled) and passes through unfloored.
    /// </remarks>
    private int ComputeSampleRateCapHz(IStreamingDevice streaming) =>
        ApplyServerRateClamp(streaming.MaximumStreamingFrequencyHz, _options.MaxSampleRateHz);

    /// <summary>
    /// Lowers a device's own cap to the operator's <c>--max-sample-rate-hz</c> when one is
    /// configured and is the stricter of the two. The whole of what this server still decides
    /// about sample rate; everything else is Core's (#481).
    /// </summary>
    /// <param name="deviceCapHz">The device's cap, from <see cref="IStreamingDevice.MaximumStreamingFrequencyHz"/>.</param>
    /// <param name="maxSampleRateHzOption">The server's <c>--max-sample-rate-hz</c>, or <c>null</c>.</param>
    /// <returns>The effective cap in Hz.</returns>
    internal static int ApplyServerRateClamp(int deviceCapHz, int? maxSampleRateHzOption) =>
        maxSampleRateHzOption.HasValue ? Math.Min(maxSampleRateHzOption.Value, deviceCapHz) : deviceCapHz;

    /// <summary>
    /// Re-validates the already-live <see cref="IStreamingDevice.StreamingFrequency"/> against the
    /// cap this device's channel set now allows, lowering it when the set just enabled by
    /// <see cref="ConfigureAnalogChannelsAsync"/>/<see cref="ConfigureDigitalChannelsAsync"/>
    /// shrank the cap below the rate a previous <see cref="SetSampleRateAsync"/> call left running
    /// (#447) — otherwise that stale rate stays live, is echoed back to the agent as if it were
    /// still valid, and is unreachable through <see cref="SetSampleRateAsync"/>'s own guard, since
    /// re-requesting the same value now fails.
    /// </summary>
    /// <remarks>
    /// A cap of zero (nothing enabled) leaves the rate alone rather than driving it to zero — zero
    /// is not a meaningful streaming frequency, and the channel set is expected to change again
    /// before streaming actually starts.
    /// </remarks>
    /// <returns>
    /// The rate that was live before the adjustment, or <c>null</c> when no adjustment was needed.
    /// </returns>
    private int? EnforceSampleRateCap(IStreamingDevice streaming)
    {
        var cap = ComputeSampleRateCapHz(streaming);
        var (newRateHz, adjustedFromHz) = SampleRateCap.Enforce(streaming.StreamingFrequency, cap);
        if (adjustedFromHz.HasValue)
        {
            streaming.StreamingFrequency = newRateHz;
        }

        return adjustedFromHz;
    }

    // --------------------------------------------------------- SD card logging

    public async Task<StartLoggingResult> StartLoggingAsync(
        string deviceId, string? fileName, string format, CancellationToken cancellationToken)
    {
        var fmt = ParseFormat(format);

        RequireControl();
        var (device, streaming) = RequireStreaming(deviceId);
        var sd = RequireSdCard(device);

        return await device.RunExclusiveAsync(async ct =>
        {
            // Use-time backstop for #447: EnforceSampleRateCap already keeps the live rate at or
            // under the cap through every configure_* call, but re-check here too, since this is
            // the point an out-of-range rate would actually reach the firmware. The firmware's
            // response to an over-cap rate is a silent one — it refuses with "Data out of range"
            // and streams zero samples, with no exception and no ErrorOccurred — so failing loudly
            // here is the only way an agent finds out before a logging session comes back empty.
            var cap = ComputeSampleRateCapHz(streaming);
            if (cap > 0 && streaming.StreamingFrequency > cap)
            {
                throw new InvalidOperationException(
                    $"The current sample rate ({streaming.StreamingFrequency} Hz) exceeds the " +
                    $"maximum {cap} Hz for the currently enabled channels. Call set_sample_rate " +
                    "with a value at or below the maximum before starting SD-card logging.");
            }

            // Core owns the naming convention and reports the effective on-card filename back to
            // us, so we no longer duplicate the log_{timestamp} generation here.
            var session = await sd.StartSdCardLoggingSessionAsync(fileName, channelMask: null, format: fmt, ct)
                .ConfigureAwait(false);

            return new StartLoggingResult(
                deviceId, session.FileName, session.Format.ToString(), streaming.StreamingFrequency, EnabledAnalog(device));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> StopLoggingAsync(string deviceId, CancellationToken cancellationToken)
    {
        RequireControl();
        var (device, _) = RequireStreaming(deviceId);
        var sd = RequireSdCard(device);

        return await device.RunExclusiveAsync(async ct =>
        {
            await sd.StopSdCardLoggingAsync(ct).ConfigureAwait(false);
            return $"Stopped SD-card logging on '{deviceId}'.";
        }, cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ SD card retrieval

    /// <summary>
    /// Lists the files on the device's SD card. Read-only: available even under
    /// <c>--read-only</c>.
    /// </summary>
    public async Task<SdFileListing> ListSdFilesAsync(string deviceId, CancellationToken cancellationToken)
    {
        var device = Require(deviceId);
        var sd = RequireSdCard(device);

        IReadOnlyList<SdCardFileInfo> files;
        try
        {
            files = await sd.GetSdCardFilesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (Rewrite(ex, deviceId) is { } rewritten)
        {
            throw rewritten;
        }

        var entries = files.Select(SdFileEntry.From).ToList();
        return new SdFileListing(deviceId, entries.Count, entries);
    }

    /// <summary>
    /// Reports free/used/total space on the device's SD card. Read-only: available even under
    /// <c>--read-only</c>.
    /// </summary>
    public async Task<SdStorageReport> GetSdStorageAsync(string deviceId, CancellationToken cancellationToken)
    {
        var device = Require(deviceId);
        var sd = RequireSdCard(device);

        try
        {
            var storage = await sd.GetSdCardStorageAsync(cancellationToken).ConfigureAwait(false);
            return SdStorageReport.From(deviceId, storage);
        }
        catch (Exception ex) when (Rewrite(ex, deviceId) is { } rewritten)
        {
            throw rewritten;
        }
    }

    /// <summary>
    /// Downloads an SD-card file to this machine and, when <paramref name="exportCsv"/> is set,
    /// parses it and writes a CSV alongside it.
    /// </summary>
    /// <remarks>
    /// Not gated by <c>--read-only</c>: it reads device data and changes nothing on the card. It
    /// does write two files into this machine's temp directory, which is the only way the agent
    /// can be handed the data at all.
    /// </remarks>
    public async Task<SdDownloadReport> DownloadSdFileAsync(
        string deviceId, string fileName, bool exportCsv, CancellationToken cancellationToken)
    {
        var name = RequireFileName(fileName);
        var device = Require(deviceId);
        var sd = RequireSdCard(device);

        // Snapshotted BEFORE the download: the download suspends the protobuf consumer and can
        // leave the transport mid-switch on a timeout, so this is the last point the live channel
        // state is guaranteed readable. It carries the calibration and — the part that actually
        // matters — the timestamp clock, which firmware 3.7.2 and earlier do not write into SD
        // logs at all. Without it the parser falls back to a 50 MHz guess against a 42 MHz clock
        // and every reconstructed timestamp comes out ~19% fast.
        var liveConfig = exportCsv ? SdCardDeviceConfiguration.FromDevice(device) : null;

        name = await ResolveFileNameAsync(sd, name, cancellationToken).ConfigureAwait(false);

        SdCardDownloadResult download;
        try
        {
            download = await sd.DownloadSdCardFileAsync(name, progress: null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (Rewrite(ex, deviceId) is { } rewritten)
        {
            throw rewritten;
        }

        var localPath = download.FilePath
            ?? throw new InvalidOperationException(
                $"The download of '{name}' reported no local file path, so there is nothing to read.");

        string? csvPath = null;
        long? rowCount = null;
        long? sampleCount = null;
        string? csvError = null;

        if (exportCsv)
        {
            try
            {
                (csvPath, rowCount, sampleCount, csvError) =
                    await ExportCsvAsync(localPath, name, liveConfig, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The raw file is already on disk and took the whole transfer to get there.
                // Failing the tool call here would hand back an error and no path, and the agent
                // would re-download to discover the same thing.
                csvError = ex.Message;
                _logger.LogWarning(ex, "CSV export failed for '{FileName}'; the raw download at {Path} is unaffected.", name, localPath);
            }
        }

        return new SdDownloadReport(
            deviceId,
            download.FileName,
            download.FileSize,
            Math.Round(download.Duration.TotalSeconds, 3),
            localPath,
            csvPath,
            rowCount,
            sampleCount,
            csvError);
    }

    /// <summary>
    /// Deletes a file from the device's SD card. Destructive, so it is refused under
    /// <c>--read-only</c>.
    /// </summary>
    public async Task<SdDeleteResult> DeleteSdFileAsync(
        string deviceId, string fileName, CancellationToken cancellationToken)
    {
        RequireControl();
        var name = RequireFileName(fileName);
        var device = Require(deviceId);
        var sd = RequireSdCard(device);

        try
        {
            await sd.DeleteSdCardFileAsync(name, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (Rewrite(ex, deviceId) is { } rewritten)
        {
            throw rewritten;
        }

        return new SdDeleteResult(deviceId, name);
    }

    /// <summary>
    /// Confirms the file is actually on the card before a transfer is attempted, and returns the
    /// name exactly as the card spells it.
    /// </summary>
    /// <remarks>
    /// Asking the firmware for a file that is not there produces no answer at all: the transfer
    /// stalls and gives up 20 seconds later with "the device stopped feeding the transfer", which
    /// tells the caller to retry the one thing that cannot work. A name that does not appear in a
    /// listing is worth failing on immediately, with the names that do.
    /// <para>
    /// Free when the caller listed first — the check runs against the listing Core already cached.
    /// The re-listing on a miss is what keeps it honest: a file recorded since that listing (by
    /// <c>start_sd_logging</c>, say) is genuinely on the card, and must not be rejected for being
    /// absent from a stale snapshot.
    /// </para>
    /// </remarks>
    internal static async Task<string> ResolveFileNameAsync(
        ISdCardOperations sd, string fileName, CancellationToken cancellationToken)
    {
        var match = Match(sd.SdCardFiles, fileName);
        if (match is not null)
        {
            return match;
        }

        IReadOnlyList<SdCardFileInfo> files;
        try
        {
            files = await sd.GetSdCardFilesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // The listing is a courtesy, not a gate. If it cannot be taken — a busy card, a
            // firmware that will not answer — let the download itself be the thing that fails, so
            // this check can never be the reason a downloadable file is refused.
            return fileName;
        }

        match = Match(files, fileName);
        if (match is not null)
        {
            return match;
        }

        var available = files.Count == 0
            ? "The card is empty."
            : "On the card: " + string.Join(", ", files.Take(20).Select(f => f.FileName))
              + (files.Count > 20 ? $", and {files.Count - 20} more (call list_sd_files for all of them)." : ".");

        throw new InvalidOperationException($"There is no file named '{fileName}' on the SD card. {available}");

        // Matched without case sensitivity because the card's filesystem is not case-sensitive,
        // and the name that comes back is the card's own spelling — the firmware is handed what it
        // put in the listing rather than whatever the caller typed.
        static string? Match(IReadOnlyList<SdCardFileInfo> files, string fileName) => files
            .FirstOrDefault(f => string.Equals(f.FileName, fileName, StringComparison.OrdinalIgnoreCase))
            ?.FileName;
    }

    /// <summary>
    /// Parses a downloaded log and writes a CSV next to it, returning the CSV path, the two counts
    /// the caller reports (CSV lines and log entries), and a warning when the CSV was written but
    /// is known to be incomplete.
    /// </summary>
    internal static async Task<(string CsvPath, long Rows, long Samples, string? Warning)> ExportCsvAsync(
        string localPath,
        string deviceFileName,
        SdCardDeviceConfiguration? liveConfig,
        CancellationToken cancellationToken)
    {
        // Format comes from the DEVICE-side name, not the local one: the local file is a temp file
        // Core minted, and deriving the format from it would make the parse depend on a name the
        // caller never chose.
        var format = SdCardFileParserFactory.DetectFormat(deviceFileName);

        var parseOptions = new SdCardParseOptions { ConfigurationOverride = liveConfig };

        var stream = new FileStream(
            localPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: parseOptions.BufferSize, useAsync: true);

        await using (stream.ConfigureAwait(false))
        {
            var session = await SdCardFileParserFactory
                .ParseWithFormatAsync(stream, Path.GetFileName(deviceFileName), format, parseOptions, cancellationToken)
                .ConfigureAwait(false);

            // The file's own status message wins; the live device fills in when the log carries
            // none. Zero is the honest last resort — a device with no analog channels exports the
            // digital column alone rather than inventing width.
            var analogCount = session.DeviceConfig?.AnalogPortCount ?? liveConfig?.AnalogPortCount ?? 0;

            var source = new SdCardSampleSource(
                session.Samples,
                session.DeviceConfig?.DeviceSerialNumber ?? liveConfig?.DeviceSerialNumber,
                analogCount);

            // Appended, not swapped: the firmware logs in CSV as well as protobuf and JSON, and
            // Core's temp file keeps the device-side extension — so Path.ChangeExtension(".csv")
            // would hand back the path of the file being read for a .csv log and truncate the
            // download on the way to parsing it. A suffix cannot collide with what it is added to.
            var csvPath = localPath + ".csv";

            // Every failure from here on deletes the file it was writing. A caller that is told
            // "no CSV" gets CsvPath=null, so a half-written one left on disk would be litter
            // nobody can name — not even to clean it up. The delete runs after the streams have
            // been disposed by the blocks below, which is what makes it work on Windows too.
            try
            {
                // useAsync, like the input stream above: CsvExporter writes through WriteAsync for
                // every row, and a FileStream opened without it services those on the thread pool
                // instead of the OS async path. See CsvWriteBufferBytes for why the buffer is its
                // own number and not the parse buffer.
                var fileStream = new FileStream(
                    csvPath, FileMode.Create, FileAccess.Write, FileShare.Read,
                    bufferSize: CsvWriteBufferBytes, useAsync: true);
                await using (fileStream.ConfigureAwait(false))
                {
                    var writer = new StreamWriter(fileStream);
                    await using (writer.ConfigureAwait(false))
                    {
                        await new CsvExporter()
                            .ExportAsync(source, writer, new CsvExportOptions { UseRelativeTime = false }, progress: null, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                if (source.SampleCount == 0)
                {
                    // A header and nothing else reads like a successful export of a file that had
                    // no data in it. Say so instead, and leave the raw download in place for
                    // whoever wants to look at why.
                    throw new InvalidOperationException(
                        $"'{deviceFileName}' parsed to zero samples, so no CSV was written. The raw file is still available at the returned path.");
                }
            }
            catch
            {
                TryDelete(csvPath);
                throw;
            }

            // Reported rather than thrown, and the CSV is kept: the columns that did map are real
            // data. Silence is the one unacceptable option — an agent analysing a CSV that quietly
            // lost channels has no way to notice.
            var warning = source.DroppedAnalogColumns > 0
                ? $"The CSV is incomplete: samples in '{deviceFileName}' carry {analogCount + source.DroppedAnalogColumns} " +
                  $"analog values but only {analogCount} analog channels are known, so {source.DroppedAnalogColumns} " +
                  "column(s) were dropped."
                : null;

            return (csvPath, source.RowCount, source.SampleCount, warning);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort; a leftover temp file is not worth failing over */ }
    }

    /// <summary>
    /// Rewrites the SD-card failures that have an MCP-specific next step into messages naming the
    /// tool to call, and lets everything else through untouched (Core's own messages are already
    /// written for a human). Used as an exception filter, so a null return leaves the original
    /// exception — and its stack — completely unmodified.
    /// </summary>
    private static Exception? Rewrite(Exception ex, string deviceId) => ex switch
    {
        SdCardBusyException => new InvalidOperationException(
            $"Device '{deviceId}' is busy with the SD card, which usually means it is still logging. " +
            "Call stop_sd_logging first, then retry.", ex),

        SdCardEmptyTransferException empty => new InvalidOperationException(
            $"{empty.Message} This is also what a live-streaming session does to the SD subsystem: " +
            "run SD retrieval before streaming in the same connection, or start and stop an SD " +
            "recording to re-arm it.", ex),

        _ => null,
    };

    /// <summary>
    /// Validates a caller-supplied SD file name and returns it trimmed.
    /// </summary>
    /// <remarks>
    /// The character check mirrors the one Core applies before putting a name into an SCPI command
    /// (quotes, newlines and semicolons), widened to every control character. It has to happen
    /// here, not just in Core: the listing pre-flight runs first and would otherwise answer a name
    /// full of newlines with "there is no file called &lt;several lines of garbage&gt;" instead of
    /// the plain "that is not a valid file name" the caller can act on.
    /// </remarks>
    private static string RequireFileName(string? fileName)
    {
        var trimmed = fileName?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new InvalidOperationException(
                "A file name is required. Call list_sd_files to see what is on the card.");
        }

        foreach (var c in trimmed)
        {
            if (char.IsControl(c) || c is '"' or ';')
            {
                throw new InvalidOperationException(
                    "That is not a valid SD file name: quotes, semicolons and control characters "
                    + "(including newlines and tabs) are not allowed. Call list_sd_files and pass a "
                    + "name exactly as it is listed.");
            }
        }

        return trimmed;
    }

    // ------------------------------------------------------------------ live data

    /// <summary>
    /// Reads the most recent value on every enabled channel. Waits only as long as it has to: the
    /// read ends as soon as every enabled channel has reported once, and gives up at
    /// <paramref name="timeoutMs"/>.
    /// </summary>
    /// <remarks>
    /// Attaches to the device's live stream, and starts one only if nothing is streaming yet — in
    /// which case it stops it again afterwards, leaving the device as it found it. A session the
    /// caller already had running is never stopped.
    /// </remarks>
    public async Task<ChannelReadings> ReadChannelValuesAsync(
        string deviceId, int timeoutMs, CancellationToken cancellationToken)
    {
        var run = await RunLiveCaptureAsync(
            deviceId,
            channels => new LatestValueSink(channels),
            ClampLiveWindow(timeoutMs, MinReadTimeoutMs, MaxReadTimeoutMs),
            cancellationToken).ConfigureAwait(false);

        var readings = run.Channels.Select(key =>
        {
            var sample = run.Sink.Latest(key);
            return new ChannelReading(
                key.Label,
                key.Number,
                key.Type.ToString(),
                sample?.Value,
                sample?.Timestamp,
                sample?.DeviceTimestamp,
                sample?.RawValue);
        }).ToList();

        return new ChannelReadings(
            deviceId,
            run.SampleRateHz,
            run.StartedStream,
            run.Sink.ReportedChannelCount,
            Math.Round(run.Outcome.Elapsed.TotalSeconds, 3),
            run.DroppedSampleCount,
            run.Sink.UnexpectedSampleCount,
            readings);
    }

    /// <summary>
    /// Captures a bounded block of live data: one row per sample tick, one column per enabled
    /// channel. Ends at whichever budget runs out first — <paramref name="durationMs"/> or
    /// <paramref name="maxRows"/>.
    /// </summary>
    /// <inheritdoc cref="ReadChannelValuesAsync" path="/remarks"/>
    public async Task<CaptureResult> CaptureSamplesAsync(
        string deviceId, int durationMs, int maxRows, CancellationToken cancellationToken)
    {
        var rowBudget = Math.Clamp(maxRows, 1, MaxCaptureRows);

        var run = await RunLiveCaptureAsync(
            deviceId,
            channels => new SampleRowSink(channels, rowBudget),
            ClampLiveWindow(durationMs, MinCaptureDurationMs, MaxCaptureDurationMs),
            cancellationToken).ConfigureAwait(false);

        var rows = run.Sink.Complete();

        return new CaptureResult(
            deviceId,
            run.SampleRateHz,
            run.StartedStream,
            Math.Round(run.Outcome.Elapsed.TotalSeconds, 3),
            rows.Count,
            run.Sink.SampleCount,
            run.DroppedSampleCount,
            run.Sink.UnexpectedSampleCount,
            RateHz(rows.Count, run.Outcome.DataElapsed),
            RateHz(rows.Count, rows.Count > 1 ? rows[^1].Timestamp - rows[0].Timestamp : TimeSpan.Zero),
            run.Sink.RowBudgetFilled,
            run.Channels.Select(c => c.Label).ToList(),
            rows);
    }

    /// <summary>
    /// Rows per second over <paramref name="span"/>, which covers the gaps <i>between</i> the rows —
    /// hence one interval fewer than there are rows. Zero when there is no span to divide by, which
    /// is every capture short enough to hold one row or none.
    /// </summary>
    private static double RateHz(int rowCount, TimeSpan span) =>
        rowCount > 1 && span > TimeSpan.Zero ? Math.Round((rowCount - 1) / span.TotalSeconds, 1) : 0;

    /// <summary>Everything one live-data tool call comes back with, all of it decided under the device's lock.</summary>
    private readonly record struct LiveRun<TSink>(
        TSink Sink,
        IReadOnlyList<ChannelKey> Channels,
        LiveCaptureOutcome Outcome,
        long DroppedSampleCount,
        bool StartedStream,
        int SampleRateHz);

    /// <summary>
    /// Runs one live-data tool call: takes the device exclusively, decides whether this call has to
    /// start the stream, builds the sink over the channels that are enabled at that moment, reads
    /// into it for <paramref name="window"/>, and stops the stream again if it started one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every decision is made <b>inside</b> the exclusive block on purpose. Deciding outside it —
    /// the obvious shape, since the answers are all readable without the lock — leaves room for
    /// another tool call to land in between and turn each answer stale: a capture would stop a
    /// stream someone else had started, or align its columns to a channel set that was
    /// reconfigured while it waited for the lock.
    /// </para>
    /// <para>
    /// The device is held for the whole capture, which is far longer than the sequences the other
    /// tools hold it for, and that is the point: a <c>configure_*</c> landing mid-capture would
    /// change the channel set the rows are aligned to and the data would silently stop meaning what
    /// its columns say. The budgets that bound a capture are what keep that block bounded.
    /// </para>
    /// </remarks>
    private async Task<LiveRun<TSink>> RunLiveCaptureAsync<TSink>(
        string deviceId,
        Func<IReadOnlyList<ChannelKey>, TSink> createSink,
        TimeSpan window,
        CancellationToken cancellationToken)
        where TSink : ILiveSampleSink
    {
        var (device, streaming, live) = RequireLiveSamples(deviceId);

        return await device.RunExclusiveAsync(async ct =>
        {
            RequireNotLoggingToSdCard(device, deviceId);
            var startStream = DecideStreamOwnership(streaming.IsStreaming, _options.ReadOnly);
            var channels = RequireEnabledChannels(device, deviceId);
            var sink = createSink(channels);

            // A device-wide running total, so what this capture cost is the difference across it.
            var droppedBefore = live.DroppedLiveSampleCount;
            try
            {
                var outcome = await LiveSampleCapture.DrainAsync(
                    live.StreamSamplesAsync(),
                    sink,
                    window,
                    startStream ? streaming.StartStreaming : null,
                    ct).ConfigureAwait(false);

                return new LiveRun<TSink>(
                    sink,
                    channels,
                    outcome,
                    live.DroppedLiveSampleCount - droppedBefore,
                    startStream,
                    streaming.StreamingFrequency);
            }
            finally
            {
                if (startStream)
                {
                    try
                    {
                        streaming.StopStreaming();
                    }
                    catch (DeviceNotConnectedException)
                    {
                        // The device went away mid-capture, which the capture itself is already
                        // throwing about. Nothing to stop, and that exception is the useful one.
                    }
                }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves a device id to the three views the live-data tools need, failing when the device
    /// cannot supply live samples at all. Only the parts that cannot change under a running
    /// server — a device's identity and what it implements — are settled here; everything about its
    /// current state is decided under its lock, in <see cref="RunLiveCaptureAsync"/>.
    /// </summary>
    private (DaqifiDevice Device, IStreamingDevice Streaming, ILiveSampleSource Live) RequireLiveSamples(string deviceId)
    {
        var (device, streaming) = RequireStreaming(deviceId);

        if (device is not ILiveSampleSource live)
        {
            throw new InvalidOperationException(
                $"Device '{deviceId}' does not support reading live samples.");
        }

        return (device, streaming, live);
    }

    /// <summary>
    /// Refuses a live read while the device is recording to its SD card. A card recording routes
    /// the device's data to the card instead of to this machine, so a capture would wait out its
    /// whole window and return nothing — and, because a recording also reads as "streaming", it
    /// would look to the rest of this path like a session someone else had started.
    /// </summary>
    private static void RequireNotLoggingToSdCard(DaqifiDevice device, string deviceId)
    {
        if (device is ISdCardOperations { IsLoggingToSdCard: true })
        {
            throw new InvalidOperationException(
                $"Device '{deviceId}' is logging to its SD card, which sends the data to the card "
                + "instead of to this machine, so no live samples arrive here. Call stop_sd_logging "
                + "first, then read live values or download_sd_file to read what was recorded.");
        }
    }

    /// <summary>
    /// The whole of the live tools' <c>--read-only</c> rule, kept apart from the device so it can be
    /// tested without one: reading a stream that is already running changes nothing on the device
    /// and stays available; starting one does not.
    /// </summary>
    /// <returns><c>true</c> when the caller must start the stream (and stop it again afterwards).</returns>
    internal static bool DecideStreamOwnership(bool isStreaming, bool readOnly)
    {
        if (isStreaming)
        {
            return false;
        }

        if (readOnly)
        {
            throw new InvalidOperationException(
                "Server is running in --read-only mode and this device is not streaming, so there "
                + "is nothing to read: starting its stream is a change this mode does not allow. "
                + "Reading from a stream that is already running is still available.");
        }

        return true;
    }

    /// <summary>
    /// The enabled channels a capture reads, analog first and each type in channel order — the
    /// column order every row is aligned to.
    /// </summary>
    private static IReadOnlyList<ChannelKey> RequireEnabledChannels(DaqifiDevice device, string deviceId)
    {
        var channels = Snapshot(device)
            .Where(c => c.IsEnabled)
            .Select(ChannelKey.For)
            .OrderBy(k => k.Type == ChannelType.Analog ? 0 : 1)
            .ThenBy(k => k.Number)
            .ToList();

        if (channels.Count == 0)
        {
            throw new InvalidOperationException(
                $"No channels are enabled on '{deviceId}', so the device has nothing to send. "
                + "Enable at least one with configure_analog_channels or configure_digital_channels.");
        }

        return channels;
    }

    /// <summary>
    /// Clamps a caller-supplied millisecond budget into the range a capture supports. Clamped
    /// rather than rejected: unlike a sample rate, a budget outside the range still has an obvious
    /// intent ("as long as you'll give me"), and the result reports the duration actually used.
    /// Internal for testing.
    /// </summary>
    internal static TimeSpan ClampLiveWindow(int milliseconds, int minimum, int maximum) =>
        TimeSpan.FromMilliseconds(Math.Clamp(milliseconds, minimum, maximum));

    // ------------------------------------------------------------------ shutdown

    /// <summary>
    /// Best-effort teardown of every connected device. Called on process shutdown so serial ports
    /// are released (and an in-progress SD capture is stopped) instead of being left held.
    /// </summary>
    public async Task ShutdownAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var registration in _registry.Devices)
            {
                try
                {
                    if (registration.Device is ISdCardOperations { IsLoggingToSdCard: true } sd)
                    {
                        await sd.StopSdCardLoggingAsync().ConfigureAwait(false);
                    }
                }
                catch { /* best effort */ }
            }

            // Clear disconnects and disposes every registered device.
            _registry.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Files an already-connected device under <paramref name="deviceId"/>, as if
    /// <see cref="ConnectAsync"/> had produced it.
    /// </summary>
    /// <remarks>
    /// Exists so the tool surface can be exercised against a device double (#465). Every tool
    /// past <see cref="Require"/> — which is all of the configuration, digital, PWM and rate
    /// logic — is otherwise reachable only with hardware attached, which is why the whole of
    /// this server's test coverage used to stop at "nothing is connected". Deliberately
    /// <c>internal</c>: the supported way in is <see cref="ConnectAsync"/>, which owns discovery
    /// lookup and the duplicate-device policy this bypasses.
    /// </remarks>
    internal void RegisterConnectedDevice(string deviceId, DaqifiDevice device) =>
        _registry.Register(device, deviceInfo: null, key: deviceId);

    private DaqifiDevice Require(string deviceId)
    {
        if (!_registry.TryGet(deviceId, out var registration))
        {
            throw new InvalidOperationException(
                $"Device '{deviceId}' is not connected. Call connect_device first.");
        }
        if (!registration!.Device.IsConnected)
        {
            // Evict by reference so only the exact stale instance we inspected is removed: a
            // concurrent reconnect may already have replaced it with a live one under the same id.
            _registry.Remove(registration.Device);
            throw new InvalidOperationException($"Device '{deviceId}' is no longer connected.");
        }
        return registration.Device;
    }

    // #333 promoted Channels/Metadata/GetChannelsSnapshot/ChannelsPopulated onto IStreamingDevice
    // and narrowed DaqifiDeviceFactory's return type, but this cast is a different one: it exists
    // because DaqifiDeviceRegistry (this._registry) is deliberately typed over the base DaqifiDevice
    // — its public Register(DaqifiDevice, ...) accepts any manually-constructed DaqifiDevice, not
    // only ones this factory built — so a registered handle's static type here is never narrower
    // than that. Every device this agent actually connects is in practice a DaqifiStreamingDevice,
    // so the runtime check below always succeeds; it stays a check rather than an assumption because
    // the registry's contract doesn't guarantee it.
    private (DaqifiDevice device, IStreamingDevice streaming) RequireStreaming(string deviceId)
    {
        var device = Require(deviceId);
        if (device is not IStreamingDevice streaming)
        {
            throw new InvalidOperationException(
                $"Device '{deviceId}' does not support streaming/configuration operations.");
        }
        return (device, streaming);
    }

    // Best-effort: a device that doesn't support the capability document (or a query that
    // fails/times out) just leaves CurrentMaximumRateHz stale or absent, and SetSampleRateAsync
    // falls back to the board-derived ceiling. Not fatal to the channel-configuration call that
    // triggered the refresh. Skipped outright while streaming/logging: ReadCapabilityDocumentAsync
    // runs a text-mode exchange that pauses the protobuf consumer, which Core documents as unsafe
    // to call while streaming (SD logging sets IsStreaming too) — leaving the cap stale here is
    // preferable to disrupting an active session.
    private async Task RefreshCapabilityDocumentAsync(DaqifiDevice device, IStreamingDevice streaming)
    {
        if (streaming.IsStreaming)
        {
            return;
        }

        try
        {
            await device.ReadCapabilityDocumentAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Capability-document refresh failed for '{DeviceId}'; set_sample_rate will keep using the last-known cap.", device.Metadata.SerialNumber);
        }
    }

    private static ISdCardOperations RequireSdCard(DaqifiDevice device)
    {
        if (device is not ISdCardOperations sd)
        {
            throw new InvalidOperationException("This device does not support SD-card operations.");
        }
        return sd;
    }

    private void RequireControl()
    {
        if (_options.ReadOnly)
        {
            throw new InvalidOperationException(
                "Server is running in --read-only mode; configuration and logging are disabled.");
        }
    }

    /// <summary>Lock-protected channel snapshot so callers never fold the live view while the consumer thread repopulates it.</summary>
    private static IReadOnlyList<IChannel> Snapshot(DaqifiDevice device) => device.GetChannelsSnapshot();

    private static IReadOnlyList<int> EnabledAnalog(DaqifiDevice device) => Snapshot(device)
        .Where(c => c.Type == ChannelType.Analog && c.IsEnabled)
        .Select(c => c.ChannelNumber)
        .OrderBy(n => n)
        .ToList();

    private static IReadOnlyList<int> EnabledDigital(DaqifiDevice device) => Snapshot(device)
        .Where(c => c.Type == ChannelType.Digital && c.IsEnabled)
        .Select(c => c.ChannelNumber)
        .OrderBy(n => n)
        .ToList();

    private static IChannel RequireDigitalChannel(DaqifiDevice device, int channelNumber)
    {
        var digital = Snapshot(device).Where(c => c.Type == ChannelType.Digital).ToList();
        var match = digital.FirstOrDefault(c => c.ChannelNumber == channelNumber);
        if (match is null)
        {
            throw new InvalidOperationException(
                $"Unknown digital channel {channelNumber}. This device has digital channels: " +
                $"{string.Join(", ", digital.Select(c => c.ChannelNumber).OrderBy(n => n))}.");
        }
        return match;
    }

    /// <summary>
    /// Fails fast with MCP-actionable guidance when PWM is enabled on <paramref name="channel"/>,
    /// rather than letting the call reach Core and surface its SDK-oriented
    /// <see cref="InvalidOperationException"/> message (which points at <c>SetPwmEnabled</c>, a
    /// method MCP callers have no tool for) — see #449.
    /// </summary>
    private static void RequirePwmDisabled(IChannel channel)
    {
        if (channel is IDigitalChannel { IsPwmEnabled: true })
        {
            throw new InvalidOperationException(
                $"Channel {channel.ChannelNumber} has PWM enabled; the firmware ignores digital direction/state " +
                "commands while PWM is running. Call disable_pwm on this channel first.");
        }
    }

    private static ChannelDirection ParseDirection(string? direction) => (direction ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "input" or "in" => ChannelDirection.Input,
        "output" or "out" => ChannelDirection.Output,
        _ => throw new InvalidOperationException($"Unknown direction '{direction}'. Use 'input' or 'output'."),
    };

    private static SdCardLogFormat ParseFormat(string? format) => (format ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "" or "protobuf" or "bin" or "binary" => SdCardLogFormat.Protobuf,
        "json" => SdCardLogFormat.Json,
        "csv" => SdCardLogFormat.Csv,
        _ => throw new InvalidOperationException($"Unknown format '{format}'. Use 'protobuf', 'json', or 'csv'."),
    };

    private static string MintId(IDeviceInfo info)
    {
        var key = info.ConnectionType switch
        {
            ConnectionType.Serial => info.PortName ?? info.SerialNumber,
            ConnectionType.WiFi => info.IPAddress?.ToString() ?? info.SerialNumber,
            _ => info.SerialNumber,
        };
        var connection = info.ConnectionType.ToString().ToLowerInvariant();
        return $"{connection}:{key}";
    }
}
