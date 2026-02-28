using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Daqifi.Core.Firmware;

/// <summary>
/// Parses the JSON response from the <c>SYSTem:COMMunicate:LAN:GETChipInfo?</c> SCPI command.
/// </summary>
public static class LanChipInfoParser
{
    /// <summary>
    /// Attempts to parse a single JSON response string into a <see cref="LanChipInfo"/>.
    /// </summary>
    /// <param name="json">The JSON string to parse, e.g. <c>{"ChipId":1234,"FwVersion":"19.5.4","BuildDate":"Jan  8 2019"}</c>.</param>
    /// <param name="result">The parsed chip info, or <see langword="null"/> if parsing failed.</param>
    /// <returns><see langword="true"/> if parsing succeeded; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(string? json, [NotNullWhen(true)] out LanChipInfo? result)
    {
        result = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("FwVersion", out var fwVersionProp))
            {
                return false;
            }

            var fwVersion = fwVersionProp.GetString();
            if (string.IsNullOrEmpty(fwVersion))
            {
                return false;
            }

            var chipId = root.TryGetProperty("ChipId", out var chipIdProp) &&
                         chipIdProp.ValueKind == JsonValueKind.Number
                ? chipIdProp.GetInt32()
                : 0;

            var buildDate = root.TryGetProperty("BuildDate", out var buildDateProp)
                ? buildDateProp.GetString() ?? string.Empty
                : string.Empty;

            result = new LanChipInfo
            {
                ChipId = chipId,
                FwVersion = fwVersion,
                BuildDate = buildDate
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Attempts to parse <see cref="LanChipInfo"/> from a sequence of text response lines,
    /// trying each non-empty line in order until one succeeds.
    /// </summary>
    /// <param name="lines">Response lines from the device.</param>
    /// <param name="result">The parsed chip info, or <see langword="null"/> if no line could be parsed.</param>
    /// <returns><see langword="true"/> if any line was successfully parsed; otherwise <see langword="false"/>.</returns>
    public static bool TryParseLines(IEnumerable<string> lines, [NotNullWhen(true)] out LanChipInfo? result)
    {
        result = null;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (TryParse(line, out result))
            {
                return true;
            }
        }

        return false;
    }
}
