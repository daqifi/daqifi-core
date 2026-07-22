using Daqifi.Core.Device.Discovery;

namespace Daqifi.Core.Device;

/// <summary>
/// One entry in a <see cref="DaqifiDeviceRegistry"/>: a connected device, the key it is filed
/// under, and the identity used to recognize it across transports.
/// </summary>
public sealed class DeviceRegistration
{
    internal DeviceRegistration(string key, DaqifiDevice device, DeviceIdentity identity, IDeviceInfo? discoveryInfo)
    {
        Key = key;
        Device = device;
        Identity = identity;
        DiscoveryInfo = discoveryInfo;
    }

    /// <summary>
    /// Gets the key this device is filed under — the handle callers pass to
    /// <see cref="DaqifiDeviceRegistry.TryGet"/> and <see cref="DaqifiDeviceRegistry.Remove(string)"/>.
    /// Defaults to <see cref="DeviceIdentity.Key"/> unless the caller supplied its own.
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// Gets the connected device. The registry owns it: it is disconnected and disposed when the
    /// registration is removed.
    /// </summary>
    public DaqifiDevice Device { get; }

    /// <summary>
    /// Gets the identity used to recognize this physical unit across transports.
    /// </summary>
    public DeviceIdentity Identity { get; }

    /// <summary>
    /// Gets the discovery metadata this device was connected from, or <c>null</c> when an
    /// already-connected device was registered without it.
    /// </summary>
    public IDeviceInfo? DiscoveryInfo { get; }

    /// <summary>
    /// Gets the transport this device is connected over, or <see cref="ConnectionType.Unknown"/>
    /// when it was registered without discovery metadata.
    /// </summary>
    public ConnectionType ConnectionType => DiscoveryInfo?.ConnectionType ?? ConnectionType.Unknown;

    /// <summary>
    /// Returns a diagnostic string naming the key, transport, and identity.
    /// </summary>
    public override string ToString() => $"{Key} [{ConnectionType}] {Identity}";
}
