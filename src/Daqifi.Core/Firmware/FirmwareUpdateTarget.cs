namespace Daqifi.Core.Firmware;

/// <summary>
/// Names the board a PIC32 firmware update is for, when more than one could be in its bootloader
/// (they share a VID/PID and have no serial number to tell them apart).
/// </summary>
/// <remarks>
/// Set whichever identifier is known. <see cref="DevicePath"/> is for a board that is already in
/// its bootloader; <see cref="LocationKey"/> is for one that is still running its application, whose
/// bootloader device path does not exist yet. When both are set, <see cref="DevicePath"/> decides and
/// <see cref="LocationKey"/> is ignored. A target with neither set names no board, the same as
/// passing no target.
/// </remarks>
public sealed record FirmwareUpdateTarget
{
    /// <summary>
    /// The HID device path of the bootloader to flash (from discovery's
    /// <c>HidDeviceInfo.DevicePath</c>). Must not be empty or whitespace.
    /// </summary>
    public string? DevicePath { get; init; }

    /// <summary>
    /// The USB physical-location key of the board to flash (from discovery's
    /// <c>IDeviceInfo.LocationKey</c>, read before the board rebooted into its bootloader). Must not
    /// be empty or whitespace.
    /// </summary>
    public string? LocationKey { get; init; }
}
