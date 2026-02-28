namespace Daqifi.Core.Firmware;

/// <summary>
/// WiFi LAN chip information returned by the <c>SYSTem:COMMunicate:LAN:GETChipInfo?</c> SCPI query.
/// </summary>
public sealed record LanChipInfo
{
    /// <summary>
    /// Gets the chip hardware identifier.
    /// </summary>
    public required int ChipId { get; init; }

    /// <summary>
    /// Gets the WiFi module firmware version string (e.g. "19.5.4").
    /// </summary>
    public required string FwVersion { get; init; }

    /// <summary>
    /// Gets the firmware build date string.
    /// </summary>
    public required string BuildDate { get; init; }
}
