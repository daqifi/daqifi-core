using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Resolves a USB physical-location key via WMI's <c>DEVPKEY_Device_LocationInfo</c> device
/// property (e.g. <c>Port_#0001.Hub_#0001</c>) — a topology-derived string Windows computes
/// from physical bus/port position, independent of which driver/class is bound to the node.
/// Uses <see cref="System.Management"/>; returns null on non-Windows platforms so callers can
/// fall back to a no-correlation strategy.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsUsbLocationProvider : IUsbLocationProvider
{
    private const string LocationInfoKeyName = "DEVPKEY_Device_LocationInfo";

    // SerialPort.GetPortNames() returns "COM<n>" on Windows; validated before
    // interpolation into the WQL query string below.
    private static readonly Regex PortNameRegex = new(
        @"^COM\d+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A parsed PnP instance ID is composed of hex/word segments separated by backslashes
    // (e.g. "HID\VID_04D8&PID_003C\7&1A2B3C4D&0&0000"). Validated before interpolation into
    // a WQL query string so a malformed device path can never inject WQL syntax.
    private static readonly Regex InstanceIdRegex = new(
        @"^[A-Z0-9_&]+(\\[A-Z0-9_&]+)+$",
        RegexOptions.Compiled);

    public string? GetLocationKey(string portNameOrDevicePath)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(portNameOrDevicePath))
        {
            return null;
        }

        try
        {
            if (PortNameRegex.IsMatch(portNameOrDevicePath))
            {
                return QueryLocationByCaption(portNameOrDevicePath);
            }

            var instanceId = HidDevicePathParser.ParseInstanceId(portNameOrDevicePath);
            return instanceId != null ? QueryLocationByDeviceId(instanceId) : null;
        }
        catch
        {
            // Any WMI/interop error → behave like the no-op provider for this input.
            // The caller falls through to "no known location", which is correct for
            // any input we can't resolve.
            return null;
        }
    }

    private static string? QueryLocationByCaption(string portName)
    {
        // Match COM-port entities by Caption suffix, e.g. "USB Serial Device (COM9)",
        // same query shape as WindowsUsbPortDescriptorProvider.
        using var searcher = new System.Management.ManagementObjectSearcher(
            $"SELECT DeviceID FROM Win32_PnPEntity WHERE PNPClass = 'Ports' AND Caption LIKE '%({portName})%'");
        return FindLocationInfo(searcher);
    }

    private static string? QueryLocationByDeviceId(string instanceId)
    {
        if (!InstanceIdRegex.IsMatch(instanceId))
        {
            return null;
        }

        // WQL requires backslashes in string literals to be escaped as "\\".
        var escaped = instanceId.Replace(@"\", @"\\", StringComparison.Ordinal);
        using var searcher = new System.Management.ManagementObjectSearcher(
            $"SELECT DeviceID FROM Win32_PnPEntity WHERE DeviceID = '{escaped}'");
        return FindLocationInfo(searcher);
    }

    private static string? FindLocationInfo(System.Management.ManagementObjectSearcher searcher)
    {
        using var results = searcher.Get();
        foreach (var entity in results)
        {
            using (entity)
            {
                if (entity is System.Management.ManagementObject managementObject)
                {
                    var location = GetLocationInfoProperty(managementObject);
                    if (location != null)
                    {
                        return location;
                    }
                }
            }
        }

        return null;
    }

    private static string? GetLocationInfoProperty(System.Management.ManagementObject entity)
    {
        using var inParams = entity.GetMethodParameters("GetDeviceProperties");
        inParams["devicePropertyKeys"] = new[] { LocationInfoKeyName };

        using var outParams = entity.InvokeMethod("GetDeviceProperties", inParams, null);
        if (outParams?["deviceProperties"] is not System.Management.ManagementBaseObject[] deviceProperties)
        {
            return null;
        }

        foreach (var property in deviceProperties)
        {
            using (property)
            {
                var keyName = property["KeyName"] as string;
                if (!string.Equals(keyName, LocationInfoKeyName, StringComparison.Ordinal))
                {
                    continue;
                }

                return property["Data"] as string;
            }
        }

        return null;
    }
}
