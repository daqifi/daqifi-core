namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// A port that was present during a discovery pass but could not be opened, together with the
/// USB physical location behind it when that could be resolved.
/// </summary>
/// <param name="PortName">The OS port name, e.g. <c>COM3</c> or <c>/dev/ttyACM0</c>.</param>
/// <param name="LocationKey">
/// The USB physical-location key for that port, or <see langword="null"/> when the platform
/// could not resolve one.
/// </param>
/// <remarks>
/// The location is what makes the name trustworthy. An OS port name is a lease, not an identity:
/// unplug a device and the next one along can be handed the same name. Matching a tracked device
/// to a busy port on the name alone would then keep a device that has genuinely gone reported as
/// present, for as long as its old name stayed occupied — the failure the busy-port rescue exists
/// to avoid causing. The location key is resolved without opening the port, so it is available on
/// exactly the path where the port cannot be opened.
/// </remarks>
internal readonly record struct BusyPort(string PortName, string? LocationKey);
