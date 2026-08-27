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

/// <summary>
/// Summary of a currently-connected device. <see cref="AnalogOutputChannelCount"/> counts DAC
/// channels, which are reported separately from <see cref="AnalogChannelCount"/> because they are
/// driven rather than measured — they never appear in an acquisition and are written with
/// set_analog_output. It is 0 on every board but Nyquist 3, and also on a Nyquist 3 whose
/// capability document did not describe them (see <see cref="AnalogOutputState"/>).
/// </summary>
public sealed record ConnectedDeviceInfo(
    string DeviceId,
    string Name,
    bool Connected,
    int AnalogChannelCount,
    int DigitalChannelCount,
    int AnalogOutputChannelCount)
{
    public static ConnectedDeviceInfo From(string id, DaqifiDevice device)
    {
        var analog = 0;
        var digital = 0;
        var analogOutput = 0;
        foreach (var ch in device.GetChannelsSnapshot())
        {
            if (ch.Type == ChannelType.Analog) analog++;
            else if (ch.Type == ChannelType.Digital) digital++;
            else if (ch.Type == ChannelType.AnalogOutput) analogOutput++;
        }
        return new ConnectedDeviceInfo(id, device.Name, device.IsConnected, analog, digital, analogOutput);
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
/// One analog-output (DAC) channel: what it accepts and what it is driving.
/// </summary>
/// <param name="Channel">The channel number, as set_analog_output takes it.</param>
/// <param name="Name">The channel's display name.</param>
/// <param name="Volts">
/// The voltage this channel is driving, or <c>null</c> when nothing has driven it yet. It is what
/// this server last latched or last read back — the DAC has no hardware readback, so no value
/// anywhere is a measurement of the pin.
/// </param>
/// <param name="PendingVolts">
/// A voltage staged by set_analog_output with <c>latch=false</c> and not yet applied, or
/// <c>null</c> when nothing is waiting. It reaches the pin on the next latch_analog_outputs.
/// </param>
/// <param name="MinVolts">The lowest voltage the channel accepts.</param>
/// <param name="MaxVolts">The highest voltage the channel accepts.</param>
/// <param name="RangeIsAssumed">
/// Whether the range above is this library's fallback rather than the device's own statement. When
/// <c>true</c> the device described the channel but not its range, so a rejected write may reflect
/// an assumption rather than the hardware's real limit.
/// </param>
/// <param name="ResolutionBits">The DAC resolution in bits.</param>
public sealed record AnalogOutputState(
    int Channel,
    string Name,
    double? Volts,
    double? PendingVolts,
    double MinVolts,
    double MaxVolts,
    bool RangeIsAssumed,
    int ResolutionBits)
{
    public static AnalogOutputState From(IAnalogOutputChannel ch) => new(
        ch.ChannelNumber,
        ch.Name,
        ch.OutputVoltage,
        ch.PendingVoltage,
        ch.MinimumVoltage,
        ch.MaximumVoltage,
        ch.RangeIsAssumed,
        ch.ResolutionBits);
}

/// <summary>
/// Result of writing an analog-output voltage.
/// </summary>
/// <param name="Applied">
/// Whether the value is now on the pin. <c>false</c> means it was staged only and is waiting for
/// latch_analog_outputs.
/// </param>
/// <param name="RangeChecked">
/// Whether the voltage was validated against a range the device stated. <c>false</c> means the
/// device described no analog outputs at all — the command was addressed by channel number and
/// nothing here can vouch for it (see <see cref="DaqifiAgent.SetAnalogOutputAsync"/>).
/// </param>
/// <param name="State">
/// The channel afterwards, or <c>null</c> when the device described no analog outputs and there is
/// therefore no modelled channel to report.
/// </param>
public sealed record AnalogOutputResult(
    string DeviceId,
    int Channel,
    double RequestedVolts,
    bool Applied,
    bool RangeChecked,
    AnalogOutputState? State);

/// <summary>
/// Result of latching the staged analog outputs: every modelled analog-output channel afterwards,
/// so a caller that staged several sees them all take effect together. <see cref="Outputs"/> is
/// empty when the device described no analog outputs — the latch command was still sent.
/// </summary>
public sealed record AnalogOutputLatchResult(string DeviceId, IReadOnlyList<AnalogOutputState> Outputs);

/// <summary>
/// What the device answered when asked what an analog output is holding.
/// </summary>
/// <param name="Volts">
/// The voltage the device reports. Not a measurement of the pin: the DAC has no readback path, so
/// the firmware answers with the value it was last told to drive. It is still the authoritative
/// round-trip — it reflects what the device accepted, including a write made before this server
/// connected.
/// </param>
public sealed record AnalogOutputReading(string DeviceId, int Channel, double Volts);

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

/// <summary>
/// The most recent live value on one channel.
/// </summary>
/// <param name="Column">The channel's label — <c>AI0</c>, <c>DIO3</c>.</param>
/// <param name="Channel">The channel number, which repeats across types (AI0 and DIO0 both exist).</param>
/// <param name="Type">Analog or Digital.</param>
/// <param name="Value">
/// The decoded value — volts for an analog channel, 0/1 for a digital one — or <c>null</c> when this
/// channel produced no sample inside the read window. Null is not zero: it means no data arrived.
/// </param>
/// <param name="Timestamp">When the sample was taken, reconstructed from the device clock.</param>
/// <param name="DeviceTimestamp">The device's own clock ticks for the sample, verbatim.</param>
/// <param name="RawValue">
/// The raw device value the reading was decoded from: the raw ADC count for an analog channel, or
/// the 0/1 bit for a digital one. Supported firmware streams raw counts on every transport, so an
/// analog reading carries one. <c>null</c> means no sample arrived inside the read window (the same
/// condition that makes <c>Value</c> null), or that the frame carried an already-scaled value
/// instead of a count, which no supported firmware sends.
/// </param>
public sealed record ChannelReading(
    string Column,
    int Channel,
    string Type,
    double? Value,
    DateTime? Timestamp,
    uint? DeviceTimestamp,
    int? RawValue);

/// <summary>
/// A spot reading of every enabled channel.
/// </summary>
/// <param name="SampleRateHz">The device's streaming rate while the reading was taken.</param>
/// <param name="StartedStream">
/// Whether this call started the device's stream (and stopped it again afterwards). <c>false</c>
/// means a stream was already running and was left running.
/// </param>
/// <param name="ChannelsReported">How many of the listed channels actually produced a value.</param>
/// <param name="DurationSeconds">How long the read waited — well under the timeout once every channel has reported.</param>
/// <param name="DroppedSampleCount">Samples the device produced faster than this server consumed them.</param>
/// <param name="IgnoredSampleCount">
/// Samples that arrived for a channel outside the enabled set this call snapshotted — someone
/// enabled a channel while the read was running.
/// </param>
public sealed record ChannelReadings(
    string DeviceId,
    int SampleRateHz,
    bool StartedStream,
    int ChannelsReported,
    double DurationSeconds,
    long DroppedSampleCount,
    long IgnoredSampleCount,
    IReadOnlyList<ChannelReading> Channels);

/// <summary>
/// One row of a capture: a single sample tick, with one entry per column in
/// <see cref="CaptureResult.Columns"/> order.
/// </summary>
/// <param name="Timestamp">The tick's timestamp, reconstructed from the device clock.</param>
/// <param name="DeviceTimestamp">The device's own clock ticks for the row, verbatim.</param>
/// <param name="Values">
/// One value per column, in <see cref="CaptureResult.Columns"/> order. An entry is <c>null</c> when
/// that channel had no value on this tick, which in practice happens only at the two ends: the
/// device's first frame after a stream starts can carry the digital port without the analog
/// readings, and a capture that runs out of time mid-tick keeps the part of the tick it got.
/// </param>
public sealed record CaptureRow(DateTime Timestamp, uint? DeviceTimestamp, IReadOnlyList<double?> Values);

/// <summary>
/// A bounded block of live data.
/// </summary>
/// <param name="SampleRateHz">The rate the device was asked to stream at.</param>
/// <param name="StartedStream">
/// Whether this call started the device's stream (and stopped it again afterwards). <c>false</c>
/// means it attached to a stream that was already running and left it running.
/// </param>
/// <param name="DurationSeconds">How long the capture actually ran.</param>
/// <param name="RowCount">Rows returned.</param>
/// <param name="SampleCount">Individual channel samples those rows hold.</param>
/// <param name="DroppedSampleCount">
/// Samples the device produced faster than this server consumed them, so they never reached a row.
/// Non-zero means the capture has gaps — the rows themselves cannot show that.
/// </param>
/// <param name="IgnoredSampleCount">
/// Samples that arrived for a channel outside the enabled set this call snapshotted — someone
/// enabled a channel while the capture was running.
/// </param>
/// <param name="MeasuredRateHz">
/// Rows per second actually achieved, timed by this machine's clock from the first row to the last
/// (so the wait for the device's stream to start is not counted against it). Compare it with
/// <paramref name="SampleRateHz"/>: a large gap means the device is not streaming at the rate it
/// was asked for.
/// </param>
/// <param name="DeviceClockRateHz">
/// Rows per second according to the device's <b>own</b> clock — the timestamps on the rows rather
/// than this machine's stopwatch. It is reported alongside <paramref name="MeasuredRateHz"/>
/// because the two disagreeing is its own diagnosis: either alone can only say "slower than
/// requested", while a device whose clock claims the full rate while real time says otherwise has a
/// clock that is not keeping real time, and its timestamps cannot be trusted as durations. Zero
/// when fewer than two rows were captured.
/// </param>
/// <param name="RowLimitReached">
/// Whether the capture stopped because it filled its row budget rather than because its time ran
/// out — so there was more data available.
/// </param>
public sealed record CaptureResult(
    string DeviceId,
    int SampleRateHz,
    bool StartedStream,
    double DurationSeconds,
    int RowCount,
    long SampleCount,
    long DroppedSampleCount,
    long IgnoredSampleCount,
    double MeasuredRateHz,
    double DeviceClockRateHz,
    bool RowLimitReached,
    IReadOnlyList<string> Columns,
    IReadOnlyList<CaptureRow> Rows);
