using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Detects USB Vendor ID and Product ID for serial ports using platform-specific APIs.
/// Used to filter serial ports before probing, avoiding sending SCPI commands to non-DAQiFi devices.
/// </summary>
internal static class SerialPortUsbDetector
{
    /// <summary>
    /// Known DAQiFi USB Vendor ID (Microchip Technology Inc.).
    /// Shared with <see cref="HidDeviceFinder.DefaultVendorId"/> for bootloader mode.
    /// </summary>
    internal const int DaqifiVendorId = 0x04D8;

    /// <summary>
    /// Represents USB identification for a serial port.
    /// </summary>
    internal sealed record UsbId(int VendorId, int ProductId);

    /// <summary>
    /// Gets USB VID/PID information for serial ports on the system.
    /// Returns a dictionary mapping port names to their USB IDs (null values not included).
    /// Returns empty dictionary if detection is unavailable or fails.
    /// </summary>
    internal static Dictionary<string, UsbId> GetPortUsbInfo()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return GetPortUsbInfoWindows();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return GetPortUsbInfoMacOS();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return GetPortUsbInfoLinux();
        }
        catch
        {
            // Platform detection failed — caller will probe all ports
        }

        return new Dictionary<string, UsbId>();
    }

    /// <summary>
    /// Checks whether the given USB Vendor ID matches a known DAQiFi vendor.
    /// </summary>
    internal static bool IsDaqifiVendor(int vendorId) => vendorId == DaqifiVendorId;

    #region Windows

    [SupportedOSPlatform("windows")]
    private static Dictionary<string, UsbId> GetPortUsbInfoWindows()
    {
        var result = new Dictionary<string, UsbId>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // Enumerate USB devices in the registry to find COM port mappings
            // Path: HKLM\SYSTEM\CurrentControlSet\Enum\USB\VID_xxxx&PID_xxxx\{serial}\Device Parameters\PortName
            using var usbKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\USB");
            if (usbKey == null) return result;

            foreach (var vidPidKeyName in usbKey.GetSubKeyNames())
            {
                var match = Regex.Match(vidPidKeyName,
                    @"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase);
                if (!match.Success) continue;

                var vid = Convert.ToInt32(match.Groups[1].Value, 16);
                var pid = Convert.ToInt32(match.Groups[2].Value, 16);

                using var vidPidKey = usbKey.OpenSubKey(vidPidKeyName);
                if (vidPidKey == null) continue;

                foreach (var instanceKeyName in vidPidKey.GetSubKeyNames())
                {
                    using var deviceParamsKey = vidPidKey.OpenSubKey($@"{instanceKeyName}\Device Parameters");
                    var portName = deviceParamsKey?.GetValue("PortName") as string;
                    if (!string.IsNullOrEmpty(portName))
                    {
                        result[portName] = new UsbId(vid, pid);
                    }
                }
            }
        }
        catch
        {
            // Registry access failed — return what we have
        }

        return result;
    }

    #endregion

    #region macOS

    private static Dictionary<string, UsbId> GetPortUsbInfoMacOS()
    {
        // Try AppleUSBDevice first (most macOS versions), fall back to IOUSBHostDevice
        var result = ParseIoregUsbSerialPorts("AppleUSBDevice");
        if (result.Count == 0)
            result = ParseIoregUsbSerialPorts("IOUSBHostDevice");
        return result;
    }

    /// <summary>
    /// Parses ioreg output for USB devices of the specified class to find serial port mappings.
    /// The output from <c>ioreg -r -c {className} -l -w0</c> lists each USB device and its descendants,
    /// including any IOSerialBSDClient entries that contain IOCalloutDevice paths.
    /// </summary>
    private static Dictionary<string, UsbId> ParseIoregUsbSerialPorts(string usbClassName)
    {
        var result = new Dictionary<string, UsbId>();

        var output = RunProcess("/usr/sbin/ioreg", $"-r -c {usbClassName} -l -w0");
        if (string.IsNullOrEmpty(output)) return result;

        // Walk through the ioreg output tracking the current USB device's VID/PID.
        // USB device properties (idVendor, idProduct) appear before any descendant
        // IOSerialBSDClient entries containing IOCalloutDevice.
        // When a new top-level USB device is encountered, VID/PID is naturally overwritten.
        int? currentVid = null;
        int? currentPid = null;

        foreach (var line in output.Split('\n'))
        {
            var vidMatch = Regex.Match(line, @"""idVendor""\s*=\s*(\d+)");
            if (vidMatch.Success)
            {
                currentVid = int.Parse(vidMatch.Groups[1].Value);
                continue;
            }

            var pidMatch = Regex.Match(line, @"""idProduct""\s*=\s*(\d+)");
            if (pidMatch.Success)
            {
                currentPid = int.Parse(pidMatch.Groups[1].Value);
                continue;
            }

            var portMatch = Regex.Match(line, @"""IOCalloutDevice""\s*=\s*""([^""]+)""");
            if (portMatch.Success && currentVid.HasValue && currentPid.HasValue)
            {
                result[portMatch.Groups[1].Value] = new UsbId(currentVid.Value, currentPid.Value);
            }
        }

        return result;
    }

    #endregion

    #region Linux

    private static Dictionary<string, UsbId> GetPortUsbInfoLinux()
    {
        var result = new Dictionary<string, UsbId>();

        try
        {
            // Read USB VID/PID from sysfs for each tty device
            var ttyDir = "/sys/class/tty";
            if (!Directory.Exists(ttyDir)) return result;

            foreach (var ttyPath in Directory.GetDirectories(ttyDir))
            {
                var ttyName = Path.GetFileName(ttyPath);
                var deviceLink = Path.Combine(ttyPath, "device");
                if (!Directory.Exists(deviceLink)) continue;

                // Walk up the sysfs tree to find the USB device with idVendor/idProduct
                var usbDevicePath = FindUsbParent(deviceLink);
                if (usbDevicePath == null) continue;

                var vidFile = Path.Combine(usbDevicePath, "idVendor");
                var pidFile = Path.Combine(usbDevicePath, "idProduct");

                if (!File.Exists(vidFile) || !File.Exists(pidFile)) continue;

                var vidStr = File.ReadAllText(vidFile).Trim();
                var pidStr = File.ReadAllText(pidFile).Trim();

                if (int.TryParse(vidStr, System.Globalization.NumberStyles.HexNumber, null, out var vid) &&
                    int.TryParse(pidStr, System.Globalization.NumberStyles.HexNumber, null, out var pid))
                {
                    result[$"/dev/{ttyName}"] = new UsbId(vid, pid);
                }
            }
        }
        catch
        {
            // sysfs access failed — return what we have
        }

        return result;
    }

    private static string? FindUsbParent(string devicePath)
    {
        var current = Path.GetFullPath(devicePath);

        for (var i = 0; i < 10; i++)
        {
            if (File.Exists(Path.Combine(current, "idVendor")))
                return current;

            var parent = Path.GetDirectoryName(current);
            if (parent == null || parent == current)
                break;

            current = parent;
        }

        return null;
    }

    #endregion

    #region Helpers

    private static string RunProcess(string fileName, string arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return output;
        }
        catch
        {
            return string.Empty;
        }
    }

    #endregion
}
