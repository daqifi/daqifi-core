using Daqifi.Core.Device.Network;
using Google.Protobuf;
using Xunit;

namespace Daqifi.Core.Tests.Device.Network;

/// <summary>
/// Unit tests for the <see cref="NetworkAddressHelper"/> class.
/// </summary>
public class NetworkAddressHelperTests
{
    [Fact]
    public void GetIpAddressString_ValidBytes_ReturnsDottedDecimal()
    {
        // Arrange
        var message = new DaqifiOutMessage
        {
            IpAddr = ByteString.CopyFrom(new byte[] { 192, 168, 1, 100 })
        };

        // Act
        var result = NetworkAddressHelper.GetIpAddressString(message);

        // Assert
        Assert.Equal("192.168.1.100", result);
    }

    [Fact]
    public void GetIpAddressString_EmptyByteString_ReturnsEmpty()
    {
        // Arrange
        var message = new DaqifiOutMessage();

        // Act
        var result = NetworkAddressHelper.GetIpAddressString(message);

        // Assert
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void GetMacAddressString_ValidBytes_ReturnsHyphenSeparatedHex()
    {
        // Arrange
        var message = new DaqifiOutMessage
        {
            MacAddr = ByteString.CopyFrom(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF })
        };

        // Act
        var result = NetworkAddressHelper.GetMacAddressString(message);

        // Assert
        Assert.Equal("AA-BB-CC-DD-EE-FF", result);
    }

    [Fact]
    public void GetMacAddressString_EmptyByteString_ReturnsEmpty()
    {
        // Arrange
        var message = new DaqifiOutMessage();

        // Act
        var result = NetworkAddressHelper.GetMacAddressString(message);

        // Assert
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void GetSubnetMaskString_ValidBytes_ReturnsDottedDecimal()
    {
        // Arrange
        var message = new DaqifiOutMessage
        {
            NetMask = ByteString.CopyFrom(new byte[] { 255, 255, 255, 0 })
        };

        // Act
        var result = NetworkAddressHelper.GetSubnetMaskString(message);

        // Assert
        Assert.Equal("255.255.255.0", result);
    }

    [Fact]
    public void GetSubnetMaskString_EmptyByteString_ReturnsEmpty()
    {
        // Arrange
        var message = new DaqifiOutMessage();

        // Act
        var result = NetworkAddressHelper.GetSubnetMaskString(message);

        // Assert
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void GetGatewayString_ValidBytes_ReturnsDottedDecimal()
    {
        // Arrange
        var message = new DaqifiOutMessage
        {
            Gateway = ByteString.CopyFrom(new byte[] { 192, 168, 1, 1 })
        };

        // Act
        var result = NetworkAddressHelper.GetGatewayString(message);

        // Assert
        Assert.Equal("192.168.1.1", result);
    }

    [Fact]
    public void GetGatewayString_EmptyByteString_ReturnsEmpty()
    {
        // Arrange
        var message = new DaqifiOutMessage();

        // Act
        var result = NetworkAddressHelper.GetGatewayString(message);

        // Assert
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void GetPrimaryDnsString_ValidBytes_ReturnsDottedDecimal()
    {
        // Arrange
        var message = new DaqifiOutMessage
        {
            PrimaryDns = ByteString.CopyFrom(new byte[] { 8, 8, 8, 8 })
        };

        // Act
        var result = NetworkAddressHelper.GetPrimaryDnsString(message);

        // Assert
        Assert.Equal("8.8.8.8", result);
    }

    [Fact]
    public void GetPrimaryDnsString_EmptyByteString_ReturnsEmpty()
    {
        // Arrange
        var message = new DaqifiOutMessage();

        // Act
        var result = NetworkAddressHelper.GetPrimaryDnsString(message);

        // Assert
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void GetSecondaryDnsString_ValidBytes_ReturnsDottedDecimal()
    {
        // Arrange
        var message = new DaqifiOutMessage
        {
            SecondaryDns = ByteString.CopyFrom(new byte[] { 1, 1, 1, 1 })
        };

        // Act
        var result = NetworkAddressHelper.GetSecondaryDnsString(message);

        // Assert
        Assert.Equal("1.1.1.1", result);
    }

    [Fact]
    public void GetSecondaryDnsString_EmptyByteString_ReturnsEmpty()
    {
        // Arrange
        var message = new DaqifiOutMessage();

        // Act
        var result = NetworkAddressHelper.GetSecondaryDnsString(message);

        // Assert
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void DefaultMessage_AllMethodsReturnEmpty()
    {
        // Arrange
        var message = new DaqifiOutMessage();

        // Act & Assert
        Assert.Equal(string.Empty, NetworkAddressHelper.GetIpAddressString(message));
        Assert.Equal(string.Empty, NetworkAddressHelper.GetMacAddressString(message));
        Assert.Equal(string.Empty, NetworkAddressHelper.GetSubnetMaskString(message));
        Assert.Equal(string.Empty, NetworkAddressHelper.GetGatewayString(message));
        Assert.Equal(string.Empty, NetworkAddressHelper.GetPrimaryDnsString(message));
        Assert.Equal(string.Empty, NetworkAddressHelper.GetSecondaryDnsString(message));
    }

    [Fact]
    public void GetIpAddressString_AllZeroBytes_ReturnsDottedZeros()
    {
        // Arrange
        var message = new DaqifiOutMessage
        {
            IpAddr = ByteString.CopyFrom(new byte[] { 0, 0, 0, 0 })
        };

        // Act
        var result = NetworkAddressHelper.GetIpAddressString(message);

        // Assert
        Assert.Equal("0.0.0.0", result);
    }
}
