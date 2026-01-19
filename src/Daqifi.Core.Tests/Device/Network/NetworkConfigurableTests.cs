using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Network;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Xunit;

namespace Daqifi.Core.Tests.Device.Network
{
    public class NetworkConfigurableTests
    {
        [Fact]
        public void NetworkConfiguration_InitializedOnConstruction()
        {
            // Act
            var device = new DaqifiStreamingDevice("TestDevice");

            // Assert
            Assert.NotNull(device.NetworkConfiguration);
            Assert.Equal(WifiMode.SelfHosted, device.NetworkConfiguration.Mode);
            Assert.Equal(WifiSecurityType.WpaPskPhrase, device.NetworkConfiguration.SecurityType);
        }

        [Fact]
        public async Task UpdateNetworkConfigurationAsync_WhenDisconnected_ThrowsInvalidOperationException()
        {
            // Arrange
            var device = new DaqifiStreamingDevice("TestDevice");
            var config = new NetworkConfiguration(
                WifiMode.ExistingNetwork,
                WifiSecurityType.WpaPskPhrase,
                "TestNetwork",
                "TestPassword");

            // Act & Assert
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => device.UpdateNetworkConfigurationAsync(config));
            Assert.Equal("Device is not connected.", exception.Message);
        }

        [Fact]
        public async Task UpdateNetworkConfigurationAsync_WithNullConfiguration_ThrowsArgumentNullException()
        {
            // Arrange
            var device = new TestableDaqifiStreamingDevice("TestDevice");
            device.Connect();

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(
                () => device.UpdateNetworkConfigurationAsync(null!));
        }

        [Fact]
        public async Task UpdateNetworkConfigurationAsync_ExistingNetworkMode_SendsCorrectCommands()
        {
            // Arrange
            var device = new TestableDaqifiStreamingDevice("TestDevice");
            device.Connect();
            var config = new NetworkConfiguration(
                WifiMode.ExistingNetwork,
                WifiSecurityType.WpaPskPhrase,
                "TestNetwork",
                "TestPassword");

            // Act
            await device.UpdateNetworkConfigurationAsync(config);

            // Assert
            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();

            Assert.Contains("SYSTem:COMMunicate:LAN:NETType 1", sentCommands); // ExistingNetwork mode
            Assert.Contains("SYSTem:COMMunicate:LAN:SSID \"TestNetwork\"", sentCommands);
            Assert.Contains("SYSTem:COMMunicate:LAN:SECurity 3", sentCommands); // WPA
            Assert.Contains("SYSTem:COMMunicate:LAN:PASs \"TestPassword\"", sentCommands);
            Assert.Contains("SYSTem:COMMunicate:LAN:APPLY", sentCommands);
            Assert.Contains("SYSTem:COMMunicate:LAN:SAVE", sentCommands);
        }

        [Fact]
        public async Task UpdateNetworkConfigurationAsync_SelfHostedMode_SendsCorrectCommands()
        {
            // Arrange
            var device = new TestableDaqifiStreamingDevice("TestDevice");
            device.Connect();
            var config = new NetworkConfiguration(
                WifiMode.SelfHosted,
                WifiSecurityType.None,
                "DAQiFi_Device",
                "");

            // Act
            await device.UpdateNetworkConfigurationAsync(config);

            // Assert
            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();

            Assert.Contains("SYSTem:COMMunicate:LAN:NETType 4", sentCommands); // SelfHosted mode
            Assert.Contains("SYSTem:COMMunicate:LAN:SSID \"DAQiFi_Device\"", sentCommands);
            Assert.Contains("SYSTem:COMMunicate:LAN:SECurity 0", sentCommands); // Open network
        }

        [Fact]
        public async Task UpdateNetworkConfigurationAsync_WhenStreaming_StopsStreamingFirst()
        {
            // Arrange
            var device = new TestableDaqifiStreamingDevice("TestDevice");
            device.Connect();
            device.StartStreaming();
            device.SentMessages.Clear();

            var config = new NetworkConfiguration(
                WifiMode.SelfHosted,
                WifiSecurityType.WpaPskPhrase,
                "TestNetwork",
                "TestPassword");

            // Act
            await device.UpdateNetworkConfigurationAsync(config);

            // Assert
            var firstCommand = device.SentMessages.First().Data;
            Assert.Equal(ScpiMessageProducer.StopStreaming.Data, firstCommand);
            Assert.False(device.IsStreaming);
        }

