namespace Daqifi.Core.Device.Diagnostics;

/// <summary>
/// How much a firmware module writes to the device's system log. The values are the ones the device
/// sends and accepts.
/// </summary>
public enum DeviceLogLevel
{
    /// <summary>
    /// Nothing is logged.
    /// </summary>
    None = 0,

    /// <summary>
    /// Errors only.
    /// </summary>
    Error = 1,

    /// <summary>
    /// Errors, state changes and significant events.
    /// </summary>
    Info = 2,

    /// <summary>
    /// Everything, including verbose diagnostics.
    /// </summary>
    Debug = 3
}
