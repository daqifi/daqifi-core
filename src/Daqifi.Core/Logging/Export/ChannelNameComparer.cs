namespace Daqifi.Core.Logging.Export;

/// <summary>
/// Orders channel names the way a person reads them — <c>AI2</c> before <c>AI10</c>, and
/// <c>Tank 2 Pressure</c> before <c>Tank 10 Pressure</c> — by comparing every run of digits in the
/// name as a number rather than as text.
/// </summary>
/// <remarks>
/// <para>
/// Plain string ordering is byte-wise, so <c>'1' &lt; '2'</c> puts <c>AI10</c> and <c>AI11</c>
/// between <c>AI1</c> and <c>AI2</c>. On a device with fewer than ten analog channels that is
/// invisible, because a single digit sorts the same either way; the moment one has ten or more, the
/// exported CSV's columns are silently in the wrong order while every value in every column is
/// individually correct (issue #675).
/// </para>
/// <para>
/// Each name is walked as alternating runs of text and digits. The comparison is <b>ordinal on the
/// text runs, numeric on the digit runs</b>. Every digit run counts, not only a trailing one:
/// device-synthesized names always end in their index, but channel names are user-editable in the
/// consuming applications, and <c>AI1_Temp</c> or <c>Tank 1 Pressure</c> would otherwise fall back
/// to byte-wise order and reintroduce #675 (issue #732).
/// </para>
/// <para>
/// Ordinal is load-bearing rather than incidental: <see cref="ISampleSource"/> implementations
/// backed by SQLite order their channels in the database using BINARY collation, and only an ordinal
/// text comparison agrees with it — a culture-sensitive one would let the same session export
/// different bytes depending on the machine's locale.
/// </para>
/// <para>
/// Names with no digits are compared ordinally end to end, so they behave exactly as they did before
/// this type existed.
/// </para>
/// </remarks>
public sealed class ChannelNameComparer : IComparer<string>
{
    /// <summary>
    /// Gets the shared instance. The comparer holds no state, so one is enough.
    /// </summary>
    public static ChannelNameComparer Instance { get; } = new();

    private ChannelNameComparer()
    {
    }

    /// <summary>
    /// Compares two channel names, ordering their text runs ordinally and their digit runs
    /// numerically.
    /// </summary>
    /// <param name="x">The first name. Null sorts before every non-null name.</param>
    /// <param name="y">The second name.</param>
    /// <returns>
    /// A negative number if <paramref name="x"/> sorts first, a positive number if
    /// <paramref name="y"/> does, and zero only when the two names are identical.
    /// </returns>
    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var xRest = x.AsSpan();
        var yRest = y.AsSpan();

        while (!xRest.IsEmpty || !yRest.IsEmpty)
        {
            // Text runs are compared whole, so one that is a prefix of the other sorts first
            // ("AI" before "AIx", and therefore "AI1x" before "AIx").
            var text = TakeRun(ref xRest, isDigit: false)
                .CompareTo(TakeRun(ref yRest, isDigit: false), StringComparison.Ordinal);
            if (text != 0)
            {
                return text;
            }

            var number = CompareNumbers(TakeRun(ref xRest, isDigit: true), TakeRun(ref yRest, isDigit: true));
            if (number != 0)
            {
                return number;
            }
        }

        // Every run matched, so the names can still differ only by leading zeros ("AI01" vs
        // "AI1"). Fall back to ordinal over the whole name rather than calling two distinct
        // columns equal: the order has to be total, or a sort's output depends on the input order
        // it happened to be given.
        return string.CompareOrdinal(x, y);
    }

    /// <summary>
    /// Splits the leading run of digits — or of non-digits — off <paramref name="rest"/> and returns
    /// it. The run is empty when <paramref name="rest"/> is empty or starts with the other kind.
    /// </summary>
    /// <remarks>
    /// Only ASCII <c>'0'</c>–<c>'9'</c> count as digits. <see cref="char.IsDigit(char)"/> also
    /// accepts the decimal digits of other scripts, which would be split off here and then compared
    /// as text anyway — a worse answer than leaving them in the text run, where they at least sort
    /// consistently.
    /// </remarks>
    private static ReadOnlySpan<char> TakeRun(ref ReadOnlySpan<char> rest, bool isDigit)
    {
        var length = 0;
        while (length < rest.Length && (rest[length] is >= '0' and <= '9') == isDigit)
        {
            length++;
        }

        var run = rest[..length];
        rest = rest[length..];
        return run;
    }

    /// <summary>
    /// Compares two runs of ASCII digits by value.
    /// </summary>
    /// <remarks>
    /// Compared by significant-digit count and then digit by digit rather than parsed: a channel
    /// name may carry an arbitrarily long run of digits, and parsing one into a
    /// <see cref="long"/> would overflow on a name a device is free to report.
    /// </remarks>
    private static int CompareNumbers(ReadOnlySpan<char> x, ReadOnlySpan<char> y)
    {
        // A name that ends where the other carries on with a number sorts first ("AI" before
        // "AI0", "Tank " before "Tank 1").
        if (x.IsEmpty || y.IsEmpty)
        {
            return x.Length.CompareTo(y.Length);
        }

        x = x.TrimStart('0');
        y = y.TrimStart('0');

        return x.Length != y.Length
            ? x.Length.CompareTo(y.Length)
            : x.CompareTo(y, StringComparison.Ordinal);
    }
}
