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
    /// <exception cref="LanNotInitializedException">
    /// The device reported SCPI <c>-200</c> ("Execution error") — its WiFi module is
    /// enabled in saved settings but its state machine has not been initialized yet.
    /// </exception>
    Task<LanChipInfo?> GetLanChipInfoAsync(CancellationToken cancellationToken = default);
}
