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
/// <see cref="Unit"/> is the engineering unit this channel's readings are expressed in — the
/// device's own (typically <c>"V"</c>) until a caller configures a transducer conversion, and
/// <c>null</c> until the capability document has been read.
/// </summary>
public sealed record ChannelInfo(int ChannelNumber, string Type, string Name, bool Enabled, string Direction, bool? OutputValue, bool? PwmCapable, bool? PwmEnabled, string? Unit = null)
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
            digital?.IsPwmEnabled,
            (ch as IScaledChannel)?.Unit);
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

/// <summary>
/// One file in the device's SD-card directory listing. <see cref="SizeBytes"/> and
/// <see cref="CreatedDate"/> are null when the listing carried neither — a size of 0 is a real
/// (empty) file, which is why an unknown size is null rather than zero.
/// </summary>
public sealed record SdFileEntry(string FileName, long? SizeBytes, DateTime? CreatedDate)
{
    public static SdFileEntry From(SdCardFileInfo info) =>
        new(info.FileName, info.SizeInBytes, info.CreatedDate);
}

/// <summary>
/// The device's SD-card directory listing. An empty <see cref="Files"/> list always means an
/// empty card: a device that did not answer the query fails the tool call instead (#396).
/// </summary>
public sealed record SdFileListing(string DeviceId, int FileCount, IReadOnlyList<SdFileEntry> Files);

/// <summary>
/// Free/used/total space on the device's SD card. <see cref="PercentFree"/> is rounded to one
/// decimal and is 0 when the device reports a total of 0 bytes.
/// </summary>
public sealed record SdStorageReport(
    string DeviceId, long FreeBytes, long UsedBytes, long TotalBytes, double PercentFree)
{
    public static SdStorageReport From(string deviceId, SdCardStorageInfo info) => new(
        deviceId,
        info.FreeBytes,
        info.UsedBytes,
        info.TotalBytes,
        info.TotalBytes > 0 ? Math.Round(info.FreeBytes * 100.0 / info.TotalBytes, 1) : 0);
}

/// <summary>
/// Result of downloading an SD-card file to this machine.
/// </summary>
/// <param name="FilePath">Where the raw file was written locally (a temporary file owned by this server).</param>
/// <param name="CsvPath">Where the CSV was written, or null when CSV export was not requested or failed.</param>
/// <param name="CsvRowCount">CSV lines written — one per distinct timestamp, not one per sample.</param>
/// <param name="SampleCount">Log entries read out of the file.</param>
/// <param name="CsvError">
/// What went wrong in the CSV step while the download itself succeeded — an unparseable format, an
/// empty log, or a CSV that was written but is missing columns. Reported rather than thrown so a
/// download that can take minutes is not discarded along with the error. Worth reading even when
/// <paramref name="CsvPath"/> is set: that combination means the CSV exists but is incomplete.
/// </param>
public sealed record SdDownloadReport(
    string DeviceId,
    string FileName,
    long SizeBytes,
    double DurationSeconds,
    string FilePath,
    string? CsvPath,
    long? CsvRowCount,
    long? SampleCount,
    string? CsvError);

/// <summary>Result of deleting a file from the SD card.</summary>
public sealed record SdDeleteResult(string DeviceId, string FileName);
