using System.Collections.Generic;
using System.Globalization;

namespace Daqifi.Core.Device.Internal;

/// <summary>
/// Reads the answer to <c>SYSTem:STReam:START?</c> out of the capability exchange it rides in.
/// </summary>
/// <remarks>
/// <para>
/// The query is the last one in that exchange, sent after <c>CONFigure:CAPabilities:JSON?</c>,
/// because each text exchange costs a consumer swap and a completion window — hundreds of
/// milliseconds on a connect that takes about two seconds — while one more line in an exchange
/// that is already running costs almost nothing.
/// </para>
/// <para>
/// Sharing the exchange means the reply has to be told apart from the schema version, which is
/// also a bare integer. The document is the anchor: the version is answered before it and the
/// rate after it, so the rate is the first line after the document. Anything else in that
/// position — an error line from firmware that does not know the query, a value that is not a
/// plain unsigned integer, or no line at all — is not the rate, and the reply is refused rather
/// than guessed at. Without a document line there is no anchor, so that is refused too: the two
/// remaining integers could be in either order.
/// </para>
/// </remarks>
internal static class StoredStreamingRateReply
{
    /// <summary>
    /// Finds the stored-rate reply in the lines of a capability exchange.
    /// </summary>
    /// <param name="lines">Every line the exchange returned, in order.</param>
    /// <param name="rateHz">
    /// The reported rate, in Hz, when this returns <c>true</c>. Not range-checked against the
    /// device, and the 1 Hz boot placeholder is returned like any other value: what to adopt is the
    /// caller's decision, since the ceiling is the device's.
    /// </param>
    /// <returns><c>true</c> when the line after the document is a plain unsigned integer that fits in an <see cref="int"/>.</returns>
    internal static bool TryParse(IReadOnlyList<string> lines, out int rateHz)
    {
        rateHz = 0;

        var documentIndex = -1;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (lines[i]?.TrimStart().StartsWith('{') == true)
            {
                documentIndex = i;
                break;
            }
        }

        if (documentIndex < 0)
        {
            return false;
        }

        for (var i = documentIndex + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // NumberStyles.None: digits only. The firmware answers with SCPI_ResultUInt64, so a
            // sign, a decimal point or an exponent is not this reply.
            return int.TryParse(line.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out rateHz);
        }

        return false;
    }
}
