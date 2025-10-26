namespace Daqifi.Core.Device;

/// <summary>
/// Provides device type detection logic based on device part numbers.
/// </summary>
public static class DeviceTypeDetector
{
    /// <summary>
    /// Detects the device type from a part number string.
    /// </summary>
    /// <param name="partNumber">The device part number (e.g., "Nq1", "Nq3", "DQF-1000", "DQF-2000", "DQF-3000").</param>
    /// <returns>The detected DeviceType, or DeviceType.Unknown if not recognized.</returns>
    /// <remarks>
    /// The detector recognizes part numbers in the following formats:
    /// - Short form: "Nq1", "Nq2", "Nq3" (case-insensitive)
    /// - Full form: "DQF-1000", "DQF-2000", "DQF-3000" (case-insensitive)
    /// </remarks>
    public static DeviceType DetectFromPartNumber(string partNumber)
    {
        if (string.IsNullOrWhiteSpace(partNumber))
        {
            return DeviceType.Unknown;
        }

        return partNumber.ToLowerInvariant() switch
        {
            "nq1" => DeviceType.Nyquist1,
            "nq2" => DeviceType.Nyquist2,
            "nq3" => DeviceType.Nyquist3,
            "dqf-1000" => DeviceType.Nyquist1,
            "dqf-2000" => DeviceType.Nyquist2,
            "dqf-3000" => DeviceType.Nyquist3,
            _ => DeviceType.Unknown
        };
    }
}
