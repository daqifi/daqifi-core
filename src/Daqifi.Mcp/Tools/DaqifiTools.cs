using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Daqifi.Mcp.Tools;

/// <summary>
/// The MCP tool surface for controlling a DAQiFi device. Each tool is a thin wrapper over
/// <see cref="DaqifiAgent"/>; the agent is supplied by dependency injection and is not part of
/// the tool's input schema. Validation/runtime failures are surfaced to the agent as
/// <see cref="McpException"/> so the human-readable message (e.g. "call connect_device first")
/// reaches the model instead of a generic error.
/// </summary>
[McpServerToolType]
public static class DaqifiTools
{
    [McpServerTool(Name = "discover_devices")]
    [Description("Discover DAQiFi devices on USB/serial and WiFi. Returns a list whose device_id values are used by the other tools. Call this first.")]
    public static Task<IReadOnlyList<DiscoveredDevice>> DiscoverDevices(
        DaqifiAgent agent,
        [Description("Discovery timeout in milliseconds (default 2000; clamped to 250..30000).")] int timeoutMs = 2000,
        [Description("Include WiFi/network discovery (default true).")] bool wifi = true,
        [Description("Include USB/serial discovery (default true).")] bool serial = true,
        CancellationToken cancellationToken = default)
        => GuardAsync(() => agent.DiscoverAsync(timeoutMs, wifi, serial, cancellationToken));

    [McpServerTool(Name = "connect_device")]
    [Description("Connect to a previously-discovered device. Pass a device_id from discover_devices. Channels are populated on connect.")]
    public static Task<ConnectedDeviceInfo> ConnectDevice(
        DaqifiAgent agent,
        [Description("The device_id from discover_devices.")] string deviceId,
        CancellationToken cancellationToken = default)
        => GuardAsync(() => agent.ConnectAsync(deviceId, cancellationToken));

    [McpServerTool(Name = "disconnect_device")]
    [Description("Disconnect from a connected device and release it.")]
    public static Task<string> DisconnectDevice(
        DaqifiAgent agent,
        [Description("The device_id to disconnect.")] string deviceId)
        => GuardAsync(() => agent.DisconnectAsync(deviceId));

    [McpServerTool(Name = "list_connected_devices")]
    [Description("List the devices currently connected to this server. Cheap; safe to call often.")]
    public static IReadOnlyList<ConnectedDeviceInfo> ListConnectedDevices(DaqifiAgent agent)
        => Guard(agent.ListConnected);

    [McpServerTool(Name = "get_device_status")]
    [Description("Get a live status snapshot for a connected device: connection state, streaming/logging flags, sample rate, and enabled analog channels.")]
    public static DeviceStatus GetDeviceStatus(
        DaqifiAgent agent,
        [Description("The device_id to inspect.")] string deviceId)
        => Guard(() => agent.GetStatus(deviceId));

    [McpServerTool(Name = "list_channels")]
    [Description("List all channels on a connected device with their type, enabled state, and direction.")]
    public static IReadOnlyList<ChannelInfo> ListChannels(
        DaqifiAgent agent,
        [Description("The device_id to inspect.")] string deviceId)
        => Guard(() => agent.ListChannels(deviceId));

    [McpServerTool(Name = "configure_analog_channels")]
    [Description("Enable exactly the given analog input channels (by channel number) and disable the rest. Pass an empty list to disable all analog channels.")]
    public static Task<ConfigureResult> ConfigureAnalogChannels(
        DaqifiAgent agent,
        [Description("The device_id to configure.")] string deviceId,
        [Description("Analog channel numbers to enable, e.g. [0,1,2,3]. Channels not listed are disabled.")] int[] enabledChannels)
        => GuardAsync(() => agent.ConfigureAnalogChannelsAsync(deviceId, enabledChannels));

