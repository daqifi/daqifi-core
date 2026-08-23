namespace Daqifi.Core.Communication.Transport;

/// <summary>
/// Normalizes raw string fields read from native HID device descriptors (serial number,
/// product name) into a usable managed string.
/// </summary>
/// <remarks>
/// The Windows/Linux (HidSharp-backed <see cref="HidLibraryPlatform"/>) and macOS (IOKit-backed
/// <see cref="MacOsHidPlatform"/>) transport platforms each read the same kind of raw HID string
/// descriptor and previously carried an identical copy of this normalization; it now lives here
/// once so the two platforms can't drift out of sync with each other.
/// </remarks>
internal static class HidStringNormalization
{
    /// <summary>
    /// Trims a raw HID string field and strips trailing NUL padding, returning
    /// <see langword="null"/> for a null, empty, or whitespace-only value.
    /// </summary>
    /// <param name="value">The raw string read from the native HID descriptor.</param>
    /// <returns>The normalized string, or <see langword="null"/> if there was no usable value.</returns>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().TrimEnd('\0');
    }
}
