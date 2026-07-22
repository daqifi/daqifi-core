using Daqifi.Core.Device;
using Daqifi.Core.Device.Discovery;

namespace Daqifi.Core.Tests.Device;

public class DeviceIdentityTests
{
    #region Construction

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_BlankDiscriminators_AreTreatedAsNotReported(string? blank)
    {
        var identity = DeviceIdentity.Create(blank, blank, blank);

        Assert.Null(identity.SerialNumber);
        Assert.Null(identity.MacAddress);
        Assert.Null(identity.LocationKey);
        Assert.True(identity.IsEmpty);
        Assert.Equal(string.Empty, identity.Key);
    }

    [Fact]
    public void Create_TrimsDiscriminators()
    {
        var identity = DeviceIdentity.Create("  1234  ");

        Assert.Equal("1234", identity.SerialNumber);
    }

    [Fact]
    public void Key_PrefersSerialThenMacThenLocation()
    {
        Assert.Equal("sn:1234", DeviceIdentity.Create("1234", "AA-BB-CC-DD-EE-FF", "Port_#0001").Key);
        Assert.Equal("mac:aabbccddeeff", DeviceIdentity.Create(null, "AA-BB-CC-DD-EE-FF", "Port_#0001").Key);
        Assert.Equal("loc:port_#0001", DeviceIdentity.Create(null, null, "Port_#0001").Key);
    }

    [Fact]
    public void FromDiscovery_ReadsSerialMacAndLocationKey()
    {
        var identity = DeviceIdentity.FromDiscovery(new DeviceInfo
        {
            SerialNumber = "1234",
            MacAddress = "AA-BB-CC-DD-EE-FF",
            LocationKey = "Port_#0001.Hub_#0001"
        });

        Assert.Equal("1234", identity.SerialNumber);
        Assert.Equal("AA-BB-CC-DD-EE-FF", identity.MacAddress);
        Assert.Equal("Port_#0001.Hub_#0001", identity.LocationKey);
    }

    [Fact]
    public void FromMetadata_ReadsSerialAndMac_AndCarriesTheSuppliedLocationKey()
    {
        var metadata = new DeviceMetadata { SerialNumber = "1234", MacAddress = "AA-BB-CC-DD-EE-FF" };

        var identity = DeviceIdentity.FromMetadata(metadata, "Port_#0001");

        Assert.Equal("1234", identity.SerialNumber);
        Assert.Equal("AA-BB-CC-DD-EE-FF", identity.MacAddress);
        Assert.Equal("Port_#0001", identity.LocationKey);
    }

    [Fact]
    public void Factories_NullArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => DeviceIdentity.FromDiscovery(null!));
        Assert.Throws<ArgumentNullException>(() => DeviceIdentity.FromMetadata(null!));
    }

    #endregion

    #region Matching

    [Fact]
    public void Matches_SameSerial_IsCaseInsensitive()
    {
        var usb = DeviceIdentity.Create("DAQ-12345");
        var wifi = DeviceIdentity.Create("daq-12345");

        Assert.True(usb.Matches(wifi));
        Assert.True(wifi.Matches(usb));
    }

    [Fact]
    public void Matches_DifferentSerials_DoNotMatch_EvenWhenAWeakerDiscriminatorAgrees()
    {
        // A serial-number mismatch is decisive: two units that both answered with a serial number
        // are different units, whatever the location key says.
        var first = DeviceIdentity.Create("1234", locationKey: "Port_#0001");
        var second = DeviceIdentity.Create("5678", locationKey: "Port_#0001");

        Assert.False(first.Matches(second));
    }

    [Fact]
    public void Matches_FallsBackToMac_WhenOneSideHasNoSerial()
    {
        var discovered = DeviceIdentity.Create(null, "AA-BB-CC-DD-EE-FF");
        var connected = DeviceIdentity.Create("1234", "aa:bb:cc:dd:ee:ff");

        Assert.True(discovered.Matches(connected));
    }

    [Fact]
    public void Matches_DifferentMacs_DoNotMatch()
    {
        var first = DeviceIdentity.Create(null, "AA-BB-CC-DD-EE-FF", "Port_#0001");
        var second = DeviceIdentity.Create(null, "11-22-33-44-55-66", "Port_#0001");

        Assert.False(first.Matches(second));
    }

    [Fact]
    public void Matches_FallsBackToLocationKey_WhenNeitherSideReportsSerialOrMac()
    {
        var first = DeviceIdentity.Create(null, null, "PORT_#0001.HUB_#0001");
        var second = DeviceIdentity.Create(null, null, "port_#0001.hub_#0001");

        Assert.True(first.Matches(second));
    }

    [Fact]
    public void Matches_EmptyIdentities_NeverMatch()
    {
        Assert.False(DeviceIdentity.Empty.Matches(DeviceIdentity.Empty));
        Assert.False(DeviceIdentity.Create("1234").Matches(DeviceIdentity.Empty));
        Assert.False(DeviceIdentity.Empty.Matches(DeviceIdentity.Create("1234")));
    }

    [Fact]
    public void Matches_NoSharedDiscriminator_DoesNotMatch()
    {
        // One device only reported a MAC, the other only a location key: nothing to compare.
        var wifi = DeviceIdentity.Create(null, "AA-BB-CC-DD-EE-FF");
        var usb = DeviceIdentity.Create(null, null, "Port_#0001");

        Assert.False(wifi.Matches(usb));
    }

    [Fact]
    public void Matches_Null_DoesNotMatch()
    {
        Assert.False(DeviceIdentity.Create("1234").Matches(null));
    }

    #endregion

    #region Merging

    [Fact]
    public void MergeWith_FillsOnlyMissingDiscriminators()
    {
        var metadata = DeviceIdentity.Create("1234");
        var discovery = DeviceIdentity.Create("9999", "AA-BB-CC-DD-EE-FF", "Port_#0001");

        var merged = metadata.MergeWith(discovery);

        Assert.Equal("1234", merged.SerialNumber);
        Assert.Equal("AA-BB-CC-DD-EE-FF", merged.MacAddress);
        Assert.Equal("Port_#0001", merged.LocationKey);
    }

    [Fact]
    public void MergeWith_Null_ReturnsSameIdentity()
    {
        var identity = DeviceIdentity.Create("1234");

        Assert.Same(identity, identity.MergeWith(null));
    }

    #endregion

    [Fact]
    public void ToString_ListsPopulatedDiscriminators()
    {
        Assert.Equal("(unidentified)", DeviceIdentity.Empty.ToString());
        Assert.Equal("sn=1234, location=Port_#0001", DeviceIdentity.Create("1234", null, "Port_#0001").ToString());
    }
}
