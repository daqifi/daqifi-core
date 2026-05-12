namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Known USB Vendor / Product identifiers for DAQiFi devices in normal
/// (non-bootloader) operating mode. Used by <see cref="SerialDeviceFinder"/>
/// to filter serial ports before probing — only ports matching one of these
/// IDs are opened, eliminating accidental SCPI traffic to other vendors'
/// COM ports (Bluetooth radios, GPS receivers, Arduinos, etc.).
/// </summary>
public static class DaqifiUsbIds
{
    /// <summary>
    /// USB vendor ID assigned by Microchip and used by DAQiFi devices
    /// (PIC32-based). Same VID is used in bootloader mode.
    /// </summary>
    public const int VendorId = 0x04D8;

    /// <summary>
    /// USB product ID for DAQiFi devices in normal USB CDC serial mode
    /// (Nyquist1, Nyquist3). Bootloader mode uses a different PID
    /// (<c>0x003C</c>, see <c>FirmwareUpdateServiceOptions.BootloaderProductId</c>).
    /// </summary>
    public const int CdcProductId = 0xF794;

    /// <summary>
    /// Returns true if the supplied descriptor matches a known DAQiFi USB
    /// CDC serial-mode device (matches <see cref="VendorId"/> and one of
    /// the known product IDs).
    /// </summary>
    public static bool IsDaqifiCdcDevice(UsbPortDescriptor descriptor)
    {
        return descriptor.VendorId == VendorId
            && descriptor.ProductId == CdcProductId;
    }
}
