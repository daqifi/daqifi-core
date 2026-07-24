using Daqifi.Core.Device.Discovery;

namespace Daqifi.Core.Device;

/// <summary>
/// Fingerprints a physical DAQiFi unit independently of the transport it was reached over, so the
/// same device seen via USB and via WiFi can be recognized as one unit rather than two.
/// </summary>
/// <remarks>
/// Three discriminators are used, in decreasing order of reliability:
/// <list type="number">
/// <item><description>
/// Serial number — reported by every transport once the device has answered a status message
/// (<see cref="IDeviceInfo.SerialNumber"/> pre-connect, <see cref="DeviceMetadata.SerialNumber"/>
/// post-connect). Compared case-insensitively.
/// </description></item>
/// <item><description>
/// MAC address — network-only, but present before a serial number is known on the WiFi path.
/// Separators are ignored, so <c>AA-BB-CC-DD-EE-FF</c> and <c>aa:bb:cc:dd:ee:ff</c> compare equal.
/// </description></item>
/// <item><description>
/// USB physical-location key (<see cref="IDeviceInfo.LocationKey"/>) — the last resort for
/// identical USB units that report no serial number. Windows-only in v1; null elsewhere.
/// </description></item>
/// </list>
/// Matching walks that list and is decided by the first discriminator that <em>both</em> identities
/// carry: two units with different serial numbers are never the same device even if some later
/// discriminator happens to agree. When no discriminator is populated on both sides the identities
/// simply do not match — an unidentifiable device never collides with another unidentifiable one.
/// </remarks>
public sealed class DeviceIdentity
{
    /// <summary>
    /// An identity with no discriminators at all. Never matches anything, including itself.
    /// </summary>
    public static readonly DeviceIdentity Empty = new(null, null, null);

    private DeviceIdentity(string? serialNumber, string? macAddress, string? locationKey)
    {
        SerialNumber = Normalize(serialNumber);
        MacAddress = Normalize(macAddress);
        LocationKey = Normalize(locationKey);

        Key = SerialNumber != null ? "sn:" + SerialNumber.ToLowerInvariant()
            : MacAddress != null ? "mac:" + NormalizeMac(MacAddress)
            : LocationKey != null ? "loc:" + LocationKey.ToLowerInvariant()
            : string.Empty;
    }

    /// <summary>
    /// Gets the device serial number, or <c>null</c> when the device did not report one.
    /// </summary>
    public string? SerialNumber { get; }

    /// <summary>
    /// Gets the device MAC address, or <c>null</c> when the device did not report one.
    /// </summary>
    public string? MacAddress { get; }

    /// <summary>
    /// Gets the USB physical-location key, or <c>null</c> when it could not be resolved.
    /// </summary>
    public string? LocationKey { get; }

    /// <summary>
    /// Gets a stable key derived from the strongest discriminator this identity carries
    /// (for example <c>sn:1234</c>), or <see cref="string.Empty"/> when it carries none.
    /// Two identities with the same non-empty key always <see cref="Matches"/>.
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// Gets a value indicating whether this identity carries no discriminator at all and can
    /// therefore never be matched against another device.
    /// </summary>
    public bool IsEmpty => Key.Length == 0;

    /// <summary>
    /// Creates an identity from raw discriminators. Null, empty, and whitespace-only values are
    /// all treated as "not reported".
    /// </summary>
    /// <param name="serialNumber">The device serial number, if known.</param>
    /// <param name="macAddress">The device MAC address, if known.</param>
    /// <param name="locationKey">The USB physical-location key, if known.</param>
    /// <returns>The identity described by the supplied discriminators.</returns>
    public static DeviceIdentity Create(string? serialNumber, string? macAddress = null, string? locationKey = null)
        => new(serialNumber, macAddress, locationKey);

