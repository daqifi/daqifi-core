using System.Collections.Generic;
using System.Threading;
using Daqifi.Core.Channel;

#nullable enable

namespace Daqifi.Core.Device;

/// <summary>
/// A device that can hand its decoded samples to a caller as they arrive, as an
/// <see cref="IAsyncEnumerable{T}"/> of <see cref="LiveSample"/>. Implemented by
/// <see cref="DaqifiStreamingDevice"/>.
/// </summary>
/// <remarks>
/// <para>
/// A capability interface rather than members on <see cref="IStreamingDevice"/>, matching
/// <see cref="SdCard.ISdCardOperations"/>: a caller holding a device asks
/// <c>if (device is ILiveSampleSource live)</c> and gets the live stream without casting to a
/// concrete class. That is what this interface is for — before it, reading live data required
/// naming <see cref="DaqifiStreamingDevice"/> itself, which every typed consumer of the package
/// had to do even though nothing about the operation is specific to that class (#498).
/// </para>
/// <para>
/// Not to be confused with <see cref="Logging.Export.ISampleSource"/>, which is the offline
/// side: an already-recorded set of samples being replayed into an exporter. This one is the
/// live device, and its enumeration ends when the device's connection does.
/// </para>
/// </remarks>
public interface ILiveSampleSource
{
    /// <inheritdoc cref="DaqifiStreamingDevice.DroppedLiveSampleCount"/>
    long DroppedLiveSampleCount { get; }

    /// <inheritdoc cref="DaqifiStreamingDevice.StreamSamplesAsync"/>
    IAsyncEnumerable<LiveSample> StreamSamplesAsync(
        CancellationToken cancellationToken = default,
        int? bufferCapacity = null);
}
