using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Daqifi.Core.Device.Diagnostics;

/// <summary>
/// Parses the response from the <c>SYSTem:MEMory:FREE?</c> SCPI query into a <see cref="MemoryDiagnostics"/>.
/// </summary>
/// <remarks>
/// The firmware returns a set of <c>Key=Value</c> lines (see <see cref="KeyValueResponseParser"/>).
/// Parsing succeeds when at least one field is recognized.
/// </remarks>
public static class MemoryDiagnosticsParser
{
    /// <summary>
    /// Attempts to parse memory diagnostics from a sequence of response lines.
    /// </summary>
    /// <param name="lines">Response lines from the device.</param>
    /// <param name="result">The parsed diagnostics, or <see langword="null"/> if no field could be parsed.</param>
    /// <returns><see langword="true"/> if at least one field was parsed; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(IEnumerable<string> lines, [NotNullWhen(true)] out MemoryDiagnostics? result)
    {
        var values = KeyValueResponseParser.Parse(lines);
        if (values.Count == 0)
        {
            result = null;
            return false;
        }

        result = new MemoryDiagnostics { Values = values };
        return true;
    }
}
