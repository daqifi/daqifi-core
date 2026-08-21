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
    /// <summary>
    /// The sysfs directory the kernel gives one entry per tty, each with a <c>device</c> symlink
    /// into the physical device tree under <c>/sys/devices</c>.
    /// </summary>
    internal const string DefaultTtyClassRoot = "/sys/class/tty";

    /// <summary>
    /// How many levels of the device tree to examine: the tty's own device node plus its seven
    /// nearest ancestors. The USB device node that holds <c>idVendor</c>/<c>idProduct</c> is only
    /// a few levels up, so the bound stops an unexpected layout from walking to the filesystem
    /// root rather than limiting any real device.
    /// </summary>
    internal const int MaxDeviceTreeLevels = 8;

    public UsbPortDescriptor? GetDescriptor(string portName)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return null;
        }

        return Resolve(portName, DefaultTtyClassRoot);
    }

    /// <summary>
    /// Resolves the descriptor for <paramref name="portName"/> against the tty class directory
    /// <paramref name="ttyClassRoot"/>, or returns null when the port has no sysfs entry, is not
    /// USB-attached, or the layout holds no readable VID/PID.
    /// </summary>
    /// <remarks>
    /// Split out from <see cref="GetDescriptor"/> — which adds the Linux platform gate and supplies
    /// the real <see cref="DefaultTtyClassRoot"/> — so the walk can be unit tested against a
    /// fixture tree on any OS, for the same reason
    /// <see cref="MacOsUsbPortDescriptorProvider.Parse"/> is exposed.
    /// </remarks>
    internal static UsbPortDescriptor? Resolve(string portName, string ttyClassRoot)
    {
        // portName is typically /dev/ttyACM0 or /dev/ttyUSB0.
        // The corresponding sysfs path is /sys/class/tty/<base>/device/...
        // We walk up the device tree looking for idVendor + idProduct,
        // which sit on the USB device node (a few levels above the tty).
        var baseName = System.IO.Path.GetFileName(portName);
        if (string.IsNullOrEmpty(baseName))
            return null;

        var sysfsRoot = System.IO.Path.Combine(ttyClassRoot, baseName, "device");
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
            for (var i = 0; i < MaxDeviceTreeLevels; i++)
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
