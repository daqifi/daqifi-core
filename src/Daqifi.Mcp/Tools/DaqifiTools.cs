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
    [McpServerTool(Name = "get_server_info")]
    [Description("Report this server's version and whether a newer Daqifi.Mcp is on NuGet. Call it when a tool you expected is missing — an out-of-date server lacks capabilities, not the hardware.")]
    public static Task<ServerVersionInfo> GetServerInfo(VersionStatus versionStatus)
        => GuardAsync(versionStatus.GetAsync);

    [McpServerTool(Name = "discover_devices")]
    [Description("Discover DAQiFi devices on USB/serial and WiFi. Returns device_id values used by the other tools; call this first.")]
    public static Task<IReadOnlyList<DiscoveredDevice>> DiscoverDevices(
        DaqifiAgent agent,
        [Description("Discovery timeout in milliseconds (default 2000; clamped to 1000..30000). Below the floor can return an empty list before a device could answer.")] int timeoutMs = 2000,
        [Description("Include WiFi/network discovery (default true).")] bool wifi = true,
        [Description("Include USB/serial discovery (default true).")] bool serial = true,
        CancellationToken cancellationToken = default)
        => GuardAsync(() => agent.DiscoverAsync(timeoutMs, wifi, serial, cancellationToken));

    [McpServerTool(Name = "connect_device")]
    [Description("Connect to a previously-discovered device by device_id. If that physical device is already connected, the existing connection is returned — use the device_id from the result.")]
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
    [Description("Get a live status snapshot for a connected device: connection state, streaming/logging flags, sample rate, and enabled analog and digital channels.")]
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
    [Description("Enable exactly the given analog input channels and disable the rest. Pass an empty list to disable all. Widening the set can lower the sample-rate ceiling; if the current rate no longer fits it is lowered and sampleRateAdjustedFromHz reports the old rate.")]
    public static Task<ConfigureResult> ConfigureAnalogChannels(
        DaqifiAgent agent,
        [Description("The device_id to configure.")] string deviceId,
        [Description("Analog channel numbers to enable, e.g. [0,1,2,3]. Channels not listed are disabled.")] int[] enabledChannels)
        => GuardAsync(() => agent.ConfigureAnalogChannelsAsync(deviceId, enabledChannels));

    [McpServerTool(Name = "configure_digital_channels")]
    [Description("Enable exactly the given digital channels and disable the rest. Enabling any digital channel powers the whole port; pass an empty list to disable all.")]
    public static Task<ConfigureDigitalResult> ConfigureDigitalChannels(
        DaqifiAgent agent,
        [Description("The device_id to configure.")] string deviceId,
        [Description("Digital channel numbers to enable, e.g. [0,1,2]. Channels not listed are disabled.")] int[] enabledChannels)
        => GuardAsync(() => agent.ConfigureDigitalChannelsAsync(deviceId, enabledChannels));

    [McpServerTool(Name = "set_digital_direction")]
    [Description("Set a digital channel's direction: 'input' or 'output'. Rejected while PWM is enabled on the channel — call disable_pwm first.")]
    public static Task<DigitalPinResult> SetDigitalDirection(
        DaqifiAgent agent,
        [Description("The device_id to configure.")] string deviceId,
        [Description("The digital channel number (e.g. 0-15 on Nyquist).")] int channel,
        [Description("'input' or 'output'.")] string direction)
        => GuardAsync(() => agent.SetDigitalDirectionAsync(deviceId, channel, direction));

    [McpServerTool(Name = "set_digital_output")]
    [Description("Drive a digital channel high or low, switching it to output if needed. Rejected while PWM is enabled on the channel — call disable_pwm first.")]
    public static Task<DigitalPinResult> SetDigitalOutput(
        DaqifiAgent agent,
        [Description("The device_id to control.")] string deviceId,
        [Description("The digital channel number (e.g. 0-15 on Nyquist).")] int channel,
        [Description("true to drive the pin high, false to drive it low.")] bool high)
        => GuardAsync(() => agent.SetDigitalOutputAsync(deviceId, channel, high));

    [McpServerTool(Name = "set_pwm_output")]
    [Description("Start PWM on a PWM-capable digital channel (Nyquist: 0, 3, 4, 5, 6, 7). Frequency is shared by all PWM channels; digital writes on the channel are rejected until disable_pwm.")]
    public static Task<PwmResult> SetPwmOutput(
        DaqifiAgent agent,
        [Description("The device_id to control.")] string deviceId,
        [Description("The PWM-capable digital channel number.")] int channel,
        [Description("Duty cycle in whole percent, 1-100. To stop the output use disable_pwm, not duty 0.")] int dutyCyclePercent,
        [Description("PWM frequency in Hz, 6-50000, applied device-wide. Pass 0 to keep the current session frequency (defaults to 1000 Hz until explicitly set).")] int frequencyHz = 0)
        => GuardAsync(() => agent.SetPwmOutputAsync(deviceId, channel, dutyCyclePercent, frequencyHz));

    [McpServerTool(Name = "disable_pwm")]
    [Description("Stop PWM on a digital channel and leave the pin high-impedance. Allowed on any digital channel (recovery for a half-armed firmware state); firmware rejection of an unarmed channel is not surfaced.")]
    public static Task<PwmResult> DisablePwm(
        DaqifiAgent agent,
        [Description("The device_id to control.")] string deviceId,
        [Description("The digital channel number to stop PWM on.")] int channel)
        => GuardAsync(() => agent.DisablePwmAsync(deviceId, channel));

    [McpServerTool(Name = "list_analog_outputs")]
    [Description("List analog output (DAC) channels with range, resolution, and the volts this server last wrote or read. An empty list means none are modelled — not necessarily that writes are impossible; a null volts is untouched this session, not 0 V.")]
    public static IReadOnlyList<AnalogOutputState> ListAnalogOutputs(
        DaqifiAgent agent,
        [Description("The device_id to inspect.")] string deviceId)
        => Guard(() => agent.ListAnalogOutputs(deviceId));

    [McpServerTool(Name = "set_analog_output")]
    [Description("Drive an analog output (DAC) channel to a voltage. Nyquist 3 only — other boards are refused. Out-of-range voltages are rejected before send; latch=false stages instead. The latch is device-wide: latch=true also applies anything staged earlier on other channels.")]
    public static Task<AnalogOutputResult> SetAnalogOutput(
        DaqifiAgent agent,
        [Description("The device_id to control.")] string deviceId,
        [Description("The analog output channel number, as list_analog_outputs reports it.")] int channel,
        [Description("The output voltage in volts. Must lie inside the channel's range (commonly 0-10 V).")] double volts,
        [Description("Apply the value now (default). Pass false to stage it without changing the pin; the next latch_analog_outputs applies it.")] bool latch = true)
        => GuardAsync(() => agent.SetAnalogOutputAsync(deviceId, channel, volts, latch));

    [McpServerTool(Name = "latch_analog_outputs")]
    [Description("Apply every analog output voltage staged with set_analog_output latch=false. Harmless with nothing staged — the device re-applies what it already holds.")]
    public static Task<AnalogOutputLatchResult> LatchAnalogOutputs(
        DaqifiAgent agent,
        [Description("The device_id to latch.")] string deviceId)
        => GuardAsync(() => agent.LatchAnalogOutputsAsync(deviceId));

    [McpServerTool(Name = "read_analog_output")]
    [Description("Ask the device what voltage a DAC channel was last told to drive — not a pin measurement. Refused while streaming.")]
    public static Task<AnalogOutputReading> ReadAnalogOutput(
        DaqifiAgent agent,
        [Description("The device_id to query.")] string deviceId,
        [Description("The analog output channel number.")] int channel,
        CancellationToken cancellationToken = default)
        => GuardAsync(() => agent.ReadAnalogOutputAsync(deviceId, channel, cancellationToken));

    [McpServerTool(Name = "set_sample_rate")]
    [Description("Set the device sample rate in Hz for streaming and SD logging. Requests above the enabled-channel ceiling (or --max-sample-rate-hz) are rejected, not silently lowered.")]
    public static Task<SampleRateResult> SetSampleRate(
        DaqifiAgent agent,
        [Description("The device_id to configure.")] string deviceId,
        [Description("Sample rate in Hz. The ceiling varies with the enabled analog channel count.")] int rateHz)
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

    [McpServerTool(Name = "list_sd_files")]
    [Description("List the log files on the device's SD card. An empty list means an empty card; a device that fails to answer raises instead.")]
    public static Task<SdFileListing> ListSdFiles(
        DaqifiAgent agent,
        [Description("The device_id to list files on.")] string deviceId,
        CancellationToken cancellationToken = default)
        => GuardAsync(() => agent.ListSdFilesAsync(deviceId, cancellationToken));

    [McpServerTool(Name = "get_sd_storage")]
    [Description("Report free, used, and total space on the device's SD card. Refused while logging — call stop_sd_logging first.")]
    public static Task<SdStorageReport> GetSdStorage(
        DaqifiAgent agent,
        [Description("The device_id to inspect.")] string deviceId,
        CancellationToken cancellationToken = default)
        => GuardAsync(() => agent.GetSdStorageAsync(deviceId, cancellationToken));

    [McpServerTool(Name = "download_sd_file")]
    [Description("Download an SD-card log file and by default parse it to CSV. Retrieve before any live stream on the same connection — streaming collapses the SD buffer (firmware #703) and later downloads come back empty.")]
    public static Task<SdDownloadReport> DownloadSdFile(
        DaqifiAgent agent,
        [Description("The device_id to download from.")] string deviceId,
        [Description("The on-card file name as list_sd_files reports it. A name that is not on the card is rejected.")] string fileName,
        [Description("Also parse the download and write a CSV (default true). If the parse fails, the download still succeeds and csvError explains why.")] bool exportCsv = true,
        CancellationToken cancellationToken = default)
        => GuardAsync(() => agent.DownloadSdFileAsync(deviceId, fileName, exportCsv, cancellationToken));

    [McpServerTool(Name = "delete_sd_file")]
    [Description("Permanently delete a file from the device's SD card. No undo — download first if the data matters. Refused in --read-only mode and while logging.")]
    public static Task<SdDeleteResult> DeleteSdFile(
        DaqifiAgent agent,
        [Description("The device_id to delete from.")] string deviceId,
        [Description("The on-card file name, exactly as list_sd_files reports it.")] string fileName,
        CancellationToken cancellationToken = default)
        => GuardAsync(() => agent.DeleteSdFileAsync(deviceId, fileName, cancellationToken));

    [McpServerTool(Name = "read_channel_values")]
    [Description("Read the latest value on every enabled channel (volts or 0/1) with its sample timestamp. Enable channels first — none enabled is refused. Starts the stream if idle (refused in --read-only); a running stream is left running. Silent channels come back null, not zero.")]
    public static Task<ChannelReadings> ReadChannelValues(
        DaqifiAgent agent,
        [Description("The device_id to read.")] string deviceId,
        [Description("How long to wait for every enabled channel, in milliseconds (default 2000; clamped to 500..30000). Reached only when a channel stays silent.")] int timeoutMs = 2000,
        CancellationToken cancellationToken = default)
        => GuardAsync(() => agent.ReadChannelValuesAsync(deviceId, timeoutMs, cancellationToken));

    [McpServerTool(Name = "capture_samples")]
    [Description("Capture live data as rows (one per sample tick, columns named AI0/DIO0). Configure channels and the sample rate first. Ends at duration or maxRows, whichever first. Starts the stream if idle (refused in --read-only); holds the device exclusively. measuredRateHz is this machine's clock, deviceClockRateHz the device's own timestamps — compare both with sampleRateHz to see what actually arrived.")]
    public static Task<CaptureResult> CaptureSamples(
        DaqifiAgent agent,
        [Description("The device_id to capture from.")] string deviceId,
        [Description("How long to capture, in milliseconds (default 1000; clamped to 250..60000).")] int durationMs = 1000,
        [Description("Most rows to return (default 500; clamped to 1..10000). A row is one sample tick across all enabled channels.")] int maxRows = 500,
        CancellationToken cancellationToken = default)
        => GuardAsync(() => agent.CaptureSamplesAsync(deviceId, durationMs, maxRows, cancellationToken));

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
