using Daqifi.Core.Channel;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Discovery;
using Daqifi.Core.Device.SdCard;

namespace Daqifi.Mcp;

/// <summary>A device seen during discovery. <see cref="DeviceId"/> is the handle used by other tools.</summary>
public sealed record DiscoveredDevice(
    string DeviceId,
    string Name,
    string ConnectionType,
    string SerialNumber,
    string FirmwareVersion,
    string? Address,
    string DeviceType)
{
    public static DiscoveredDevice From(string id, IDeviceInfo info) => new(
        id,
        info.Name,
        info.ConnectionType.ToString(),
        info.SerialNumber,
        info.FirmwareVersion,
        info.ConnectionType == Daqifi.Core.Device.Discovery.ConnectionType.Serial ? info.PortName : info.IPAddress?.ToString(),
        info.Type.ToString());
}

/// <summary>Summary of a currently-connected device.</summary>
public sealed record ConnectedDeviceInfo(
    string DeviceId,
    string Name,
    bool Connected,
    int AnalogChannelCount,
    int DigitalChannelCount)
{
    public static ConnectedDeviceInfo From(string id, DaqifiDevice device)
    {
        var analog = 0;
        var digital = 0;
        foreach (var ch in device.GetChannelsSnapshot())
        {
            if (ch.Type == ChannelType.Analog) analog++;
            else if (ch.Type == ChannelType.Digital) digital++;
        }
        return new ConnectedDeviceInfo(id, device.Name, device.IsConnected, analog, digital);
    }
}

/// <summary>Live status snapshot for a connected device.</summary>
public sealed record DeviceStatus(
    string DeviceId,
    string Name,
    string ConnectionStatus,
    bool Streaming,
    bool LoggingToSdCard,
    int SampleRateHz,
    IReadOnlyList<int> EnabledAnalogChannels,
    IReadOnlyList<int> EnabledDigitalChannels)
{
    public static DeviceStatus From(string id, DaqifiDevice device)
    {
        var streaming = (device as IStreamingDevice)?.IsStreaming ?? false;
        var rate = (device as IStreamingDevice)?.StreamingFrequency ?? 0;
        var logging = (device as ISdCardOperations)?.IsLoggingToSdCard ?? false;
        var channels = device.GetChannelsSnapshot();
        var enabledAnalog = channels
            .Where(c => c.Type == ChannelType.Analog && c.IsEnabled)
            .Select(c => c.ChannelNumber)
            .OrderBy(n => n)
            .ToList();
        var enabledDigital = channels
            .Where(c => c.Type == ChannelType.Digital && c.IsEnabled)
            .Select(c => c.ChannelNumber)
            .OrderBy(n => n)
            .ToList();
        return new DeviceStatus(
            id, device.Name, device.Status.ToString(), streaming, logging, rate, enabledAnalog, enabledDigital);
    }
}

/// <summary>
/// A single channel on a device. <see cref="OutputValue"/> is the last commanded output level
/// for digital channels; <see cref="PwmCapable"/>/<see cref="PwmEnabled"/> report PWM hardware
/// support and the last commanded PWM state (all three are null for analog channels).
/// </summary>
public sealed record ChannelInfo(int ChannelNumber, string Type, string Name, bool Enabled, string Direction, bool? OutputValue, bool? PwmCapable, bool? PwmEnabled)
{
    public static ChannelInfo From(IChannel ch)
    {
        var digital = ch as IDigitalChannel;
        return new(
            ch.ChannelNumber,
            ch.Type.ToString(),
            ch.Name,
            ch.IsEnabled,
            ch.Direction.ToString(),
            digital?.OutputValue,
            digital?.IsPwmCapable,
            digital?.IsPwmEnabled);
    }
}

/// <summary>
/// Result of a channel-configuration change. <see cref="SampleRateAdjustedFromHz"/> is non-null
/// only when the newly-enabled channel set lowered the device's rate cap below the sample rate
/// that was already live, in which case <see cref="SampleRateHz"/> has already been brought down
/// to the new cap — see <see cref="DaqifiAgent.EnforceSampleRateCap"/> (#447). It is <c>null</c>
/// when the rate needed no adjustment, including when nothing was ever set.
/// </summary>
public sealed record ConfigureResult(
    string DeviceId,
    IReadOnlyList<int> EnabledAnalogChannels,
    int SampleRateHz,
    int? SampleRateAdjustedFromHz);

/// <summary>
/// Result of a digital channel-configuration change. <see cref="SampleRateHz"/> and
/// <see cref="SampleRateAdjustedFromHz"/> mirror <see cref="ConfigureResult"/> — digital
/// reconfiguration also refreshes the device's rate cap, so the same live-rate re-validation
/// applies (#447).
/// </summary>
public sealed record ConfigureDigitalResult(
    string DeviceId,
    IReadOnlyList<int> EnabledDigitalChannels,
    int SampleRateHz,
    int? SampleRateAdjustedFromHz);

/// <summary>
/// Result of a digital direction or output change, reflecting the channel's state after the
/// operation. <see cref="OutputValue"/> is the last commanded output level; it is meaningful
/// only while <see cref="Direction"/> is Output.
/// </summary>
public sealed record DigitalPinResult(string DeviceId, int Channel, string Direction, bool OutputValue)
{
    public static DigitalPinResult From(string deviceId, IChannel ch) => new(
        deviceId,
        ch.ChannelNumber,
        ch.Direction.ToString(),
        ch is IDigitalChannel digital && digital.OutputValue);
}

/// <summary>
/// Result of a PWM operation, reflecting the channel's state after it. <see cref="DutyCyclePercent"/>
/// and <see cref="FrequencyHz"/> are <c>null</c> until a value has actually been commanded this
/// session — Core seeds both with session defaults (not device state) that are indistinguishable
/// from a real command, so a caller cannot otherwise tell "this is what the device is doing" from
/// "this is a constant Core made up" (#450). <see cref="FrequencyHz"/> is device-wide; all PWM
/// channels share one frequency.
/// </summary>
public sealed record PwmResult(string DeviceId, int Channel, bool Enabled, int? DutyCyclePercent, int? FrequencyHz)
{
    public static PwmResult From(string deviceId, IStreamingDevice device, IChannel ch, bool dutyCommanded, bool frequencyCommanded)
    {
        var digital = ch as IDigitalChannel;
        return new(
            deviceId,
            ch.ChannelNumber,
            digital?.IsPwmEnabled ?? false,
            dutyCommanded ? digital?.PwmDutyCyclePercent : null,
            frequencyCommanded ? device.PwmFrequencyHz : null);
    }
}

/// <summary>
/// Result of a sample-rate change. A request exceeding the effective ceiling (the device's cap
/// for its currently enabled channels, or a lower <c>--max-sample-rate-hz</c>) is rejected
/// outright rather than clamped — see <see cref="DaqifiAgent.SetSampleRateAsync"/>.
/// </summary>
public sealed record SampleRateResult(string DeviceId, int RequestedRateHz);

/// <summary>Result of starting SD-card logging.</summary>
public sealed record StartLoggingResult(
    string DeviceId,
    string FileName,
    string Format,
    int SampleRateHz,
    IReadOnlyList<int> EnabledAnalogChannels);
