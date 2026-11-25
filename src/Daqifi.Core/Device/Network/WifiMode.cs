using System.ComponentModel;

namespace Daqifi.Core.Device.Network;

/// <summary>
/// Specifies the WiFi operating mode for the device.
/// </summary>
/// <remarks>
/// The values are the ones the device sends and accepts, so neither is zero and
/// <c>default(WifiMode)</c> is not a mode: sending it throws
/// <see cref="System.ArgumentOutOfRangeException"/>. Where the mode may be unknown it is a
/// <c>WifiMode?</c>, and <see langword="null"/> is the unknown.
/// </remarks>
public enum WifiMode
{
    /// <summary>
    /// Device connects to an existing WiFi network as a client.
    /// </summary>
    [Description("Existing Network")]
    ExistingNetwork = 1,

    /// <summary>
    /// Device creates its own WiFi network as an access point.
    /// </summary>
    [Description("Self Hosted")]
    SelfHosted = 4
}
