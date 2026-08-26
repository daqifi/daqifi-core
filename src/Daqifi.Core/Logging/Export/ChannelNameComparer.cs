namespace Daqifi.Core.Logging.Export;

/// <summary>
/// Orders channel names the way a person reads them — <c>AI2</c> before <c>AI10</c> — by splitting a
/// trailing run of digits off the name and comparing it as a number rather than as text.
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
/// The comparison is <b>ordinal on the prefix, numeric on the trailing digits</b>. Ordinal is
/// load-bearing rather than incidental: <see cref="ISampleSource"/> implementations backed by SQLite
/// order their channels in the database using BINARY collation, and only an ordinal prefix
/// comparison agrees with it — a culture-sensitive one would let the same session export different
/// bytes depending on the machine's locale.
/// </para>
/// <para>
/// Names with no trailing digits are compared ordinally end to end, so they behave exactly as they
/// did before this type existed.
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
    /// Compares two channel names, ordering a shared prefix ordinally and a trailing number
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

        var xDigits = TrailingDigitStart(x);
        var yDigits = TrailingDigitStart(y);

        var prefix = x.AsSpan(0, xDigits).CompareTo(y.AsSpan(0, yDigits), StringComparison.Ordinal);
        if (prefix != 0)
        {
            return prefix;
        }

        var number = CompareTrailingNumbers(x.AsSpan(xDigits), y.AsSpan(yDigits));
        if (number != 0)
        {
            return number;
        }

        // Same prefix and the same numeric value, so the names can still differ only by leading
        // zeros ("AI01" vs "AI1"). Fall back to ordinal over the whole name rather than calling two
        // distinct columns equal: the order has to be total, or a sort's output depends on the input
        // order it happened to be given.
        return string.CompareOrdinal(x, y);
    }

    /// <summary>
    /// Returns the index at which <paramref name="value"/>'s trailing run of digits begins, or its
    /// length when it does not end in a digit.
    /// </summary>
    /// <remarks>
    /// Only ASCII <c>'0'</c>–<c>'9'</c> count. <see cref="char.IsDigit(char)"/> also accepts the
    /// decimal digits of other scripts, which would be split off here and then compared as text
    /// anyway — a worse answer than leaving them in the prefix, where they at least sort
    /// consistently.
    /// </remarks>
    private static int TrailingDigitStart(string value)
    {
        var start = value.Length;
        while (start > 0 && value[start - 1] is >= '0' and <= '9')
        {
            start--;
        }

        return start;
    }

    /// <summary>
    /// Compares two runs of ASCII digits by value.
    /// </summary>
    /// <remarks>
    /// Compared by significant-digit count and then digit by digit rather than parsed: a channel
    /// name may carry an arbitrarily long run of digits, and parsing one into a
    /// <see cref="long"/> would overflow on a name a device is free to report.
    /// </remarks>
    private static int CompareTrailingNumbers(ReadOnlySpan<char> x, ReadOnlySpan<char> y)
    {
        // An unnumbered name sorts before a numbered one that shares its prefix ("AI" before "AI0").
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
