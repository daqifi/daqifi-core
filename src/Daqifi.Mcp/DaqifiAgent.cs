using System.Collections.Concurrent;
using Daqifi.Core.Channel;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Discovery;
using Daqifi.Core.Device.SdCard;

namespace Daqifi.Mcp;

/// <summary>
/// Agent-facing facade over <c>Daqifi.Core</c>. Owns device discovery results and the set of
/// connected devices, and translates the high-level tool surface into the real SDK calls
/// (static <see cref="DaqifiDeviceFactory"/>, <see cref="IStreamingDevice"/> channel APIs,
/// <see cref="ISdCardOperations"/>). One instance is shared by all tool calls.
/// </summary>
/// <remarks>
/// The MCP transport may dispatch tool calls concurrently, so every operation that connects,
/// disconnects, or mutates device state is serialized behind <see cref="_gate"/>. Read-only
/// introspection snapshots the channel collection instead, so it never blocks and never folds the
/// live <c>Channels</c> view while the device's consumer thread repopulates it.
/// </remarks>
public sealed class DaqifiAgent
{
    /// <summary>The maximum sample rate the Nyquist hardware accepts (SCPI range is 1–1000 Hz).</summary>
    public const int HardwareMaxSampleRateHz = 1000;

    private readonly ServerOptions _options;
    private readonly ConcurrentDictionary<string, IDeviceInfo> _discovered = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DaqifiDevice> _connected = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DaqifiAgent(ServerOptions options) => _options = options;

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

    public async Task<ConnectedDeviceInfo> ConnectAsync(string deviceId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connected.TryGetValue(deviceId, out var already))
            {
                if (already.IsConnected)
                {
                    return ConnectedDeviceInfo.From(deviceId, already);
                }

                // Stale handle (device dropped); discard it and reconnect.
                _connected.TryRemove(new KeyValuePair<string, DaqifiDevice>(deviceId, already));
                already.Dispose();
            }

            if (!_discovered.TryGetValue(deviceId, out var info))
            {
                throw new InvalidOperationException(
                    $"Unknown device_id '{deviceId}'. Call discover_devices first and use a device_id from its result.");
            }

            var device = await DaqifiDeviceFactory
                .ConnectFromDeviceInfoAsync(info, null, cancellationToken)
                .ConfigureAwait(false);

            _connected[deviceId] = device;
            return ConnectedDeviceInfo.From(deviceId, device);
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
            if (_connected.TryRemove(deviceId, out var device))
            {
                try { device.Disconnect(); } catch { /* best effort */ }
                device.Dispose();
                return $"Disconnected '{deviceId}'.";
            }
            return $"Device '{deviceId}' was not connected.";
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<ConnectedDeviceInfo> ListConnected() =>
        _connected.Select(kvp => ConnectedDeviceInfo.From(kvp.Key, kvp.Value)).ToList();

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
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            RequireControl();
            var (device, streaming) = RequireStreaming(deviceId);

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

            return new ConfigureResult(deviceId, EnabledAnalog(device), streaming.StreamingFrequency);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Enables exactly the requested digital channels (by channel number) and disables the rest.
    /// The wire-level DIO enable is global — enabling any digital channel turns the whole port
    /// on — but per-channel enablement determines which channels the streaming decode samples.
    /// </summary>
    public async Task<ConfigureDigitalResult> ConfigureDigitalChannelsAsync(string deviceId, int[] enabledChannels)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            RequireControl();
            var (device, streaming) = RequireStreaming(deviceId);

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

