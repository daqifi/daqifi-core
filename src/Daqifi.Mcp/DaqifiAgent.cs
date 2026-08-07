using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Daqifi.Core.Channel;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Discovery;
using Daqifi.Core.Device.SdCard;
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

    public DaqifiAgent(ServerOptions options, ILogger<DaqifiAgent>? logger = null)
    {
        _options = options;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DaqifiAgent>.Instance;
    }

    // ---------------------------------------------------------------- discovery

    public async Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(
        int timeoutMs, bool wifi, bool serial, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 250, 30_000));
        var infos = new List<IDeviceInfo>();

        if (wifi)
        {
            using var finder = new WiFiDeviceFinder();
            infos.AddRange(await finder.DiscoverAsync(timeout).ConfigureAwait(false));
        }

        if (serial)
        {
            using var finder = new SerialDeviceFinder();
            infos.AddRange(await finder.DiscoverAsync(timeout).ConfigureAwait(false));
        }

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

            return new ConfigureResult(deviceId, EnabledAnalog(device), streaming.StreamingFrequency);
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

            return new ConfigureDigitalResult(deviceId, EnabledDigital(device));
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

        return await device.RunExclusiveAsync(_ =>
        {
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
        // between them could flip the pin back to input before the value is driven.
        return await device.RunExclusiveAsync(_ =>
        {
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
            // MaxSamplingRate is the absolute sampling-ISR ceiling, not what the device will
            // actually accept for the channels enabled right now — that is
            // CapabilityStreaming.CurrentMaximumRateHz, refreshed after every channel-
            // configuration call (see ConfigureAnalogChannelsAsync/ConfigureDigitalChannelsAsync).
            // Guard against a non-positive board-table value so the fallback is always >= 1; a
            // reported CurrentMaximumRateHz of 0 is a real answer ("no channels enabled") and is
            // deliberately not floored the same way.
            var hardwareMax = Math.Max(1, device.Metadata.Capabilities.MaxSamplingRate);

            // Bound to hardwareMax defensively: CurrentMaximumRateHz and MaxSamplingRate come from
            // two independently-parsed fields, so a self-inconsistent document (or a channel-set
            // read that raced a board-table update) could otherwise report a "current" cap above
            // the absolute ceiling StreamingFrequency itself enforces — which would let this check
            // pass and then fail one line down with the wrong exception type.
            var currentMax = device.Metadata.CapabilityDocument?.Streaming?.CurrentMaximumRateHz;
            var deviceCap = currentMax.HasValue ? Math.Min(currentMax.Value, hardwareMax) : hardwareMax;
            var cap = _options.MaxSampleRateHz is { } max ? Math.Min(max, deviceCap) : deviceCap;

            if (rateHz > cap)
            {
                throw new InvalidOperationException(
                    $"Requested {rateHz} Hz exceeds the maximum {cap} Hz for the currently enabled channels.");
            }

            streaming.StreamingFrequency = rateHz;
            return Task.FromResult(new SampleRateResult(deviceId, rateHz));
        }).ConfigureAwait(false);
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
