namespace Daqifi.Core.Communication.Transport;

/// <summary>
/// Metadata for a discovered HID device.
/// </summary>
public sealed class HidDeviceInfo
{
    /// <summary>
    /// Initializes a new HID device info instance.
    /// </summary>
    /// <param name="vendorId">USB vendor ID.</param>
    /// <param name="productId">USB product ID.</param>
    /// <param name="devicePath">Platform-specific HID device path.</param>
    /// <param name="serialNumber">Device serial number, when available.</param>
    /// <param name="productName">Product name, when available.</param>
    public HidDeviceInfo(
        int vendorId,
        int productId,
        string devicePath,
        string? serialNumber = null,
        string? productName = null)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
        {
            throw new ArgumentException("Device path cannot be null or empty.", nameof(devicePath));
        }

        VendorId = vendorId;
        ProductId = productId;
        DevicePath = devicePath;
        SerialNumber = serialNumber;
        ProductName = productName;
    }

    /// <summary>
    /// Gets the USB vendor ID.
    /// </summary>
    public int VendorId { get; }

    /// <summary>
    /// Gets the USB product ID.
    /// </summary>
    public int ProductId { get; }

    /// <summary>
    /// Gets the platform-specific device path.
    /// </summary>
    public string DevicePath { get; }

    /// <summary>
    /// Gets the HID serial number, when available.
    /// </summary>
    public string? SerialNumber { get; }

    /// <summary>
    /// Gets the HID product name, when available.
    /// </summary>
    public string? ProductName { get; }

    /// <summary>
    /// Returns a compact string representation.
    /// </summary>
    public override string ToString()
    {
        var serialPart = string.IsNullOrWhiteSpace(SerialNumber) ? "unknown" : SerialNumber;
        return $"VID=0x{VendorId:X4}, PID=0x{ProductId:X4}, Serial={serialPart}, Path={DevicePath}";
    }
}