    /// <summary>
    /// Creates an identity from discovery metadata — the identity available <em>before</em> a
    /// device is connected.
    /// </summary>
    /// <param name="deviceInfo">The discovered device information.</param>
    /// <returns>The identity described by <paramref name="deviceInfo"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="deviceInfo"/> is <c>null</c>.</exception>
    public static DeviceIdentity FromDiscovery(IDeviceInfo deviceInfo)
    {
        ArgumentNullException.ThrowIfNull(deviceInfo);
        return new DeviceIdentity(deviceInfo.SerialNumber, deviceInfo.MacAddress, deviceInfo.LocationKey);
    }

    /// <summary>
    /// Creates an identity from a connected device's metadata — the identity available
    /// <em>after</em> the device has answered its initial status message. Metadata carries no USB
    /// location key, so pass through the one discovery reported (if any) to keep that discriminator.
    /// </summary>
    /// <param name="metadata">The connected device's metadata.</param>
    /// <param name="locationKey">The USB physical-location key from discovery, if known.</param>
    /// <returns>The identity described by <paramref name="metadata"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="metadata"/> is <c>null</c>.</exception>
    public static DeviceIdentity FromMetadata(DeviceMetadata metadata, string? locationKey = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return new DeviceIdentity(metadata.SerialNumber, metadata.MacAddress, locationKey);
    }

    /// <summary>
    /// Determines whether this identity and <paramref name="other"/> describe the same physical
    /// unit, using the first discriminator both identities carry. Returns <c>false</c> when they
    /// share no populated discriminator, so unidentifiable devices never match each other.
    /// </summary>
    /// <param name="other">The identity to compare against.</param>
    /// <returns><c>true</c> when both identities describe the same physical device.</returns>
    public bool Matches(DeviceIdentity? other)
    {
        if (other == null)
        {
            return false;
        }

        if (SerialNumber != null && other.SerialNumber != null)
        {
            return SerialNumber.Equals(other.SerialNumber, StringComparison.OrdinalIgnoreCase);
        }

        if (MacAddress != null && other.MacAddress != null)
        {
            return NormalizeMac(MacAddress).Equals(NormalizeMac(other.MacAddress), StringComparison.Ordinal);
        }

        if (LocationKey != null && other.LocationKey != null)
        {
            return LocationKey.Equals(other.LocationKey, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// Returns an identity that keeps this instance's discriminators and fills in any it is
    /// missing from <paramref name="fallback"/>. Used to carry a pre-connect discriminator (such as
    /// the USB location key, which metadata never reports) into the post-connect identity.
    /// </summary>
    /// <param name="fallback">The identity to take missing discriminators from.</param>
    /// <returns>The merged identity.</returns>
    public DeviceIdentity MergeWith(DeviceIdentity? fallback)
    {
        if (fallback == null)
        {
            return this;
        }

        return new DeviceIdentity(
            SerialNumber ?? fallback.SerialNumber,
            MacAddress ?? fallback.MacAddress,
            LocationKey ?? fallback.LocationKey);
    }

    /// <summary>
    /// Returns a diagnostic string listing the populated discriminators.
    /// </summary>
    public override string ToString()
    {
        if (IsEmpty)
        {
            return "(unidentified)";
        }

        var parts = new List<string>(3);
        if (SerialNumber != null)
        {
            parts.Add("sn=" + SerialNumber);
        }
        if (MacAddress != null)
        {
            parts.Add("mac=" + MacAddress);
        }
        if (LocationKey != null)
        {
            parts.Add("location=" + LocationKey);
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// Trims a discriminator and collapses "not reported" spellings (null, empty, whitespace)
    /// onto <c>null</c> so they are never compared as values.
    /// </summary>
    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Strips separators and case from a MAC address so the hyphenated form Core produces
    /// compares equal to the colon-separated form other tooling uses.
    /// </summary>
    private static string NormalizeMac(string macAddress)
    {
        var buffer = new char[macAddress.Length];
        var length = 0;

        foreach (var c in macAddress)
        {
            if (c is '-' or ':' or '.' or ' ')
            {
                continue;
            }

            buffer[length++] = char.ToLowerInvariant(c);
        }

        return new string(buffer, 0, length);
    }
}
