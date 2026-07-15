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
    private const string ParentKeyName = "DEVPKEY_Device_Parent";

    // SerialPort.GetPortNames() returns "COM<n>" on Windows; validated before
    // interpolation into the WQL query string below.
    private static readonly Regex PortNameRegex = new(
        @"^COM\d+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
        var deviceId = FindDeviceId(searcher);
        return deviceId != null ? LocationParentWalker.Resolve(deviceId, QueryLocationAndParent) : null;
    }

    private static string? QueryLocationByDeviceId(string instanceId)
    {
        return LocationParentWalker.InstanceIdRegex.IsMatch(instanceId)
            ? LocationParentWalker.Resolve(instanceId, QueryLocationAndParent)
            : null;
    }

    private static (string? Location, string? ParentId) QueryLocationAndParent(string instanceId)
    {
        // WQL requires backslashes in string literals to be escaped as "\\".
        var escaped = instanceId.Replace(@"\", @"\\", StringComparison.Ordinal);
        using var searcher = new System.Management.ManagementObjectSearcher(
            $"SELECT DeviceID FROM Win32_PnPEntity WHERE DeviceID = '{escaped}'");
        using var results = searcher.Get();
        foreach (var entity in results)
        {
            using (entity)
            {
                if (entity is System.Management.ManagementObject managementObject)
                {
                    return GetDeviceProperties(managementObject);
                }
            }
        }

        return (null, null);
    }

    private static string? FindDeviceId(System.Management.ManagementObjectSearcher searcher)
    {
        using var results = searcher.Get();
        foreach (var entity in results)
        {
            using (entity)
            {
                return entity["DeviceID"] as string;
            }
        }

        return null;
    }

    private static (string? Location, string? ParentId) GetDeviceProperties(System.Management.ManagementObject entity)
    {
        using var inParams = entity.GetMethodParameters("GetDeviceProperties");
        inParams["devicePropertyKeys"] = new[] { LocationInfoKeyName, ParentKeyName };

        using var outParams = entity.InvokeMethod("GetDeviceProperties", inParams, null);
        if (outParams?["deviceProperties"] is not System.Management.ManagementBaseObject[] deviceProperties)
        {
            return (null, null);
        }

        string? location = null;
        string? parentId = null;
        foreach (var property in deviceProperties)
        {
            using (property)
            {
                // A key WMI couldn't resolve on this node comes back as a property object whose
                // "Type"/"KeyName"/"Data" members throw ManagementException("Not found") on access
                // instead of yielding null — requesting the LocationInfo and Parent keys together
                // means one can legitimately be absent (a HID collection's own node never has
                // LocationInfo) while the other resolves, so each read must tolerate that per-entry.
                var keyName = TryGetStringProperty(property, "KeyName");
                if (keyName == null)
                {
                    continue;
                }

                if (string.Equals(keyName, LocationInfoKeyName, StringComparison.Ordinal))
                {
                    location = TryGetStringProperty(property, "Data");
                }
                else if (string.Equals(keyName, ParentKeyName, StringComparison.Ordinal))
                {
                    parentId = TryGetStringProperty(property, "Data");
                }
            }
        }

        return (location, parentId);
    }

    private static string? TryGetStringProperty(System.Management.ManagementBaseObject obj, string propertyName)
    {
        try
        {
            return obj[propertyName] as string;
        }
        catch (System.Management.ManagementException)
        {
            return null;
        }
    }
}
