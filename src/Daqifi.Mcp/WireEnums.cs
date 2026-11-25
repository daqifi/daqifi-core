using System.Text.Json.Serialization;
using Daqifi.Core.Channel;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Discovery;
using Daqifi.Core.Device.SdCard;

namespace Daqifi.Mcp;

// Every closed set of strings this server puts on the wire, spelled out here and nowhere else.
// Core's enums are never serialized: these are separate types, each member carries its wire
// spelling explicitly, and WireEnums maps between the two. Renaming a Core member (or one of these)
// therefore cannot change what an AI client sees, and the SDK turns each type into a JSON-schema
// "enum" so a client knows the values without reading prose (issue #1042).

/// <summary>How a discovered device is reached.</summary>
public enum WireConnectionType
{
    /// <summary>A transport this server does not recognize.</summary>
    [JsonStringEnumMemberName("unknown")]
    Unknown,

    /// <summary>USB, as a serial port.</summary>
    [JsonStringEnumMemberName("serial")]
    Serial,

    /// <summary>WiFi.</summary>
    [JsonStringEnumMemberName("wifi")]
    Wifi,

    /// <summary>USB HID: a device sitting in its bootloader.</summary>
    [JsonStringEnumMemberName("hid")]
    Hid,
}

/// <summary>The state of a connection this server holds.</summary>
public enum WireConnectionStatus
{
    /// <summary>Not connected.</summary>
    [JsonStringEnumMemberName("disconnected")]
    Disconnected,

    /// <summary>A connection is being opened.</summary>
    [JsonStringEnumMemberName("connecting")]
    Connecting,

    /// <summary>Connected and usable.</summary>
    [JsonStringEnumMemberName("connected")]
    Connected,

    /// <summary>The connection dropped.</summary>
    [JsonStringEnumMemberName("lost")]
    Lost,

    /// <summary>The connection dropped and is being re-established.</summary>
    [JsonStringEnumMemberName("retrying")]
    Retrying,

    /// <summary>Connecting was given up on.</summary>
    [JsonStringEnumMemberName("failed")]
    Failed,
}

/// <summary>The board model.</summary>
public enum WireDeviceType
{
    /// <summary>The device did not report a part number this server recognizes.</summary>
    [JsonStringEnumMemberName("unknown")]
    Unknown,

    /// <summary>Nyquist 1.</summary>
    [JsonStringEnumMemberName("nyquist1")]
    Nyquist1,

    /// <summary>Nyquist 2.</summary>
    [JsonStringEnumMemberName("nyquist2")]
    Nyquist2,

    /// <summary>Nyquist 3.</summary>
    [JsonStringEnumMemberName("nyquist3")]
    Nyquist3,
}

/// <summary>What a channel is.</summary>
public enum WireChannelType
{
    /// <summary>An analog input.</summary>
    [JsonStringEnumMemberName("analog")]
    Analog,

    /// <summary>A digital input/output.</summary>
    [JsonStringEnumMemberName("digital")]
    Digital,

    /// <summary>An analog output (DAC).</summary>
    [JsonStringEnumMemberName("analog_output")]
    AnalogOutput,
}

/// <summary>The direction a channel is in.</summary>
public enum WireChannelDirection
{
    /// <summary>The direction is not known. Never sent; kept so the published tool schemas do not change.</summary>
    [JsonStringEnumMemberName("unknown")]
    Unknown,

    /// <summary>The channel is read.</summary>
    [JsonStringEnumMemberName("input")]
    Input,

    /// <summary>The channel is driven.</summary>
    [JsonStringEnumMemberName("output")]
    Output,
}

/// <summary>
/// The direction a caller may put a digital channel in: <see cref="WireChannelDirection"/> without
/// the value only a device can report.
/// </summary>
public enum WireDigitalDirection
{
    /// <summary>Read the pin.</summary>
    [JsonStringEnumMemberName("input")]
    Input,

    /// <summary>Drive the pin.</summary>
    [JsonStringEnumMemberName("output")]
    Output,
}

/// <summary>The format of an SD-card log.</summary>
public enum WireSdLogFormat
{
    /// <summary>The device's binary log.</summary>
    [JsonStringEnumMemberName("protobuf")]
    Protobuf,

    /// <summary>JSON lines.</summary>
    [JsonStringEnumMemberName("json")]
    Json,

    /// <summary>CSV.</summary>
    [JsonStringEnumMemberName("csv")]
    Csv,
}