    [McpServerTool(Name = "configure_digital_channels")]
    [Description("Enable exactly the given digital channels (by channel number) and disable the rest. Enabled digital channels are sampled during streaming; the device's DIO enable is global, so enabling any digital channel powers the whole port. Pass an empty list to disable all digital channels.")]
    public static Task<ConfigureDigitalResult> ConfigureDigitalChannels(
        DaqifiAgent agent,
        [Description("The device_id to configure.")] string deviceId,
        [Description("Digital channel numbers to enable, e.g. [0,1,2]. Channels not listed are disabled.")] int[] enabledChannels)
        => GuardAsync(() => agent.ConfigureDigitalChannelsAsync(deviceId, enabledChannels));

    [McpServerTool(Name = "set_digital_direction")]
    [Description("Set a digital channel's direction: 'input' (high-impedance, sampled during streaming) or 'output' (driven by the device; set the level with set_digital_output).")]
    public static Task<DigitalPinResult> SetDigitalDirection(
        DaqifiAgent agent,
        [Description("The device_id to configure.")] string deviceId,
        [Description("The digital channel number (e.g. 0-15 on Nyquist).")] int channel,
        [Description("'input' or 'output'.")] string direction)
        => GuardAsync(() => agent.SetDigitalDirectionAsync(deviceId, channel, direction));

    [McpServerTool(Name = "set_digital_output")]
    [Description("Drive a digital channel high or low. If the channel is currently an input it is switched to output direction first, so one call is enough to drive a pin.")]
    public static Task<DigitalPinResult> SetDigitalOutput(
        DaqifiAgent agent,
        [Description("The device_id to control.")] string deviceId,
        [Description("The digital channel number (e.g. 0-15 on Nyquist).")] int channel,
        [Description("true to drive the pin high, false to drive it low.")] bool high)
        => GuardAsync(() => agent.SetDigitalOutputAsync(deviceId, channel, high));

    [McpServerTool(Name = "set_sample_rate")]
    [Description("Set the device sample (streaming) rate in Hz, applied to streaming and SD-card logging. Nyquist hardware supports 1–1000 Hz; requests above 1000 Hz (or above --max-sample-rate-hz) are clamped and reported in the result.")]
    public static Task<SampleRateResult> SetSampleRate(
        DaqifiAgent agent,
        [Description("The device_id to configure.")] string deviceId,
        [Description("Sample rate in Hz (1–1000).")] int rateHz)
        => GuardAsync(() => agent.SetSampleRateAsync(deviceId, rateHz));

    [McpServerTool(Name = "start_sd_logging")]
    [Description("Start on-device SD-card logging using the currently enabled channels and sample rate. Requires a USB/serial connection (the SD card and WiFi share a bus). Configure channels and sample rate first.")]
    public static Task<StartLoggingResult> StartSdLogging(
        DaqifiAgent agent,
        [Description("The device_id to log on.")] string deviceId,
        [Description("Optional log file name. If omitted, the device auto-generates log_<timestamp>.")] string? fileName = null,
        [Description("Log format: 'protobuf' (default), 'json', or 'csv'.")] string format = "protobuf",
        CancellationToken cancellationToken = default)
        => GuardAsync(() => agent.StartLoggingAsync(deviceId, fileName, format, cancellationToken));

    [McpServerTool(Name = "stop_sd_logging")]
    [Description("Stop on-device SD-card logging on a device.")]
    public static Task<string> StopSdLogging(
        DaqifiAgent agent,
        [Description("The device_id to stop logging on.")] string deviceId,
        CancellationToken cancellationToken = default)
        => GuardAsync(() => agent.StopLoggingAsync(deviceId, cancellationToken));

    // Surface real exception messages (validation + Core errors) to the agent rather than a
    // generic "An error occurred". Cancellation is allowed to propagate untouched.
    private static T Guard<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (McpException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new McpException(ex.Message);
        }
    }

    private static async Task<T> GuardAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (McpException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new McpException(ex.Message);
        }
    }
}