            return new ConfigureDigitalResult(deviceId, EnabledDigital(device));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Sets a digital channel's direction (input or output) via
    /// <see cref="IStreamingDevice.SetDioDirection"/>.
    /// </summary>
    public async Task<DigitalPinResult> SetDigitalDirectionAsync(string deviceId, int channel, string direction)
    {
        var parsed = ParseDirection(direction);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            RequireControl();
            var (device, streaming) = RequireStreaming(deviceId);
            var ch = RequireDigitalChannel(device, channel);

            streaming.SetDioDirection(ch, parsed);

            return DigitalPinResult.From(deviceId, ch);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Drives a digital output channel high or low. A channel still in input direction is
    /// switched to output first, so a single call is enough to drive a pin.
    /// </summary>
    public async Task<DigitalPinResult> SetDigitalOutputAsync(string deviceId, int channel, bool high)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            RequireControl();
            var (device, streaming) = RequireStreaming(deviceId);
            var ch = RequireDigitalChannel(device, channel);

            if (ch.Direction != ChannelDirection.Output)
            {
                streaming.SetDioDirection(ch, ChannelDirection.Output);
            }

            streaming.SetDioValue(ch, high);

            return DigitalPinResult.From(deviceId, ch);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Configures and starts PWM output on a PWM-capable digital channel: duty first, then the
    /// shared frequency when supplied, then enable. The device's PWM frequency is global (one
    /// hardware timer drives all PWM channels).
    /// </summary>
    public async Task<PwmResult> SetPwmOutputAsync(string deviceId, int channel, int dutyCyclePercent, int frequencyHz)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            RequireControl();
            var (device, streaming) = RequireStreaming(deviceId);
            var ch = RequireDigitalChannel(device, channel);

            if (frequencyHz == 0 && streaming.PwmFrequencyHz == 0)
            {
                throw new InvalidOperationException(
                    "No PWM frequency has been set this session. Pass frequency_hz (6-50000). " +
                    "The frequency is shared by all PWM channels.");
            }

            // Duty before frequency before enable: the firmware applies a stored duty when the
            // frequency is (re)programmed, so this order never leaves a stale compare value.
            streaming.SetPwmDutyCycle(ch, dutyCyclePercent);
            if (frequencyHz != 0)
            {
                streaming.SetPwmFrequency(frequencyHz);
            }
            streaming.SetPwmEnabled(ch, true);

            return PwmResult.From(deviceId, streaming, ch);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stops PWM output on a digital channel. The pin is left high-impedance; use
    /// set_digital_direction / set_digital_output to drive it digitally again.
    /// </summary>
    public async Task<PwmResult> DisablePwmAsync(string deviceId, int channel)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            RequireControl();
            var (device, streaming) = RequireStreaming(deviceId);
            var ch = RequireDigitalChannel(device, channel);

            streaming.SetPwmEnabled(ch, false);

            return PwmResult.From(deviceId, streaming, ch);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SampleRateResult> SetSampleRateAsync(string deviceId, int rateHz)
    {
        if (rateHz < 1)
        {
            throw new InvalidOperationException("rate_hz must be >= 1.");
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            RequireControl();
            var (_, streaming) = RequireStreaming(deviceId);

            // The hardware ceiling (1000 Hz) always applies; --max-sample-rate-hz can only lower it.
            // Guard against a non-positive cap so the applied rate is always a valid >= 1 value.
            var cap = _options.MaxSampleRateHz is { } max
                ? Math.Clamp(max, 1, HardwareMaxSampleRateHz)
                : HardwareMaxSampleRateHz;

            var applied = Math.Min(rateHz, cap);
            streaming.StreamingFrequency = applied;

            var note = applied != rateHz
                ? $"Requested {rateHz} Hz clamped to {applied} Hz (maximum {cap} Hz)."
                : null;
            return new SampleRateResult(deviceId, rateHz, applied, applied != rateHz, note);
        }
        finally
        {
            _gate.Release();
        }
    }

    // --------------------------------------------------------- SD card logging

    public async Task<StartLoggingResult> StartLoggingAsync(
        string deviceId, string? fileName, string format, CancellationToken cancellationToken)
    {
        var fmt = ParseFormat(format);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireControl();
            var (device, streaming) = RequireStreaming(deviceId);
            var sd = RequireSdCard(device);

            // Generate the name in this layer so the result reports the real on-card filename.
            // Core honors a non-empty fileName verbatim; channels use the device's current config.
            var effectiveName = string.IsNullOrWhiteSpace(fileName)
                ? $"log_{DateTime.Now:yyyyMMdd_HHmmss}{ExtensionFor(fmt)}"
                : fileName!;

            await sd.StartSdCardLoggingAsync(effectiveName, channelMask: null, format: fmt, cancellationToken)
                .ConfigureAwait(false);

            return new StartLoggingResult(
                deviceId, effectiveName, fmt.ToString(), streaming.StreamingFrequency, EnabledAnalog(device));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> StopLoggingAsync(string deviceId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireControl();
            var (device, _) = RequireStreaming(deviceId);
            var sd = RequireSdCard(device);
            await sd.StopSdCardLoggingAsync(cancellationToken).ConfigureAwait(false);
            return $"Stopped SD-card logging on '{deviceId}'.";
        }
        finally
        {
            _gate.Release();
        }
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
            foreach (var device in _connected.Values)
            {
                try
                {
                    if (device is ISdCardOperations { IsLoggingToSdCard: true } sd)
                    {
                        await sd.StopSdCardLoggingAsync().ConfigureAwait(false);
                    }
                }
                catch { /* best effort */ }

                try { device.Disconnect(); } catch { /* best effort */ }
                device.Dispose();
            }
            _connected.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------------ helpers

    private DaqifiDevice Require(string deviceId)
    {
        if (!_connected.TryGetValue(deviceId, out var device))
        {
            throw new InvalidOperationException(
                $"Device '{deviceId}' is not connected. Call connect_device first.");
        }
        if (!device.IsConnected)
        {
            // Only evict the exact stale instance we inspected (a concurrent reconnect may have
            // already replaced it with a live one under the same id).
            _connected.TryRemove(new KeyValuePair<string, DaqifiDevice>(deviceId, device));
            throw new InvalidOperationException($"Device '{deviceId}' is no longer connected.");
        }
        return device;
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

    private static string ExtensionFor(SdCardLogFormat format) => format switch
    {
        SdCardLogFormat.Json => ".json",
        SdCardLogFormat.Csv => ".csv",
        _ => ".bin",
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
