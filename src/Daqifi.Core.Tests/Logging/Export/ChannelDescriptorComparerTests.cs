using Daqifi.Core.Channel;
using Daqifi.Core.Logging.Export;

namespace Daqifi.Core.Tests.Logging.Export;

public class ChannelDescriptorComparerTests
{
    private static readonly ChannelDescriptorComparer Comparer = ChannelDescriptorComparer.Default;

    private static ChannelDescriptor Channel(string device, string serial, string name)
        => new(device, serial, name, ChannelType.Analog);

    private static int Sign(int value) => Math.Sign(value);

    [Fact]
    public void Compare_SameDeviceAndSerial_OrdersChannelNamesNaturally()
    {
        var ai2 = Channel("Nq1", "SN001", "AI2");
        var ai10 = Channel("Nq1", "SN001", "AI10");

        Assert.Equal(-1, Sign(Comparer.Compare(ai2, ai10)));
    }

    [Fact]
    public void Compare_DeviceNameDecidesBeforeSerialAndChannel()
    {
        var a = Channel("DevA", "SN999", "AI99");
        var b = Channel("DevB", "SN001", "AI0");

        Assert.Equal(-1, Sign(Comparer.Compare(a, b)));
    }

    [Fact]
    public void Compare_SameDevice_SerialDecidesBeforeChannel()
    {
        var a = Channel("Nq1", "SN001", "AI99");
        var b = Channel("Nq1", "SN002", "AI0");

        Assert.Equal(-1, Sign(Comparer.Compare(a, b)));
    }

    [Theory]
    [InlineData("DevA", "Deva")]
    [InlineData("Nq1", "nq1")]
    public void Compare_DeviceName_IsOrdinalNotCultureSensitive(string upper, string lower)
    {
        // Ordinal matches SQLite's BINARY collation, so a source that orders in the database and
        // one that orders in memory agree byte for byte regardless of the machine's locale.
        var a = Channel(upper, "SN001", "AI0");
        var b = Channel(lower, "SN001", "AI0");

        Assert.Equal(Sign(string.CompareOrdinal(upper, lower)), Sign(Comparer.Compare(a, b)));
    }

    [Fact]
    public void Compare_SerialNumber_IsOrdinalNotNatural()
    {
        // Only the channel name gets natural ordering; serials stay byte-wise so the SQLite
        // agreement holds for them too.
        var sn2 = Channel("Nq1", "SN2", "AI0");
        var sn10 = Channel("Nq1", "SN10", "AI0");

        Assert.Equal(1, Sign(Comparer.Compare(sn2, sn10)));
    }

    [Fact]
    public void Compare_IdenticalDescriptors_AreEqual()
    {
        Assert.Equal(0, Comparer.Compare(Channel("Nq1", "SN001", "AI0"), Channel("Nq1", "SN001", "AI0")));
    }

    [Fact]
    public void Compare_ChannelType_DoesNotAffectOrder()
    {
        // Two descriptors that name the same column but disagree on type are the same column.
        var analog = new ChannelDescriptor("Nq1", "SN001", "AI0", ChannelType.Analog);
        var digital = new ChannelDescriptor("Nq1", "SN001", "AI0", ChannelType.Digital);

        Assert.Equal(0, Comparer.Compare(analog, digital));
    }

    [Fact]
    public void Compare_Nulls_SortBeforeEveryDescriptor()
    {
        Assert.Equal(0, Comparer.Compare(null, null));
        Assert.Equal(-1, Sign(Comparer.Compare(null, Channel("Nq1", "SN001", "AI0"))));
        Assert.Equal(1, Sign(Comparer.Compare(Channel("Nq1", "SN001", "AI0"), null)));
    }

    [Fact]
    public void Sort_TwoDevicesWithTwelveChannelsEach_GroupsByDeviceThenOrdersNaturally()
    {
        var channels = new List<ChannelDescriptor>();
        foreach (var device in new[] { "DevB", "DevA" })
        {
            channels.AddRange(Enumerable.Range(0, 12).Select(i => Channel(device, "SN001", $"AI{i}")));
        }

        var sorted = channels.OrderBy(c => c, Comparer).Select(c => c.Key).ToArray();

        string[] expected =
        [
            .. Enumerable.Range(0, 12).Select(i => $"DevA:SN001:AI{i}"),
            .. Enumerable.Range(0, 12).Select(i => $"DevB:SN001:AI{i}")
        ];
        Assert.Equal(expected, sorted);
    }
}
