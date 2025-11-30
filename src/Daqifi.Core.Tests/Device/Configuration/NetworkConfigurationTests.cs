using Daqifi.Core.Device.Configuration;
using Xunit;

namespace Daqifi.Core.Tests.Device.Configuration;

/// <summary>
/// Tests for NetworkConfiguration model.
/// </summary>
public class NetworkConfigurationTests
{
    [Fact]
    public void NetworkConfiguration_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var config = new NetworkConfiguration();

        // Assert
        // Default value for enum is 0, which doesn't match any defined enum value
        Assert.Equal((WifiMode)0, config.Mode);
        Assert.Equal(WifiSecurityType.None, config.SecurityType);
        Assert.Null(config.Ssid);
        Assert.Null(config.Password);
        Assert.Null(config.IpAddress);
        Assert.Null(config.MacAddress);
        Assert.Null(config.Gateway);
        Assert.Null(config.SubnetMask);
    }

    [Fact]
    public void NetworkConfiguration_SetProperties_WorksCorrectly()
    {
        // Arrange
        var config = new NetworkConfiguration();

        // Act
        config.Mode = WifiMode.SelfHosted;
        config.SecurityType = WifiSecurityType.WpaPskPhrase;
        config.Ssid = "TestNetwork";
        config.Password = "TestPassword123";
        config.IpAddress = "192.168.1.100";
        config.MacAddress = "00:11:22:33:44:55";

        // Assert
        Assert.Equal(WifiMode.SelfHosted, config.Mode);
        Assert.Equal(WifiSecurityType.WpaPskPhrase, config.SecurityType);
        Assert.Equal("TestNetwork", config.Ssid);
        Assert.Equal("TestPassword123", config.Password);
        Assert.Equal("192.168.1.100", config.IpAddress);
        Assert.Equal("00:11:22:33:44:55", config.MacAddress);
    }

    [Theory]
    [InlineData(WifiMode.ExistingNetwork, 1)]
    [InlineData(WifiMode.SelfHosted, 4)]
    public void WifiMode_EnumValues_AreCorrect(WifiMode mode, int expectedValue)
    {
        // Assert
        Assert.Equal(expectedValue, (int)mode);
    }

    [Theory]
    [InlineData(WifiSecurityType.None, 0)]
    [InlineData(WifiSecurityType.WpaPskPhrase, 3)]
    public void WifiSecurityType_EnumValues_AreCorrect(WifiSecurityType securityType, int expectedValue)
    {
        // Assert
        Assert.Equal(expectedValue, (int)securityType);
    }
}

/// <summary>
/// Tests for NetworkStatus model.
/// </summary>
public class NetworkStatusTests
{
    [Fact]
    public void NetworkStatus_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var status = new NetworkStatus();

        // Assert
        Assert.False(status.IsConnected);
        Assert.Null(status.IpAddress);
        Assert.Null(status.MacAddress);
        Assert.Null(status.Ssid);
        Assert.Null(status.SignalStrength);
        Assert.Null(status.Gateway);
        Assert.Null(status.SubnetMask);
    }

    [Fact]
    public void NetworkStatus_SetProperties_WorksCorrectly()
    {
        // Arrange
        var status = new NetworkStatus();

        // Act
        status.IsConnected = true;
        status.IpAddress = "192.168.1.100";
        status.MacAddress = "00:11:22:33:44:55";
        status.Ssid = "TestNetwork";
        status.SignalStrength = -45;
        status.Gateway = "192.168.1.1";
        status.SubnetMask = "255.255.255.0";

        // Assert
        Assert.True(status.IsConnected);
        Assert.Equal("192.168.1.100", status.IpAddress);
        Assert.Equal("00:11:22:33:44:55", status.MacAddress);
        Assert.Equal("TestNetwork", status.Ssid);
        Assert.Equal(-45, status.SignalStrength);
        Assert.Equal("192.168.1.1", status.Gateway);
        Assert.Equal("255.255.255.0", status.SubnetMask);
    }
}
