using System.Net;
using Daqifi.Core.Device.Network;
using Xunit;

namespace Daqifi.Core.Tests.Device.Network
{
    public class NetworkConfigurationTests
    {
        [Fact]
        public void DefaultConstructor_InitializesWithDefaultValues()
        {
            // Act
            var config = new NetworkConfiguration();

            // Assert
            Assert.Equal(WifiMode.SelfHosted, config.Mode);
            Assert.Equal(WifiSecurityType.WpaPskPhrase, config.SecurityType);
            Assert.Equal(string.Empty, config.Ssid);
            Assert.Equal(string.Empty, config.Password);
            Assert.Null(config.StaticIP);
            Assert.Null(config.SubnetMask);
            Assert.Null(config.Gateway);
        }

        [Fact]
        public void ParameterizedConstructor_SetsAllProperties()
        {
            // Arrange
            var mode = WifiMode.ExistingNetwork;
            var securityType = WifiSecurityType.None;
            var ssid = "TestNetwork";
            var password = "TestPassword";

            // Act
            var config = new NetworkConfiguration(mode, securityType, ssid, password);

            // Assert
            Assert.Equal(mode, config.Mode);
            Assert.Equal(securityType, config.SecurityType);
            Assert.Equal(ssid, config.Ssid);
            Assert.Equal(password, config.Password);
        }

        [Fact]
        public void Clone_CreatesIndependentCopy()
        {
            // Arrange
            var original = new NetworkConfiguration(
                WifiMode.ExistingNetwork,
                WifiSecurityType.WpaPskPhrase,
                "OriginalSSID",
                "OriginalPassword");

            // Act
            var clone = original.Clone();

            // Assert
            Assert.Equal(original.Mode, clone.Mode);
            Assert.Equal(original.SecurityType, clone.SecurityType);
            Assert.Equal(original.Ssid, clone.Ssid);
            Assert.Equal(original.Password, clone.Password);

            // Verify independence
            clone.Ssid = "ModifiedSSID";
            Assert.Equal("OriginalSSID", original.Ssid);
            Assert.Equal("ModifiedSSID", clone.Ssid);
        }

        [Fact]
        public void Properties_CanBeModified()
        {
            // Arrange
            var config = new NetworkConfiguration();

            // Act
            config.Mode = WifiMode.ExistingNetwork;
            config.SecurityType = WifiSecurityType.None;
            config.Ssid = "NewSSID";
            config.Password = "NewPassword";

            // Assert
            Assert.Equal(WifiMode.ExistingNetwork, config.Mode);
            Assert.Equal(WifiSecurityType.None, config.SecurityType);
            Assert.Equal("NewSSID", config.Ssid);
            Assert.Equal("NewPassword", config.Password);
        }

        [Fact]
        public void StaticIPConstructor_SetsAllProperties()
        {
            // Arrange
            var staticIP = IPAddress.Parse("10.0.0.5");
            var subnet = IPAddress.Parse("255.255.255.0");
            var gateway = IPAddress.Parse("10.0.0.1");

            // Act
            var config = new NetworkConfiguration(
                WifiMode.ExistingNetwork,
                WifiSecurityType.WpaPskPhrase,
                "Net",
                "Pass",
                staticIP,
                subnet,
                gateway);

            // Assert
            Assert.Equal(WifiMode.ExistingNetwork, config.Mode);
            Assert.Equal(WifiSecurityType.WpaPskPhrase, config.SecurityType);
            Assert.Equal("Net", config.Ssid);
            Assert.Equal("Pass", config.Password);
            Assert.Equal(staticIP, config.StaticIP);
            Assert.Equal(subnet, config.SubnetMask);
            Assert.Equal(gateway, config.Gateway);
        }

        [Fact]
        public void StaticIPProperties_CanBeAssignedAndCleared()
        {
            // Arrange
            var config = new NetworkConfiguration
            {
                StaticIP = IPAddress.Parse("192.168.1.10"),
                SubnetMask = IPAddress.Parse("255.255.255.0"),
                Gateway = IPAddress.Parse("192.168.1.1")
            };

            // Act
            config.StaticIP = null;
            config.SubnetMask = null;
            config.Gateway = null;

            // Assert
            Assert.Null(config.StaticIP);
            Assert.Null(config.SubnetMask);
            Assert.Null(config.Gateway);
        }

        [Fact]
        public void Clone_PreservesStaticIPFields()
        {
            // Arrange
            var original = new NetworkConfiguration(
                WifiMode.ExistingNetwork,
                WifiSecurityType.WpaPskPhrase,
                "Net",
                "Pass",
                IPAddress.Parse("10.0.0.5"),
                IPAddress.Parse("255.255.255.0"),
                IPAddress.Parse("10.0.0.1"));

            // Act
            var clone = original.Clone();

            // Assert
            Assert.Equal(original.StaticIP, clone.StaticIP);
            Assert.Equal(original.SubnetMask, clone.SubnetMask);
            Assert.Equal(original.Gateway, clone.Gateway);

            // Verify independence — replacing the clone's references should not
            // affect the original.
            clone.StaticIP = IPAddress.Parse("172.16.0.5");
            Assert.NotEqual(original.StaticIP, clone.StaticIP);
        }

        [Fact]
        public void Clone_PreservesNullStaticIPFields()
        {
            // Arrange
            var original = new NetworkConfiguration(
                WifiMode.SelfHosted,
                WifiSecurityType.None,
                "Net",
                string.Empty);

            // Act
            var clone = original.Clone();

            // Assert
            Assert.Null(clone.StaticIP);
            Assert.Null(clone.SubnetMask);
            Assert.Null(clone.Gateway);
        }
    }

    public class WifiModeTests
    {
        [Fact]
        public void WifiMode_ExistingNetwork_HasCorrectValue()
        {
            Assert.Equal(1, (int)WifiMode.ExistingNetwork);
        }

        [Fact]
        public void WifiMode_SelfHosted_HasCorrectValue()
        {
            Assert.Equal(4, (int)WifiMode.SelfHosted);
        }
    }

    public class WifiSecurityTypeTests
    {
        [Fact]
        public void WifiSecurityType_None_HasCorrectValue()
        {
            Assert.Equal(0, (int)WifiSecurityType.None);
        }

        [Fact]
        public void WifiSecurityType_WpaPskPhrase_HasCorrectValue()
        {
            Assert.Equal(3, (int)WifiSecurityType.WpaPskPhrase);
        }
    }
}
