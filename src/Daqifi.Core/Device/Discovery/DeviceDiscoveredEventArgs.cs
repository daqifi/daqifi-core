using System;

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Event args for device discovered events.
/// </summary>
public class DeviceDiscoveredEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the DeviceDiscoveredEventArgs class.
    /// </summary>
    /// <param name="deviceInfo">The discovered device information.</param>
    public DeviceDiscoveredEventArgs(IDeviceInfo deviceInfo)
    {
        DeviceInfo = deviceInfo ?? throw new ArgumentNullException(nameof(deviceInfo));
    }

    /// <summary>
    /// Gets the discovered device information.
    /// </summary>
    public IDeviceInfo DeviceInfo { get; }
}
