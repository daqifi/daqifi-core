namespace Daqifi.Core.Logging.Export;

/// <summary>
/// The column order <see cref="CsvExporter"/> expects from <see cref="ISampleSource.GetChannels"/>:
/// by device name, then serial number, then channel name — the first two ordinally, the last with
/// <see cref="ChannelNameComparer"/> so <c>AI2</c> comes before <c>AI10</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ISampleSource"/> is implemented once per application — desktop, Avalonia, Android —
/// and each implementation decides the order its channels come back in. Left unstated, that made a
/// CSV's column order a property of which application exported it, and gave every one of them its
/// own chance to get the ordering of a two-digit channel index wrong (issue #675). This is the
/// single answer they can all sort by.
/// </para>
/// <para>
/// Device name and serial number stay <b>ordinal</b>, matching SQLite's BINARY collation, so an
/// implementation that orders in the database and one that orders in memory produce byte-identical
/// output. Passing no comparer at all would order by the current culture and quietly break that
/// agreement on any machine whose locale disagrees with the developer's.
/// </para>
/// </remarks>
public sealed class ChannelDescriptorComparer : IComparer<ChannelDescriptor>
{
    /// <summary>
    /// Gets the shared instance. The comparer holds no state, so one is enough.
    /// </summary>
    public static ChannelDescriptorComparer Default { get; } = new();

    private ChannelDescriptorComparer()
    {
    }

    /// <summary>
    /// Compares two channel descriptors by device name, serial number, then channel name.
    /// </summary>
    /// <param name="x">The first descriptor. Null sorts before every non-null descriptor.</param>
    /// <param name="y">The second descriptor.</param>
    /// <returns>
    /// A negative number if <paramref name="x"/> sorts first, a positive number if
    /// <paramref name="y"/> does, and zero when the two name the same column.
    /// </returns>
    public int Compare(ChannelDescriptor? x, ChannelDescriptor? y)
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

        var device = string.CompareOrdinal(x.DeviceName, y.DeviceName);
        if (device != 0)
        {
            return device;
        }

        var serial = string.CompareOrdinal(x.DeviceSerialNo, y.DeviceSerialNo);
        return serial != 0
            ? serial
            : ChannelNameComparer.Instance.Compare(x.ChannelName, y.ChannelName);
    }
}
