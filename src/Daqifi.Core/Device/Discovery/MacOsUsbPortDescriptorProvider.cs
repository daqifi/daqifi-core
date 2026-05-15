using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Resolves USB VID/PID for serial ports via <c>ioreg</c> on macOS.
/// Runs ioreg at most once per <see cref="CacheTtlMs"/> window so a single
/// discovery pass (many <see cref="GetDescriptor"/> calls within ~1s) pays
/// one ioreg invocation, while back-to-back passes always see fresh device
/// state (a device plugged in between passes is detected on the next pass).
/// </summary>
internal sealed class MacOsUsbPortDescriptorProvider : IUsbPortDescriptorProvider
{
    private const int IoregTimeoutMs = 5000;

    // One ioreg invocation covers a full discovery pass (all ports probed
    // within ~1.2s), but a subsequent pass always fetches fresh state.
    // This matches the per-call freshness of the Windows/Linux providers
    // while avoiding N redundant ioreg invocations per pass.
    private const int CacheTtlMs = 2000;

    private static readonly object CacheLock = new();
    private static Dictionary<string, UsbPortDescriptor> _cachedMap = new();
    private static long _cacheExpiresAtMs;

    public UsbPortDescriptor? GetDescriptor(string portName)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return null;

        var map = GetOrRefreshMap();
        map.TryGetValue(portName, out var descriptor);
        return descriptor;
    }

    private static Dictionary<string, UsbPortDescriptor> GetOrRefreshMap()
    {
        var nowMs = Environment.TickCount64;
        lock (CacheLock)
        {
            if (nowMs < _cacheExpiresAtMs)
                return _cachedMap;

            _cachedMap = BuildDescriptorMap();
            _cacheExpiresAtMs = nowMs + CacheTtlMs;
            return _cachedMap;
        }
    }

    private static Dictionary<string, UsbPortDescriptor> BuildDescriptorMap()
    {
        // AppleUSBDevice covers most macOS versions; IOUSBHostDevice is used
        // on newer kernel releases. Try both and take the first non-empty result.
        var result = Parse(RunIoreg("AppleUSBDevice"));
        if (result.Count == 0)
            result = Parse(RunIoreg("IOUSBHostDevice"));
        return result;
    }

    /// <summary>
    /// Parses the stdout of <c>ioreg -r -c {usbClassName} -l -w0</c> into a
    /// port-name → VID/PID map. Exposed internally for unit testing without
    /// spawning a real process.
    /// </summary>
    internal static Dictionary<string, UsbPortDescriptor> Parse(string ioregOutput)
    {
        var result = new Dictionary<string, UsbPortDescriptor>();
        if (string.IsNullOrEmpty(ioregOutput))
            return result;

        // ioreg -r prints each top-level USB device followed by its descendants.
        // idVendor / idProduct appear as properties on the USB device node;
        // IOCalloutDevice appears inside a child IOSerialBSDClient. Tracking the
        // most-recently-seen VID/PID and associating it with each callout device
        // works because ioreg always prints a node's own properties before those
        // of its children.
        int? currentVid = null;
        int? currentPid = null;

        foreach (var line in ioregOutput.Split('\n'))
        {
            var vidMatch = Regex.Match(line, @"""idVendor""\s*=\s*(\d+)");
            if (vidMatch.Success)
            {
                currentVid = int.Parse(vidMatch.Groups[1].Value,
                    System.Globalization.CultureInfo.InvariantCulture);
                continue;
            }

            var pidMatch = Regex.Match(line, @"""idProduct""\s*=\s*(\d+)");
            if (pidMatch.Success)
            {
                currentPid = int.Parse(pidMatch.Groups[1].Value,
                    System.Globalization.CultureInfo.InvariantCulture);
                continue;
            }

            var portMatch = Regex.Match(line, @"""IOCalloutDevice""\s*=\s*""([^""]+)""");
            if (portMatch.Success && currentVid.HasValue && currentPid.HasValue)
            {
                result[portMatch.Groups[1].Value] = new UsbPortDescriptor(currentVid.Value, currentPid.Value);
            }
        }

        return result;
    }

    private static string RunIoreg(string usbClassName)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/usr/sbin/ioreg",
                    Arguments = $"-r -c {usbClassName} -l -w0",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();

            // Read stdout asynchronously so WaitForExit can enforce the timeout.
            // Calling ReadToEnd() before WaitForExit() deadlocks if the output
            // buffer fills before the process exits.
            var readTask = process.StandardOutput.ReadToEndAsync();
            if (process.WaitForExit(IoregTimeoutMs) && readTask.Wait(IoregTimeoutMs))
                return readTask.Result;

            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
