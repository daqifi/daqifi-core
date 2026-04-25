namespace Daqifi.Core.Logging.Export;

/// <summary>
/// Represents a single data point — one channel value at one point in time.
/// </summary>
/// <param name="TimestampTicks">
/// The <see cref="DateTime.Ticks"/> value for this sample.
/// The stream from <see cref="ISampleSource.StreamSamples"/> must be ordered by this value ascending.
/// </param>
/// <param name="ChannelKey">
/// The composite channel identifier in the format <c>{DeviceName}:{DeviceSerialNo}:{ChannelName}</c>.
/// Must match a key returned by <see cref="ISampleSource.GetChannels"/>.
/// </param>
/// <param name="Value">The numeric value of the sample.</param>
public record SampleRow(long TimestampTicks, string ChannelKey, double Value);
