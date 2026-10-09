using Daqifi.Core.Device.Network;
using Google.Protobuf;
using Xunit;

namespace Daqifi.Core.Tests.Device.Network;

/// <summary>
/// Unit tests for the <see cref="NetworkAddressHelper"/> class.
/// </summary>
public class NetworkAddressHelperTests
{
    #region Valid Data

    [Fact]
    public void GetIpAddressString_ValidBytes_ReturnsDottedDecimal()
    {
        var message = new DaqifiOutMessage
        {
            IpAddr = ByteString.CopyFrom(new byte[] { 192, 168, 1, 100 })
        };

        var result = NetworkAddressHelper.GetIpAddressString(message);

        Assert.Equal("192.168.1.100", result);
    }

    [Fact]
    public void GetMacAddressString_ValidBytes_ReturnsHyphenSeparatedHex()
    {
        var message = new DaqifiOutMessage
        {
            MacAddr = ByteString.CopyFrom(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF })
        };

        var result = NetworkAddressHelper.GetMacAddressString(message);

        Assert.Equal("AA-BB-CC-DD-EE-FF", result);
    }

    [Fact]
    public void GetSubnetMaskString_ValidBytes_ReturnsDottedDecimal()
    {
        var message = new DaqifiOutMessage
        {
            NetMask = ByteString.CopyFrom(new byte[] { 255, 255, 255, 0 })
        };

        var result = NetworkAddressHelper.GetSubnetMaskString(message);

        Assert.Equal("255.255.255.0", result);
    }

    [Fact]
    public void GetGatewayString_ValidBytes_ReturnsDottedDecimal()
    {
        var message = new DaqifiOutMessage
        {
            Gateway = ByteString.CopyFrom(new byte[] { 192, 168, 1, 1 })
        };

        var result = NetworkAddressHelper.GetGatewayString(message);

        Assert.Equal("192.168.1.1", result);
    }

    [Fact]
    public void GetPrimaryDnsString_ValidBytes_ReturnsDottedDecimal()
    {
        var message = new DaqifiOutMessage
        {
            PrimaryDns = ByteString.CopyFrom(new byte[] { 8, 8, 8, 8 })
        };

        var result = NetworkAddressHelper.GetPrimaryDnsString(message);

        Assert.Equal("8.8.8.8", result);
    }

    [Fact]
    public void GetSecondaryDnsString_ValidBytes_ReturnsDottedDecimal()
    {
        var message = new DaqifiOutMessage
        {
            SecondaryDns = ByteString.CopyFrom(new byte[] { 1, 1, 1, 1 })
        };

        var result = NetworkAddressHelper.GetSecondaryDnsString(message);

        Assert.Equal("1.1.1.1", result);
    }

    [Fact]
    public void GetIpAddressString_AllZeroBytes_ReturnsDottedZeros()
    {
        var message = new DaqifiOutMessage
        {
            IpAddr = ByteString.CopyFrom(new byte[] { 0, 0, 0, 0 })
        };

        var result = NetworkAddressHelper.GetIpAddressString(message);

        Assert.Equal("0.0.0.0", result);
    }

    #endregion

    #region Empty / Default Data

    // Name is the failing-row label. Read is the getter that row calls. A default message leaves
    // every address field an empty ByteString.
    public static TheoryData<AddressGetter> AddressGetters() => new()
    {
        new(nameof(NetworkAddressHelper.GetIpAddressString), NetworkAddressHelper.GetIpAddressString),
        new(nameof(NetworkAddressHelper.GetMacAddressString), NetworkAddressHelper.GetMacAddressString),
        new(nameof(NetworkAddressHelper.GetSubnetMaskString), NetworkAddressHelper.GetSubnetMaskString),
        new(nameof(NetworkAddressHelper.GetGatewayString), NetworkAddressHelper.GetGatewayString),
        new(nameof(NetworkAddressHelper.GetPrimaryDnsString), NetworkAddressHelper.GetPrimaryDnsString),
        new(nameof(NetworkAddressHelper.GetSecondaryDnsString), NetworkAddressHelper.GetSecondaryDnsString),
    };

    [Theory]
    [MemberData(nameof(AddressGetters))]
    public void GetAddress_EmptyByteString_ReturnsEmpty(AddressGetter getter)
    {
        Assert.Equal(string.Empty, getter.Read(new DaqifiOutMessage()));
    }

    #endregion

    #region Invalid Lengths

    [Fact]
    public void GetIpAddressString_TooFewBytes_ReturnsEmpty()
    {
        // Arrange — 3 bytes is not a valid IPv4 address
        var message = new DaqifiOutMessage
        {
            IpAddr = ByteString.CopyFrom(new byte[] { 192, 168, 1 })
        };

        var result = NetworkAddressHelper.GetIpAddressString(message);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void GetIpAddressString_TooManyBytes_ReturnsEmpty()
    {
        // Arrange — 5 bytes is not a valid IPv4 address
        var message = new DaqifiOutMessage
        {
            IpAddr = ByteString.CopyFrom(new byte[] { 192, 168, 1, 100, 5 })
        };

        var result = NetworkAddressHelper.GetIpAddressString(message);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void GetMacAddressString_TooFewBytes_ReturnsEmpty()
    {
        // Arrange — 4 bytes is not a valid MAC address
        var message = new DaqifiOutMessage
        {
            MacAddr = ByteString.CopyFrom(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD })
        };

        var result = NetworkAddressHelper.GetMacAddressString(message);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void GetMacAddressString_TooManyBytes_ReturnsEmpty()
    {
        // Arrange — 7 bytes is not a valid MAC address
        var message = new DaqifiOutMessage
        {
            MacAddr = ByteString.CopyFrom(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x11 })
        };

        var result = NetworkAddressHelper.GetMacAddressString(message);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void GetSubnetMaskString_InvalidLength_ReturnsEmpty()
    {
        var message = new DaqifiOutMessage
        {
            NetMask = ByteString.CopyFrom(new byte[] { 255, 255 })
        };

        Assert.Equal(string.Empty, NetworkAddressHelper.GetSubnetMaskString(message));
    }

    [Fact]
    public void GetGatewayString_InvalidLength_ReturnsEmpty()
    {
        var message = new DaqifiOutMessage
        {
            Gateway = ByteString.CopyFrom(new byte[] { 10, 0, 0, 1, 1 })
        };

        Assert.Equal(string.Empty, NetworkAddressHelper.GetGatewayString(message));
    }

    [Fact]
    public void GetPrimaryDnsString_InvalidLength_ReturnsEmpty()
    {
        var message = new DaqifiOutMessage
        {
            PrimaryDns = ByteString.CopyFrom(new byte[] { 8, 8 })
        };

        Assert.Equal(string.Empty, NetworkAddressHelper.GetPrimaryDnsString(message));
    }

    [Fact]
    public void GetSecondaryDnsString_InvalidLength_ReturnsEmpty()
    {
        var message = new DaqifiOutMessage
        {
            SecondaryDns = ByteString.CopyFrom(new byte[] { 1 })
        };

        Assert.Equal(string.Empty, NetworkAddressHelper.GetSecondaryDnsString(message));
    }

    #endregion

    #region Null Message

    [Theory]
    [MemberData(nameof(AddressGetters))]
    public void GetAddress_NullMessage_ThrowsArgumentNullException(AddressGetter getter)
    {
        Assert.Throws<ArgumentNullException>(() => getter.Read(null!));
    }

    #endregion

    /// <summary>
    /// One <see cref="NetworkAddressHelper"/> getter. <see cref="ToString"/> is the row name.
    /// </summary>
    public sealed record AddressGetter(string Name, Func<DaqifiOutMessage, string> Read)
    {
        public override string ToString() => Name;
    }
}
