namespace Daqifi.Core.Device.Diagnostics;

/// <summary>
/// The result of applying a runtime log level, echoed by the device from a
/// <c>SYSTem:LOG:LEVel module,level</c> command.
/// </summary>
/// <remarks>
/// The <see cref="Level"/> actually applied can be lower than the requested value when the module's
/// compile-time <see cref="Ceiling"/> caps it. Both are the device's own numbers: a level newer than
/// the ones <see cref="DeviceLogLevel"/> names arrives as that number rather than being dropped.
/// </remarks>
public sealed record LogLevelSetting
{
    /// <summary>
    /// Gets the module name the level was applied to (e.g. <c>STREAM</c>), as reported by the device.
    /// </summary>
    public required string Module { get; init; }

    /// <summary>
    /// Gets the log level now in effect for the module.
    /// </summary>
    public required DeviceLogLevel Level { get; init; }

    /// <summary>
    /// Gets the compile-time ceiling for the module. The effective <see cref="Level"/> can never
    /// exceed this value.
    /// </summary>
    public required DeviceLogLevel Ceiling { get; init; }
}
