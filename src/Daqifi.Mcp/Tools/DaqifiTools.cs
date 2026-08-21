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
        [Description("Discovery timeout in milliseconds (default 2000; clamped to 1000..30000). The floor is bench-measured: the serial identify handshake takes ~830 ms, so a lower budget returns an empty list before a device could ever answer — indistinguishable from none being attached. Serial probing can return before the full timeout once probes settle; WiFi discovery listens for replies for the whole window regardless, so a wifi=true call (the default) generally takes close to the full budget either way.")] int timeoutMs = 2000,
        [Description("Include WiFi/network discovery (default true).")] bool wifi = true,
        [Description("Include USB/serial discovery (default true).")] bool serial = true,
        CancellationToken cancellationToken = default)
        => GuardAsync(() => agent.DiscoverAsync(timeoutMs, wifi, serial, cancellationToken));

    [McpServerTool(Name = "connect_device")]
    [Description("Connect to a previously-discovered device. Pass a device_id from discover_devices. Channels are populated on connect. If that physical device is already connected (including over a different transport), the existing connection is returned — use the device_id from the result for follow-up calls.")]
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
    [Description("Enable exactly the given analog input channels (by channel number) and disable the rest. Pass an empty list to disable all analog channels. Widening the channel set can lower the device's sample-rate ceiling; if the currently set rate no longer fits, it is automatically lowered to the new ceiling and the response's sampleRateAdjustedFromHz reports the rate it was lowered from.")]
    public static Task<ConfigureResult> ConfigureAnalogChannels(
        DaqifiAgent agent,
        [Description("The device_id to configure.")] string deviceId,
        [Description("Analog channel numbers to enable, e.g. [0,1,2,3]. Channels not listed are disabled.")] int[] enabledChannels)
        => GuardAsync(() => agent.ConfigureAnalogChannelsAsync(deviceId, enabledChannels));

    [McpServerTool(Name = "configure_digital_channels")]
    [Description("Enable exactly the given digital channels (by channel number) and disable the rest. Enabled digital channels are sampled during streaming; the device's DIO enable is global, so enabling any digital channel powers the whole port. Pass an empty list to disable all digital channels. As with configure_analog_channels, an over-cap live sample rate is automatically lowered and reported via sampleRateAdjustedFromHz.")]
    public static Task<ConfigureDigitalResult> ConfigureDigitalChannels(
        DaqifiAgent agent,
        [Description("The device_id to configure.")] string deviceId,
        [Description("Digital channel numbers to enable, e.g. [0,1,2]. Channels not listed are disabled.")] int[] enabledChannels)
        => GuardAsync(() => agent.ConfigureDigitalChannelsAsync(deviceId, enabledChannels));

    [McpServerTool(Name = "set_digital_direction")]
    [Description("Set a digital channel's direction: 'input' (high-impedance, sampled during streaming) or 'output' (driven by the device; set the level with set_digital_output). Rejected while PWM is enabled on the channel — call disable_pwm first.")]
    public static Task<DigitalPinResult> SetDigitalDirection(
        DaqifiAgent agent,
        [Description("The device_id to configure.")] string deviceId,
        [Description("The digital channel number (e.g. 0-15 on Nyquist).")] int channel,
        [Description("'input' or 'output'.")] string direction)
        => GuardAsync(() => agent.SetDigitalDirectionAsync(deviceId, channel, direction));

    [McpServerTool(Name = "set_digital_output")]
    [Description("Drive a digital channel high or low. If the channel is currently an input it is switched to output direction first, so one call is enough to drive a pin. Rejected while PWM is enabled on the channel — call disable_pwm first.")]
    public static Task<DigitalPinResult> SetDigitalOutput(
        DaqifiAgent agent,
        [Description("The device_id to control.")] string deviceId,
        [Description("The digital channel number (e.g. 0-15 on Nyquist).")] int channel,
        [Description("true to drive the pin high, false to drive it low.")] bool high)
        => GuardAsync(() => agent.SetDigitalOutputAsync(deviceId, channel, high));

    [McpServerTool(Name = "set_pwm_output")]
    [Description("Start PWM output on a PWM-capable digital channel (Nyquist: channels 0, 3, 4, 5, 6, 7). Sets the duty cycle, optionally the device-wide frequency, then enables the channel. The frequency is shared by ALL PWM channels (one hardware timer). While PWM runs, set_digital_direction/set_digital_output on the channel are rejected rather than silently ignored — call disable_pwm first to drive it digitally again.")]
    public static Task<PwmResult> SetPwmOutput(
        DaqifiAgent agent,
        [Description("The device_id to control.")] string deviceId,
        [Description("The PWM-capable digital channel number.")] int channel,
        [Description("Duty cycle in whole percent, 1-100. To stop the output use disable_pwm, not duty 0.")] int dutyCyclePercent,
        [Description("PWM frequency in Hz, 6-50000, applied device-wide. Pass 0 to keep the current session frequency (defaults to 1000 Hz until explicitly set).")] int frequencyHz = 0)
        => GuardAsync(() => agent.SetPwmOutputAsync(deviceId, channel, dutyCyclePercent, frequencyHz));

    [McpServerTool(Name = "disable_pwm")]
    [Description("Stop PWM output on a digital channel. The pin is left high-impedance (not driven); use set_digital_direction/set_digital_output to drive it digitally again. Allowed on any digital channel, including one that isn't PWM-capable — this is the only recovery path for the firmware's half-armed PWM state. This call always succeeds from the caller's point of view: if the channel was never actually armed, the firmware rejects the command internally but that rejection is not surfaced here (the tool never throws for it and the result carries no error field).")]
    public static Task<PwmResult> DisablePwm(
        DaqifiAgent agent,
        [Description("The device_id to control.")] string deviceId,
        [Description("The digital channel number to stop PWM on.")] int channel)
        => GuardAsync(() => agent.DisablePwmAsync(deviceId, channel));

    [McpServerTool(Name = "list_analog_outputs")]
    [Description("List the device's analog output (DAC) channels with the voltage range each accepts, its resolution, and the value it is driving. Call this before set_analog_output to learn the legal range. An empty list means no DAC channel is modelled, which happens two ways: the board has none (analog output is Nyquist 3 hardware), or it has them but did not describe them in its capability document (firmware below v3.5.0). Do not read an empty list as 'writing is impossible' — in the second case set_analog_output still drives the channel by number and answers with rangeChecked false, saying that nothing validated the voltage; only in the first is it refused. Available in --read-only mode; costs no device round-trip, so `volts` is only what this server has written or read back this session — a null there means this server has not touched the channel, NOT that the pin is at 0 V. Use read_analog_output to ask the device itself.")]
    public static IReadOnlyList<AnalogOutputState> ListAnalogOutputs(
        DaqifiAgent agent,
        [Description("The device_id to inspect.")] string deviceId)
        => Guard(() => agent.ListAnalogOutputs(deviceId));

    [McpServerTool(Name = "set_analog_output")]
    [Description("Drive an analog output (DAC) channel to a voltage. Nyquist 3 hardware only; on any other board the call is refused rather than silently discarded by the firmware. A voltage outside the channel's range is rejected before anything is sent — call list_analog_outputs for the range. By default the value takes effect immediately; pass latch=false to stage it instead and apply several channels together with latch_analog_outputs. Note that the latch is device-wide, not per-channel: a call with latch=true also applies anything staged earlier on OTHER channels, and the result reports only the channel written — call list_analog_outputs afterwards to see them all.")]
    public static Task<AnalogOutputResult> SetAnalogOutput(
        DaqifiAgent agent,
        [Description("The device_id to control.")] string deviceId,
        [Description("The analog output channel number, as list_analog_outputs reports it.")] int channel,
        [Description("The output voltage in volts. Must lie inside the channel's range (commonly 0-10 V).")] double volts,
        [Description("Apply the value now (default). Pass false to stage it without changing the pin; it takes effect on the next latch_analog_outputs, which is how several outputs are made to change together.")] bool latch = true)
        => GuardAsync(() => agent.SetAnalogOutputAsync(deviceId, channel, volts, latch));

    [McpServerTool(Name = "latch_analog_outputs")]
    [Description("Apply every analog output voltage staged with set_analog_output latch=false, so the staged channels change together. Returns the state of every analog output afterwards. Harmless with nothing staged — the device re-applies what it already holds.")]
    public static Task<AnalogOutputLatchResult> LatchAnalogOutputs(
        DaqifiAgent agent,
        [Description("The device_id to latch.")] string deviceId)
        => GuardAsync(() => agent.LatchAnalogOutputsAsync(deviceId));

    [McpServerTool(Name = "read_analog_output")]
    [Description("Ask the device what voltage an analog output channel is holding. This is a round-trip to the firmware, so it reflects what the device actually accepted — including a value written before this server connected — but it is not a measurement of the pin: the DAC has no readback path, so the device answers with the value it was last told to drive. Refused while the device is streaming, because the binary stream corrupts the reply. Available in --read-only mode.")]
    public static Task<AnalogOutputReading> ReadAnalogOutput(
        DaqifiAgent agent,
        [Description("The device_id to query.")] string deviceId,
        [Description("The analog output channel number.")] int channel,
        CancellationToken cancellationToken = default)
        => GuardAsync(() => agent.ReadAnalogOutputAsync(deviceId, channel, cancellationToken));

    [McpServerTool(Name = "set_sample_rate")]
    [Description("Set the device sample (streaming) rate in Hz, applied to streaming and SD-card logging. The achievable maximum depends on how many channels are currently enabled (configure channels first for an accurate ceiling) and is further capped by --max-sample-rate-hz if set; a request above the effective cap is rejected — the call throws rather than silently applying a lower rate.")]
    public static Task<SampleRateResult> SetSampleRate(
        DaqifiAgent agent,
        [Description("The device_id to configure.")] string deviceId,
        [Description("Sample rate in Hz. The ceiling varies with the enabled channel count; get_device_status or a prior configure_analog_channels call error message reports the current limit.")] int rateHz)
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
    [Description("List the log files on the device's SD card, with size and creation date where the device reports them. An empty list always means an empty card — a device that fails to answer the listing raises an error instead. Available in --read-only mode.")]
    public static Task<SdFileListing> ListSdFiles(
        DaqifiAgent agent,
        [Description("The device_id to list files on.")] string deviceId,
        CancellationToken cancellationToken = default)
        => GuardAsync(() => agent.ListSdFilesAsync(deviceId, cancellationToken));

    [McpServerTool(Name = "get_sd_storage")]
    [Description("Report free, used, and total space on the device's SD card. Refused while the device is logging (the SD card is busy) — call stop_sd_logging first. Available in --read-only mode.")]
    public static Task<SdStorageReport> GetSdStorage(
        DaqifiAgent agent,
        [Description("The device_id to inspect.")] string deviceId,
        CancellationToken cancellationToken = default)
        => GuardAsync(() => agent.GetSdStorageAsync(deviceId, cancellationToken));

    [McpServerTool(Name = "download_sd_file")]
    [Description("Download an SD-card log file to this machine and, by default, parse it into a CSV you can read. Returns the local path of both files plus the sample and CSV row counts (a row is one timestamp, not one sample). Run SD retrieval before any live streaming on the same connection: a stream collapses the device's SD buffer and later downloads come back empty. Filenames come from list_sd_files. Large files take as long as the transfer takes.")]
    public static Task<SdDownloadReport> DownloadSdFile(
        DaqifiAgent agent,
        [Description("The device_id to download from.")] string deviceId,
        [Description("The on-card file name as list_sd_files reports it (matched without case sensitivity). A name that is not on the card is rejected straight away.")] string fileName,
        [Description("Also parse the download and write a CSV next to it (default true). Set false to fetch the raw file only, e.g. when it is large and you just want it on disk. If the parse fails, the download still succeeds and csvError explains why.")] bool exportCsv = true,
        CancellationToken cancellationToken = default)
        => GuardAsync(() => agent.DownloadSdFileAsync(deviceId, fileName, exportCsv, cancellationToken));

    [McpServerTool(Name = "delete_sd_file")]
    [Description("Permanently delete a file from the device's SD card. There is no undo and no recycle bin — download it first if the data matters. Refused in --read-only mode, and refused while the device is logging.")]
    public static Task<SdDeleteResult> DeleteSdFile(
        DaqifiAgent agent,
        [Description("The device_id to delete from.")] string deviceId,
        [Description("The on-card file name, exactly as list_sd_files reports it.")] string fileName,
        CancellationToken cancellationToken = default)
        => GuardAsync(() => agent.DeleteSdFileAsync(deviceId, fileName, cancellationToken));

    [McpServerTool(Name = "read_channel_values")]
    [Description("Read the latest value on every enabled channel: volts for analog inputs, 0/1 for digital ones, each with the timestamp it was sampled at. Returns as soon as every enabled channel has reported, so it normally costs one sample period rather than the full timeout; a channel that reported nothing comes back with a null value rather than a zero. Configure and enable channels first — with none enabled the device sends nothing and the call is refused. If the device is not already streaming this starts its stream and stops it again afterwards (refused in --read-only mode, since that is a change); a stream that was already running is read and left running.")]
    public static Task<ChannelReadings> ReadChannelValues(
        DaqifiAgent agent,
        [Description("The device_id to read.")] string deviceId,
        [Description("How long to wait for every enabled channel to report, in milliseconds (default 2000; clamped to 500..30000). Reached only when a channel stays silent: a device already streaming at 1 kHz answers in a few milliseconds, and one that has to be started answers in about 100 ms — which is why the floor is where it is.")] int timeoutMs = 2000,
        CancellationToken cancellationToken = default)
        => GuardAsync(() => agent.ReadChannelValuesAsync(deviceId, timeoutMs, cancellationToken));

    [McpServerTool(Name = "capture_samples")]
    [Description("Capture a block of live data as rows: one row per sample tick, one column per enabled channel (columns are named AI0/DIO0 and listed in the result). The capture ends at whichever budget runs out first, the duration or the row count, and reports what it actually got — rows, the rate it achieved, and the number of samples dropped because this server could not keep up. Compare measuredRateHz (this machine's clock) with sampleRateHz to see whether the device is streaming as fast as it was asked to, and with deviceClockRateHz (the device's own timestamps) to see whether its clock is keeping real time. Configure channels and the sample rate first. If the device is not already streaming this starts its stream and stops it again afterwards (refused in --read-only mode); a stream that was already running is read and left running. The device is held exclusively for the whole capture, so other tool calls on it wait.")]
    public static Task<CaptureResult> CaptureSamples(
        DaqifiAgent agent,
        [Description("The device_id to capture from.")] string deviceId,
        [Description("How long to capture for, in milliseconds (default 1000; clamped to 250..60000). The floor is bench-measured: a device that is not streaming yet sends nothing for the first ~100 ms after the start command, so a shorter window would come back empty from a healthy device.")] int durationMs = 1000,
        [Description("Most rows to return (default 500; clamped to 1..10000). A row is one sample tick across all enabled channels, so 1000 rows at 1 kHz is one second of data. rowLimitReached in the result tells you the capture stopped on this budget rather than on time — i.e. there was more data.")] int maxRows = 500,
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
