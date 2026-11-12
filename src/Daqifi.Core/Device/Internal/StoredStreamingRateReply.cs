using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Daqifi.Core.Device.Internal;

/// <summary>
/// Reads the answer to <c>SYSTem:STReam:START?</c> out of the capability exchange it rides in,
/// and what the capability document says about whether that answer is a real rate.
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
/// rate after it, so the rate is the first line after the document and the version is only ever
/// looked for before it (<see cref="IndexOfDocumentLine"/>). Anything else in the rate's
/// position — an error line from firmware that does not know the query, a value that is not a
/// plain unsigned integer, or no line at all — is not the rate, and the reply is refused rather
/// than guessed at. Without a document line there is no anchor, so that is refused too: the two
/// remaining integers could be in either order.
/// </para>
/// </remarks>
internal static class StoredStreamingRateReply
{
    /// <summary>
    /// Finds the capability document in the lines of a capability exchange: the first line that
    /// starts with <c>{</c>, or <c>-1</c> when there is none.
    /// </summary>
    /// <param name="lines">Every line the exchange returned, in order.</param>
    /// <returns>The index of the document line, or <c>-1</c>.</returns>
    internal static int IndexOfDocumentLine(IReadOnlyList<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i]?.TrimStart().StartsWith('{') == true)
            {
                return i;
            }
        }

        return -1;
    }

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

        var documentIndex = IndexOfDocumentLine(lines);
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

    /// <summary>
    /// Reads whether the device has had a streaming rate configured since power-up, from the
    /// document's <c>streaming.timing.actual_rate_millihz</c>.
    /// </summary>
    /// <remarks>
    /// The firmware reports that figure as <c>0</c> until a start configures a rate, and as the
    /// rate it applied afterwards (<c>Streaming_IsRateConfigured</c>, v3.9.0). That is what tells
    /// the 1 Hz boot placeholder apart from a 1 Hz somebody started at: <c>START?</c> answers
    /// <c>1</c> for both. Measured on the bench Nq1 on firmware 3.9.0: <c>0</c> on a device nothing
    /// had started, <c>10000</c> after a 10 Hz start and stop.
    /// </remarks>
    /// <param name="lines">Every line the exchange returned, in order.</param>
    /// <returns>
    /// <c>true</c> or <c>false</c> when the document states it; <c>null</c> when there is no
    /// document, it is not valid JSON, or it carries no such field.
    /// </returns>
    internal static bool? ReadRateConfigured(IReadOnlyList<string> lines)
    {
        var documentIndex = IndexOfDocumentLine(lines);
        if (documentIndex < 0)
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(lines[documentIndex]);
            return json.RootElement.ValueKind == JsonValueKind.Object
                   && json.RootElement.TryGetProperty("streaming", out var streaming)
                   && streaming.ValueKind == JsonValueKind.Object
                   && streaming.TryGetProperty("timing", out var timing)
                   && timing.ValueKind == JsonValueKind.Object
                   && timing.TryGetProperty("actual_rate_millihz", out var actualRate)
                   && actualRate.ValueKind == JsonValueKind.Number
                   && actualRate.TryGetInt64(out var millihertz)
                ? millihertz != 0
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
