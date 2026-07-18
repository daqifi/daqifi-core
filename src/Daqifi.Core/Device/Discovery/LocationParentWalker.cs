using System.Text.RegularExpressions;

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Walks a PnP device's parent chain looking for a resolved USB physical-location value. Kept
/// separate from <see cref="WindowsUsbLocationProvider"/> (which is Windows-only because it calls
/// into <see cref="System.Management"/>) so this pure decision logic — when to stop, how deep to
/// go, and how to normalize a parent instance ID before re-querying it — can be unit tested on any
/// platform with a fake query function, without WMI/hardware access.
/// </summary>
internal static class LocationParentWalker
{
    // A HID collection's own PnP node never carries DEVPKEY_Device_LocationInfo — only the
    // physical USB device node one level up in the device tree does (confirmed on real hardware:
    // a bootloader's HID node returns no LocationInfo property at all, while its
    // DEVPKEY_Device_Parent node returns the same value the same physical port reports in
    // serial/CDC mode). Bounded so a pathological/cyclic parent chain can't spin forever; the
    // real device tree is 1 hop away.
    internal const int MaxParentHops = 4;

    // A parsed PnP instance ID is composed of hex/word segments separated by backslashes
    // (e.g. "HID\VID_04D8&PID_003C\7&1A2B3C4D&0&0000"). Validated before a caller interpolates
    // it into a WQL query string so a malformed/unexpected value can never inject WQL syntax.
    internal static readonly Regex InstanceIdRegex = new(
        @"^[A-Z0-9_&]+(\\[A-Z0-9_&]+)+$",
        RegexOptions.Compiled);

    /// <summary>
    /// Repeatedly calls <paramref name="query"/> for <paramref name="instanceId"/> and then each
    /// resolved parent, returning the first non-empty location found, or null if none is found
    /// within <see cref="MaxParentHops"/> hops, the chain ends, or a parent ID doesn't look like a
    /// valid PnP instance ID.
    /// </summary>
    /// <param name="instanceId">Starting PnP instance ID; must already match <see cref="InstanceIdRegex"/>.</param>
    /// <param name="query">
    /// Resolves a PnP instance ID to its location (or null/empty if unresolved) and its parent's
    /// instance ID (or null if it has none / couldn't be resolved).
    /// </param>
    internal static string? Resolve(string instanceId, Func<string, (string? Location, string? ParentId)> query)
    {
        var currentId = instanceId;
        for (var hop = 0; hop <= MaxParentHops; hop++)
        {
            var (location, parentId) = query(currentId);
            if (!string.IsNullOrEmpty(location))
            {
                return location;
            }

            // WMI's DEVPKEY_Device_Parent value case doesn't match the uppercase convention this
            // module normalizes instance IDs to — normalize before validating/re-querying, or a
            // real parent chain segment with lowercase hex fails the regex and the walk stops one
            // hop too early.
            var normalizedParentId = parentId?.ToUpperInvariant();
            if (normalizedParentId == null || !InstanceIdRegex.IsMatch(normalizedParentId))
            {
                return null;
            }

            currentId = normalizedParentId;
        }

        return null;
    }
}