/// <summary>How the check for a newer server release went.</summary>
public enum WireVersionCheck
{
    /// <summary>nuget.org answered.</summary>
    [JsonStringEnumMemberName("ok")]
    Ok,

    /// <summary>The server was started with <c>--no-version-check</c>.</summary>
    [JsonStringEnumMemberName("disabled")]
    Disabled,

    /// <summary>nuget.org could not be reached.</summary>
    [JsonStringEnumMemberName("unavailable")]
    Unavailable,
}

/// <summary>
/// Maps Core's enums to the wire's and back. A Core member with no arm here throws rather than
/// reaching a client under a made-up name; <c>WireEnumContractTests</c> walks every Core member, so
/// a new one fails there and never in front of a caller.
/// </summary>
internal static class WireEnums
{
    internal static WireConnectionType ToWire(this ConnectionType value) => value switch
    {
        ConnectionType.Unknown => WireConnectionType.Unknown,
        ConnectionType.Serial => WireConnectionType.Serial,
        ConnectionType.Wifi => WireConnectionType.Wifi,
        ConnectionType.Hid => WireConnectionType.Hid,
        _ => throw Unmapped(value),
    };

    // The wire says whether there is a connection, not how far its initialization has got: every
    // state with a session up is "connected".
    internal static WireConnectionStatus ToWire(this DeviceState value) => value switch
    {
        DeviceState.Disconnected => WireConnectionStatus.Disconnected,
        DeviceState.Connecting => WireConnectionStatus.Connecting,
        DeviceState.Connected => WireConnectionStatus.Connected,
        DeviceState.Initializing => WireConnectionStatus.Connected,
        DeviceState.Ready => WireConnectionStatus.Connected,
        DeviceState.InitializationFailed => WireConnectionStatus.Connected,
        DeviceState.Disconnecting => WireConnectionStatus.Connected,
        DeviceState.Lost => WireConnectionStatus.Lost,
        DeviceState.Retrying => WireConnectionStatus.Retrying,
        DeviceState.Failed => WireConnectionStatus.Failed,
        DeviceState.Disposed => WireConnectionStatus.Disconnected,
        _ => throw Unmapped(value),
    };

    internal static WireDeviceType ToWire(this DeviceType value) => value switch
    {
        DeviceType.Unknown => WireDeviceType.Unknown,
        DeviceType.Nyquist1 => WireDeviceType.Nyquist1,
        DeviceType.Nyquist2 => WireDeviceType.Nyquist2,
        DeviceType.Nyquist3 => WireDeviceType.Nyquist3,
        _ => throw Unmapped(value),
    };

    internal static WireChannelType ToWire(this ChannelType value) => value switch
    {
        ChannelType.Analog => WireChannelType.Analog,
        ChannelType.Digital => WireChannelType.Digital,
        ChannelType.AnalogOutput => WireChannelType.AnalogOutput,
        _ => throw Unmapped(value),
    };

    internal static WireChannelDirection ToWire(this ChannelDirection value) => value switch
    {
        ChannelDirection.Input => WireChannelDirection.Input,
        ChannelDirection.Output => WireChannelDirection.Output,
        _ => throw Unmapped(value),
    };

    internal static WireSdLogFormat ToWire(this SdCardLogFormat value) => value switch
    {
        SdCardLogFormat.Protobuf => WireSdLogFormat.Protobuf,
        SdCardLogFormat.Json => WireSdLogFormat.Json,
        SdCardLogFormat.Csv => WireSdLogFormat.Csv,
        _ => throw Unmapped(value),
    };

    internal static ChannelDirection ToCore(this WireDigitalDirection value) => value switch
    {
        WireDigitalDirection.Input => ChannelDirection.Input,
        WireDigitalDirection.Output => ChannelDirection.Output,
        _ => throw Unmapped(value),
    };

    internal static SdCardLogFormat ToCore(this WireSdLogFormat value) => value switch
    {
        WireSdLogFormat.Protobuf => SdCardLogFormat.Protobuf,
        WireSdLogFormat.Json => SdCardLogFormat.Json,
        WireSdLogFormat.Csv => SdCardLogFormat.Csv,
        _ => throw Unmapped(value),
    };

    private static InvalidOperationException Unmapped<T>(T value)
        where T : struct, Enum =>
        new($"This server has no wire name for {typeof(T).Name} value '{value}'.");
}
