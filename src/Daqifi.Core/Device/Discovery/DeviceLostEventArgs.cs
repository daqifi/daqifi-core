using System;

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Event args raised when a previously discovered device disappears from the
/// live set maintained by <see cref="ContinuousDeviceFinder"/>.
/// </summary>
public class DeviceLostEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceLostEventArgs"/> class.
    /// </summary>
    /// <param name="deviceInfo">The device that was lost.</param>
    public DeviceLostEventArgs(IDeviceInfo deviceInfo)
    {
        DeviceInfo = deviceInfo ?? throw new ArgumentNullException(nameof(deviceInfo));
    }

    /// <summary>
    /// Gets the device that disappeared from the live set. This is the most
    /// recent metadata observed for the device before it was lost.
    /// </summary>
    public IDeviceInfo DeviceInfo { get; }
}
