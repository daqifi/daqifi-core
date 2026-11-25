using Daqifi.Core.Device;
using Microsoft.Extensions.Logging;

namespace Daqifi.Core.Tests.Device;

/// <summary>A <see cref="DaqifiDevice"/> with no transport: it connects to nothing and cannot send.</summary>
internal sealed class TransportlessDevice(string name, ILogger? logger = null) : DaqifiDevice(name, logger);

/// <summary>A <see cref="DaqifiStreamingDevice"/> with no transport: it connects to nothing and cannot send.</summary>
internal sealed class TransportlessStreamingDevice(string name, ILogger? logger = null)
    : DaqifiStreamingDevice(name, logger);

/// <summary>A <see cref="DaqifiDevice"/> that writes to a stream and reads nothing.</summary>
internal sealed class StreamBackedDevice(string name, Stream stream, ILogger? logger = null)
    : DaqifiDevice(name, stream, logger);
