namespace Daqifi.Core.Firmware;

/// <summary>
/// Optional capability for querying the WiFi LAN chip information from a device.
/// Implement this interface on a device class to enable WiFi firmware version checks
/// before flashing.
/// </summary>
public interface ILanChipInfoProvider
{
    /// <summary>
    /// Queries the device for its current WiFi module chip information.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The parsed <see cref="LanChipInfo"/>, or <see langword="null"/> if the device
    /// did not return a recognizable response.
    /// </returns>
    Task<LanChipInfo?> GetLanChipInfoAsync(CancellationToken cancellationToken = default);
}
