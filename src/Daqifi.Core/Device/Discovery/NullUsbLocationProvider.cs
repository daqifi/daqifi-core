namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// No-op location provider that returns null for every input. Used as the fallback on
/// platforms where USB physical-location resolution isn't implemented (Linux, macOS,
/// and any other non-Windows platform in v1).
/// </summary>
internal sealed class NullUsbLocationProvider : IUsbLocationProvider
{
    public static readonly NullUsbLocationProvider Instance = new();

    private NullUsbLocationProvider() { }

    public string? GetLocationKey(string portNameOrDevicePath) => null;
}
