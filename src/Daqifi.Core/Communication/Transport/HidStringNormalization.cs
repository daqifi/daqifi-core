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
    /// Trims whitespace and NUL padding from both ends of a raw HID string field, returning
    /// <see langword="null"/> when nothing is left.
    /// </summary>
    /// <remarks>
    /// HID string descriptors are fixed-width and NUL-padded, so a field carrying no value can
    /// arrive as NUL padding just as easily as it can arrive empty or blank. All of those
    /// spellings of "not reported" collapse onto <see langword="null"/>, so a caller only has to
    /// check for <see langword="null"/> to know whether the device told it anything. NUL is not
    /// whitespace as far as .NET is concerned (<c>char.IsWhiteSpace('\0')</c> is
    /// <see langword="false"/>), so the padding has to be trimmed explicitly rather than left to
    /// <see cref="string.Trim()"/>.
    /// </remarks>
    /// <param name="value">The raw string read from the native HID descriptor.</param>
    /// <returns>The normalized string, or <see langword="null"/> if there was no usable value.</returns>
    public static string? Normalize(string? value)
    {
        if (value == null)
        {
            return null;
        }

        var start = 0;
        var end = value.Length - 1;

        while (start <= end && IsPadding(value[start]))
        {
            start++;
        }

        while (end >= start && IsPadding(value[end]))
        {
            end--;
        }

        return end < start ? null : value.Substring(start, end - start + 1);
    }

    private static bool IsPadding(char c) => c == '\0' || char.IsWhiteSpace(c);
}