        [Fact]
        public async Task UpdateNetworkConfigurationAsync_UpdatesLocalConfiguration()
        {
            // Arrange
            var device = new TestableDaqifiStreamingDevice("TestDevice");
            device.Connect();
            var config = new NetworkConfiguration(
                WifiMode.ExistingNetwork,
                WifiSecurityType.WpaPskPhrase,
                "UpdatedNetwork",
                "UpdatedPassword");

            // Act
            await device.UpdateNetworkConfigurationAsync(config);

            // Assert
            Assert.Equal(WifiMode.ExistingNetwork, device.NetworkConfiguration.Mode);
            Assert.Equal(WifiSecurityType.WpaPskPhrase, device.NetworkConfiguration.SecurityType);
            Assert.Equal("UpdatedNetwork", device.NetworkConfiguration.Ssid);
            Assert.Equal("UpdatedPassword", device.NetworkConfiguration.Password);
        }

        [Fact]
        public async Task UpdateNetworkConfigurationAsync_PreparesLanInterface()
        {
            // Arrange
            var device = new TestableDaqifiStreamingDevice("TestDevice");
            device.Connect();
            var config = new NetworkConfiguration(
                WifiMode.SelfHosted,
                WifiSecurityType.WpaPskPhrase,
                "TestNetwork",
                "TestPassword");

            // Act
            await device.UpdateNetworkConfigurationAsync(config);

            // Assert
            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();

            // LAN interface preparation (disable SD, enable LAN)
            Assert.Contains("SYSTem:STORage:SD:ENAble 0", sentCommands);
            Assert.Contains("SYSTem:COMMunicate:LAN:ENAbled 1", sentCommands);
        }

        [Fact]
        public void PrepareSdInterface_WhenDisconnected_ThrowsInvalidOperationException()
        {
            // Arrange
            var device = new DaqifiStreamingDevice("TestDevice");

            // Act & Assert
            var exception = Assert.Throws<InvalidOperationException>(() => device.PrepareSdInterface());
            Assert.Equal("Device is not connected.", exception.Message);
        }

        [Fact]
        public void PrepareSdInterface_WhenConnected_SendsCorrectCommands()
        {
            // Arrange
            var device = new TestableDaqifiStreamingDevice("TestDevice");
            device.Connect();

            // Act
            device.PrepareSdInterface();

            // Assert
            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();

            Assert.Equal(2, sentCommands.Count);
            Assert.Equal("SYSTem:COMMunicate:LAN:ENAbled 0", sentCommands[0]); // Disable LAN
            Assert.Equal("SYSTem:STORage:SD:ENAble 1", sentCommands[1]); // Enable SD
        }

        [Fact]
        public void PrepareLanInterface_WhenDisconnected_ThrowsInvalidOperationException()
        {
            // Arrange
            var device = new DaqifiStreamingDevice("TestDevice");

            // Act & Assert
            var exception = Assert.Throws<InvalidOperationException>(() => device.PrepareLanInterface());
            Assert.Equal("Device is not connected.", exception.Message);
        }

        [Fact]
        public void PrepareLanInterface_WhenConnected_SendsCorrectCommands()
        {
            // Arrange
            var device = new TestableDaqifiStreamingDevice("TestDevice");
            device.Connect();

            // Act
            device.PrepareLanInterface();

            // Assert
            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();

            Assert.Equal(2, sentCommands.Count);
            Assert.Equal("SYSTem:STORage:SD:ENAble 0", sentCommands[0]); // Disable SD
            Assert.Equal("SYSTem:COMMunicate:LAN:ENAbled 1", sentCommands[1]); // Enable LAN
        }

        /// <summary>
        /// A testable version of DaqifiStreamingDevice that captures sent messages.
        /// </summary>
        private class TestableDaqifiStreamingDevice : DaqifiStreamingDevice
        {
            public List<IOutboundMessage<string>> SentMessages { get; } = new();

            public TestableDaqifiStreamingDevice(string name, IPAddress? ipAddress = null)
                : base(name, ipAddress)
            {
            }

            public override void Send<T>(IOutboundMessage<T> message)
            {
                // Override to capture the message instead of sending it.
                // This avoids the base class's check for a real connection.
                if (message is IOutboundMessage<string> stringMessage)
                {
                    SentMessages.Add(stringMessage);
                }
            }
        }
    }
}
