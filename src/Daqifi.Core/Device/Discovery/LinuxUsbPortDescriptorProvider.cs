using System.Globalization;
using System.Runtime.InteropServices;

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Resolves USB VID/PID for serial ports via Linux <c>/sys/class/tty/</c>
/// sysfs entries. Returns null on non-Linux platforms or for ports whose
/// sysfs lookup fails (non-USB serial, virtual ttys, etc.).
/// </summary>
internal sealed class LinuxUsbPortDescriptorProvider : IUsbPortDescriptorProvider
{
    public UsbPortDescriptor? GetDescriptor(string portName)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return null;
        }

        // portName is typically /dev/ttyACM0 or /dev/ttyUSB0.
        // The corresponding sysfs path is /sys/class/tty/<base>/device/...
        // We walk up the device tree looking for idVendor + idProduct,
        // which sit on the USB device node (a few levels above the tty).
        var baseName = System.IO.Path.GetFileName(portName);
        if (string.IsNullOrEmpty(baseName))
            return null;

        var sysfsRoot = $"/sys/class/tty/{baseName}/device";
        if (!System.IO.Directory.Exists(sysfsRoot))
            return null;

        // Walk up the symlink-resolved path looking for idVendor/idProduct.
        // Bound the depth to keep this defensive against unexpected layouts.
        try
        {
            // /sys/class/tty/<base>/device is a symlink into the actual USB
            // device tree (e.g. /sys/devices/pci.../usb1/.../1-1.2). Walking
            // parents of the unresolved logical path lands back in /sys/class
            // and never reaches the node that holds idVendor/idProduct, so
            // resolve to the physical target before traversal.
            var dirInfo = new System.IO.DirectoryInfo(sysfsRoot);
            var resolved = dirInfo.ResolveLinkTarget(returnFinalTarget: true);
            var current = (resolved ?? dirInfo).FullName;
            for (var i = 0; i < 8; i++)
            {
                var vendorPath = System.IO.Path.Combine(current, "idVendor");
                var productPath = System.IO.Path.Combine(current, "idProduct");
                if (System.IO.File.Exists(vendorPath) && System.IO.File.Exists(productPath))
                {
                    var vidText = System.IO.File.ReadAllText(vendorPath).Trim();
                    var pidText = System.IO.File.ReadAllText(productPath).Trim();
                    if (int.TryParse(vidText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var vid) &&
                        int.TryParse(pidText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var pid))
                    {
                        return new UsbPortDescriptor(vid, pid);
                    }
                    return null;
                }

                var parent = System.IO.Directory.GetParent(current);
                if (parent == null || parent.FullName == current)
                    break;
                current = parent.FullName;
            }
        }
        catch
        {
            // Permission denied / IO error → fall through to null.
        }

        return null;
    }
}
