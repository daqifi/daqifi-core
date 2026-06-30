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
    IReadOnlyList<int> EnabledAnalogChannels)
{
    public static DeviceStatus From(string id, DaqifiDevice device)
    {
        var streaming = (device as IStreamingDevice)?.IsStreaming ?? false;
        var rate = (device as IStreamingDevice)?.StreamingFrequency ?? 0;
        var logging = (device as ISdCardOperations)?.IsLoggingToSdCard ?? false;
        var enabled = device.GetChannelsSnapshot()
            .Where(c => c.Type == ChannelType.Analog && c.IsEnabled)
            .Select(c => c.ChannelNumber)
            .OrderBy(n => n)
            .ToList();
        return new DeviceStatus(id, device.Name, device.Status.ToString(), streaming, logging, rate, enabled);
    }
}

/// <summary>A single channel on a device.</summary>
public sealed record ChannelInfo(int ChannelNumber, string Type, string Name, bool Enabled, string Direction)
{
    public static ChannelInfo From(IChannel ch) =>
        new(ch.ChannelNumber, ch.Type.ToString(), ch.Name, ch.IsEnabled, ch.Direction.ToString());
}

/// <summary>Result of a channel-configuration change.</summary>
public sealed record ConfigureResult(string DeviceId, IReadOnlyList<int> EnabledAnalogChannels, int SampleRateHz);

/// <summary>
/// Result of a sample-rate change. <see cref="Clamped"/> is true when <see cref="RequestedRateHz"/>
/// exceeded the effective ceiling (1000 Hz hardware limit, or a lower <c>--max-sample-rate-hz</c>),
/// in which case <see cref="Note"/> explains the adjustment.
/// </summary>
public sealed record SampleRateResult(string DeviceId, int RequestedRateHz, int AppliedRateHz, bool Clamped, string? Note);

/// <summary>Result of starting SD-card logging.</summary>
public sealed record StartLoggingResult(
    string DeviceId,
    string FileName,
    string Format,
    int SampleRateHz,
    IReadOnlyList<int> EnabledAnalogChannels);
