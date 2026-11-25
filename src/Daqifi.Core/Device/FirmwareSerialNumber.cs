using System.Globalization;

namespace Daqifi.Core.Device;

/// <summary>
/// Converts the hex serial-number text the firmware writes into the decimal form Core uses
/// everywhere (<see cref="DeviceMetadata.SerialNumber"/>).
/// </summary>
/// <remarks>
/// For text that is known to come from a firmware hex writer only: an mDNS TXT record or the
/// header of a CSV or JSON SD-card log. The format comes from where the text came from, never
/// from the text itself, because a 16-digit hex serial made only of the digits 0-9 is also a
/// valid decimal number.
/// </remarks>
internal static class FirmwareSerialNumber
{
    /// <summary>
    /// Length of the text the firmware writes: it formats the board's 64-bit serial zero-padded
    /// (<c>"%016llX"</c>, or <c>"%08lX%08lX"</c>), so always exactly 16 hex digits.
    /// </summary>
    private const int HexLength = 16;

    /// <summary>
    /// Converts firmware hex serial text to decimal. Only the exact firmware format is converted;
    /// anything else is returned trimmed but otherwise untouched, so a serial from some other
    /// writer is never silently reinterpreted.
    /// </summary>
    /// <param name="text">The serial text as the firmware wrote it.</param>
    /// <returns>The decimal serial number, or the trimmed input when it is not the firmware format.</returns>
    internal static string ToDecimal(string text)
    {
        var trimmed = text.Trim();

        // AllowHexSpecifier, not HexNumber: the latter also accepts surrounding white space, which
        // is already gone, and nothing else may be accepted here.
        return trimmed.Length == HexLength
            && ulong.TryParse(trimmed, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var serialNumber)
            ? serialNumber.ToString(CultureInfo.InvariantCulture)
            : trimmed;
    }
}
