namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Resolves the USB Vendor/Product ID associated with a serial port name
/// (e.g. <c>COM9</c> on Windows, <c>/dev/ttyACM1</c> on Linux). Used by
/// <see cref="SerialDeviceFinder"/> to pre-filter ports before opening
/// them, so non-DAQiFi hardware (Bluetooth radios, GPS receivers, other
/// vendors' COM ports, etc.) is skipped instantly without sending SCPI
/// commands or waiting for a probe timeout.
/// </summary>
/// <remarks>
/// Implementations are platform-specific: Windows uses WMI, Linux reads
/// <c>/sys/class/tty</c>, macOS runs <c>ioreg</c>. Platforms without an
/// implementation fall back to <see cref="NullUsbPortDescriptorProvider"/>
/// which returns null for every port, preserving the legacy "probe every
/// port" behavior.
/// </remarks>
public interface IUsbPortDescriptorProvider
{
    /// <summary>
    /// Returns the USB descriptor for <paramref name="portName"/>, or
    /// <c>null</c> if the port is not USB-attached or the descriptor
    /// cannot be resolved.
    /// </summary>
    UsbPortDescriptor? GetDescriptor(string portName);
}

/// <summary>
/// USB Vendor/Product identification for a serial port.
/// </summary>
/// <param name="VendorId">USB vendor ID (e.g. 0x04D8 for DAQiFi/Microchip).</param>
/// <param name="ProductId">USB product ID (e.g. 0xF794 for DAQiFi USB CDC mode).</param>
public sealed record UsbPortDescriptor(int VendorId, int ProductId);
