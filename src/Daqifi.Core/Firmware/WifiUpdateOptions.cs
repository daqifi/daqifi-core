namespace Daqifi.Core.Firmware;

/// <summary>
/// Options for <see cref="IFirmwareUpdateService.UpdateWifiModuleAsync"/>.
/// </summary>
public sealed record WifiUpdateOptions
{
    /// <summary>
    /// When <c>true</c>, bypasses the internal version probe and always runs the flash flow. Use it
    /// after a separate <see cref="IFirmwareUpdateService.CheckWifiFirmwareStatusAsync"/> call so the
    /// device isn't queried twice (issue #143). The default is <c>false</c>.
    /// </summary>
    public bool SkipVersionCheck { get; init; }
}
