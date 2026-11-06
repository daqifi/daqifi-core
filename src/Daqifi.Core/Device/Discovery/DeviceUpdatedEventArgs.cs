using System;

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Event args raised when a device already in the live set maintained by
/// <see cref="ContinuousDeviceFinder"/> comes back from a discovery pass with different
/// metadata — a new port name after a replug, a new IP address from the router, a new
/// firmware version after a flash, a new power state.
/// </summary>
/// <remarks>
/// The device neither arrived nor went away, so neither
/// <see cref="ContinuousDeviceFinder.DeviceDiscovered"/> nor
/// <see cref="ContinuousDeviceFinder.DeviceLost"/> describes it, and
/// <see cref="ContinuousDeviceFinder.DeviceDiscovered"/> fires only once per device until it is
/// lost and rediscovered. <see cref="PreviousDeviceInfo"/> is carried alongside
/// <see cref="DeviceInfo"/> so a consumer holding a row keyed by something that just changed —
/// a port name, say — can still find the row this update belongs to (#725).
/// </remarks>
public class DeviceUpdatedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceUpdatedEventArgs"/> class.
    /// </summary>
    /// <param name="previousDeviceInfo">The metadata the live set held before this pass.</param>
    /// <param name="deviceInfo">The metadata the pass returned.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public DeviceUpdatedEventArgs(IDeviceInfo previousDeviceInfo, IDeviceInfo deviceInfo)
    {
        PreviousDeviceInfo = previousDeviceInfo ?? throw new ArgumentNullException(nameof(previousDeviceInfo));
        DeviceInfo = deviceInfo ?? throw new ArgumentNullException(nameof(deviceInfo));
    }

    /// <summary>
    /// Gets the metadata the live set held for this device before the pass that raised the
    /// event. This is what a consumer's own copy of the device still says.
    /// </summary>
    public IDeviceInfo PreviousDeviceInfo { get; }

    /// <summary>
    /// Gets the metadata the pass returned, which is now what
    /// <see cref="ContinuousDeviceFinder.Devices"/> holds for this device.
    /// </summary>
    public IDeviceInfo DeviceInfo { get; }
}
