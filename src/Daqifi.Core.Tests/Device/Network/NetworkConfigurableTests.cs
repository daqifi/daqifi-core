using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Network;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
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
        public async Task UpdateNetworkConfigurationAsync_WhenDisconnected_ThrowsDeviceNotConnectedException()
        {
            // Arrange
            var device = new DaqifiStreamingDevice("TestDevice");
            var config = new NetworkConfiguration(
                WifiMode.ExistingNetwork,
                WifiSecurityType.WpaPskPhrase,
                "TestNetwork",
                "TestPassword");

            // Act & Assert
            var exception = await Assert.ThrowsAsync<DeviceNotConnectedException>(
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
            // Password should NOT be sent for open networks
            Assert.DoesNotContain(sentCommands, cmd => cmd.StartsWith("SYSTem:COMMunicate:LAN:PASs"));
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
        public async Task UpdateNetworkConfigurationAsync_WithStaticIP_SendsAddressMaskGatewayBeforeApply()
        {
            // Arrange
            var device = new TestableDaqifiStreamingDevice("TestDevice");
            device.Connect();
            var config = new NetworkConfiguration(
                WifiMode.ExistingNetwork,
                WifiSecurityType.WpaPskPhrase,
                "Net",
                "Pass",
                IPAddress.Parse("10.0.0.5"),
                IPAddress.Parse("255.255.255.0"),
                IPAddress.Parse("10.0.0.1"));

            // Act
            await device.UpdateNetworkConfigurationAsync(config);

            // Assert
            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();

            Assert.Contains("SYSTem:COMMunicate:LAN:ADDRess \"10.0.0.5\"", sentCommands);
            Assert.Contains("SYSTem:COMMunicate:LAN:MASK \"255.255.255.0\"", sentCommands);
            Assert.Contains("SYSTem:COMMunicate:LAN:GATEway \"10.0.0.1\"", sentCommands);

            // Apply must follow all three (firmware reads runtime config at APPLY time).
            var applyIndex = sentCommands.IndexOf("SYSTem:COMMunicate:LAN:APPLY");
            Assert.True(applyIndex > sentCommands.IndexOf("SYSTem:COMMunicate:LAN:ADDRess \"10.0.0.5\""));
            Assert.True(applyIndex > sentCommands.IndexOf("SYSTem:COMMunicate:LAN:MASK \"255.255.255.0\""));
            Assert.True(applyIndex > sentCommands.IndexOf("SYSTem:COMMunicate:LAN:GATEway \"10.0.0.1\""));

            // SAVE persists what APPLY pushed, so it must come after every static-IP setter too.
            var saveIndex = sentCommands.IndexOf("SYSTem:COMMunicate:LAN:SAVE");
            Assert.True(saveIndex > sentCommands.IndexOf("SYSTem:COMMunicate:LAN:ADDRess \"10.0.0.5\""));
            Assert.True(saveIndex > sentCommands.IndexOf("SYSTem:COMMunicate:LAN:MASK \"255.255.255.0\""));
            Assert.True(saveIndex > sentCommands.IndexOf("SYSTem:COMMunicate:LAN:GATEway \"10.0.0.1\""));
        }

        [Fact]
        public async Task UpdateNetworkConfigurationAsync_WithoutStaticIP_DoesNotSendAddressMaskGateway()
        {
            // Arrange
            var device = new TestableDaqifiStreamingDevice("TestDevice");
            device.Connect();
            var config = new NetworkConfiguration(
                WifiMode.ExistingNetwork,
                WifiSecurityType.WpaPskPhrase,
                "Net",
                "Pass");

            // Act
            await device.UpdateNetworkConfigurationAsync(config);

            // Assert
            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();

            Assert.DoesNotContain(sentCommands, c => c.StartsWith("SYSTem:COMMunicate:LAN:ADDRess "));
            Assert.DoesNotContain(sentCommands, c => c.StartsWith("SYSTem:COMMunicate:LAN:MASK "));
            Assert.DoesNotContain(sentCommands, c => c.StartsWith("SYSTem:COMMunicate:LAN:GATEway "));
        }

        [Fact]
        public async Task UpdateNetworkConfigurationAsync_WithPartialStaticIP_OnlySendsNonNullFields()
        {
            // Arrange
            var device = new TestableDaqifiStreamingDevice("TestDevice");
            device.Connect();
            var config = new NetworkConfiguration(
                WifiMode.ExistingNetwork,
                WifiSecurityType.WpaPskPhrase,
                "Net",
                "Pass",
                IPAddress.Parse("10.0.0.5"),
                subnetMask: null,
                gateway: null);

            // Act
            await device.UpdateNetworkConfigurationAsync(config);

            // Assert
            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();

            Assert.Contains("SYSTem:COMMunicate:LAN:ADDRess \"10.0.0.5\"", sentCommands);
            Assert.DoesNotContain(sentCommands, c => c.StartsWith("SYSTem:COMMunicate:LAN:MASK "));
            Assert.DoesNotContain(sentCommands, c => c.StartsWith("SYSTem:COMMunicate:LAN:GATEway "));
        }

        [Fact]
        public async Task UpdateNetworkConfigurationAsync_WithNullStaticFields_PreservesPreviouslyCachedValues()
        {
            // Arrange — first call seeds the cache with a known static IP.
            var device = new TestableDaqifiStreamingDevice("TestDevice");
            device.Connect();
            var originalStaticIP = IPAddress.Parse("10.0.0.5");
            var originalSubnet = IPAddress.Parse("255.255.255.0");
            var originalGateway = IPAddress.Parse("10.0.0.1");
            await device.UpdateNetworkConfigurationAsync(new NetworkConfiguration(
                WifiMode.ExistingNetwork,
                WifiSecurityType.WpaPskPhrase,
                "Net",
                "Pass",
                originalStaticIP,
                originalSubnet,
                originalGateway));

            // Act — second call only changes WiFi settings; static IP fields are
            // null which means "leave unchanged". The cache must not be cleared.
            await device.UpdateNetworkConfigurationAsync(new NetworkConfiguration(
                WifiMode.ExistingNetwork,
                WifiSecurityType.WpaPskPhrase,
                "OtherNet",
                "OtherPass"));

            // Assert
            Assert.Equal(originalStaticIP, device.NetworkConfiguration.StaticIP);
            Assert.Equal(originalSubnet, device.NetworkConfiguration.SubnetMask);
            Assert.Equal(originalGateway, device.NetworkConfiguration.Gateway);
        }

        [Fact]
        public async Task UpdateNetworkConfigurationAsync_WithStaticIP_UpdatesLocalConfiguration()
        {
            // Arrange
            var device = new TestableDaqifiStreamingDevice("TestDevice");
            device.Connect();
            var staticIP = IPAddress.Parse("10.0.0.5");
            var subnet = IPAddress.Parse("255.255.255.0");
            var gateway = IPAddress.Parse("10.0.0.1");
            var config = new NetworkConfiguration(
                WifiMode.ExistingNetwork,
                WifiSecurityType.WpaPskPhrase,
                "Net",
                "Pass",
                staticIP,
                subnet,
                gateway);

            // Act
            await device.UpdateNetworkConfigurationAsync(config);

            // Assert
            Assert.Equal(staticIP, device.NetworkConfiguration.StaticIP);
            Assert.Equal(subnet, device.NetworkConfiguration.SubnetMask);
            Assert.Equal(gateway, device.NetworkConfiguration.Gateway);
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
        public async Task UpdateNetworkConfigurationAsync_OverWifi_StillReEnablesLan()
        {
            // Regression: network reconfiguration must bring the LAN back up after ApplyNetworkLan
            // even over a non-USB (WiFi/TCP) control transport — it owns the LAN state and must NOT
            // rely on the transport-aware PrepareLanInterface (which leaves LAN alone over WiFi).
            var device = new TestableNonUsbDaqifiStreamingDevice("TestDevice");
            device.Connect();
            var config = new NetworkConfiguration(
                WifiMode.SelfHosted,
                WifiSecurityType.WpaPskPhrase,
                "TestNetwork",
                "TestPassword");

            await device.UpdateNetworkConfigurationAsync(config);

            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Contains("SYSTem:COMMunicate:LAN:ENAbled 1", sentCommands); // EnableNetworkLan (unconditional)
            Assert.Contains("SYSTem:STORage:SD:ENAble 0", sentCommands);       // DisableStorageSd
        }

        [Fact]
        public async Task UpdateNetworkConfigurationAsync_SavesBeforeApplying()
        {
            // Regression (#352): the save MUST precede the apply. ApplyNetworkLan restarts the WiFi
            // module; a save sent afterwards can be lost to that restart, leaving the device
            // applied-but-not-persisted (new config live now, gone on the next power cycle). The
            // firmware persists the *staged* settings, so saving first is both valid and durable.
            var device = new TestableDaqifiStreamingDevice("TestDevice");
            device.Connect();
            var config = new NetworkConfiguration(
                WifiMode.ExistingNetwork,
                WifiSecurityType.WpaPskPhrase,
                "TestNetwork",
                "TestPassword");

            await device.UpdateNetworkConfigurationAsync(config);

            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            var saveIndex = sentCommands.IndexOf("SYSTem:COMMunicate:LAN:SAVE");
            var applyIndex = sentCommands.IndexOf("SYSTem:COMMunicate:LAN:APPLY");

            Assert.True(saveIndex >= 0, "Expected a LAN:SAVE command to be sent.");
            Assert.True(applyIndex >= 0, "Expected a LAN:APPLY command to be sent.");
            Assert.True(
                saveIndex < applyIndex,
                $"LAN:SAVE (index {saveIndex}) must be sent before LAN:APPLY (index {applyIndex}).");
        }

        [Fact]
        public async Task UpdateNetworkConfigurationAsync_OverWifi_SavesBeforeApplying()
        {
            // The field case this ordering exists for: switching the device from one hotspot to
            // another over a WiFi/TCP control link, with no USB cable available. The apply drops the
            // connection by design — that is inherent to leaving the current network — so the config
            // must already be in NVM before it fires.
            var device = new TestableNonUsbDaqifiStreamingDevice("TestDevice");
            device.Connect();
            var config = new NetworkConfiguration(
                WifiMode.ExistingNetwork,
                WifiSecurityType.WpaPskPhrase,
                "OtherHotspot",
                "OtherPassword");

            await device.UpdateNetworkConfigurationAsync(config);

            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            var saveIndex = sentCommands.IndexOf("SYSTem:COMMunicate:LAN:SAVE");
            var applyIndex = sentCommands.IndexOf("SYSTem:COMMunicate:LAN:APPLY");

            Assert.True(saveIndex >= 0, "Expected a LAN:SAVE command to be sent over WiFi.");
            Assert.True(applyIndex >= 0, "Expected a LAN:APPLY command to be sent over WiFi.");
            Assert.True(
                saveIndex < applyIndex,
                $"Over WiFi, LAN:SAVE (index {saveIndex}) must be sent before LAN:APPLY (index {applyIndex}).");
        }

        [Fact]
        public async Task UpdateNetworkConfigurationAsync_SendsNothingAfterApply()
        {
            // The apply is the last command by design: anything sent after it races the WiFi module
            // restart and can be silently dropped over a WiFi/TCP transport. Guards against a future
            // change appending a trailing command to the sequence.
            var device = new TestableNonUsbDaqifiStreamingDevice("TestDevice");
            device.Connect();
            var config = new NetworkConfiguration(
                WifiMode.ExistingNetwork,
                WifiSecurityType.WpaPskPhrase,
                "TestNetwork",
                "TestPassword");

            await device.UpdateNetworkConfigurationAsync(config);

            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            var applyIndex = sentCommands.IndexOf("SYSTem:COMMunicate:LAN:APPLY");

            Assert.True(applyIndex >= 0, "Expected a LAN:APPLY command to be sent.");
            Assert.True(
                applyIndex == sentCommands.Count - 1,
                "LAN:APPLY must be the final command sent; commands after it can be lost to the " +
                $"WiFi module restart. Trailing commands: [{string.Join(", ", sentCommands.Skip(applyIndex + 1))}]");
        }

        [Fact]
        public async Task UpdateNetworkConfigurationAsync_CanceledBeforeCommit_SendsNeitherSaveNorApply()
        {
            // Cancellation before the save is a clean abort: everything sent so far only staged
            // values in the device's runtime settings, so nothing is persisted or applied.
            using var cts = new CancellationTokenSource();
            var device = new CancelOnCommandDaqifiStreamingDevice(
                "TestDevice", cancelAfterCommand: "SYSTem:STORage:SD:ENAble 0", cts: cts);
            device.Connect();
            var config = new NetworkConfiguration(
                WifiMode.ExistingNetwork,
                WifiSecurityType.WpaPskPhrase,
                "TestNetwork",
                "TestPassword");

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => device.UpdateNetworkConfigurationAsync(config, cts.Token));

            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            Assert.DoesNotContain("SYSTem:COMMunicate:LAN:SAVE", sentCommands);
            Assert.DoesNotContain("SYSTem:COMMunicate:LAN:APPLY", sentCommands);
        }

        [Fact]
        public async Task UpdateNetworkConfigurationAsync_CanceledDuringRestartWait_CompletesAndUpdatesLocalState()
        {
            // Past the save/apply the device has already committed. Cancelling during the restart
            // wait must end the wait rather than fail the call — surfacing "canceled" here would
            // tell the caller nothing happened while the device sits on the new configuration, and
            // would strand NetworkConfiguration holding the old values.
            using var cts = new CancellationTokenSource();
            var device = new CancelOnCommandDaqifiStreamingDevice(
                "TestDevice", cancelAfterCommand: "SYSTem:COMMunicate:LAN:APPLY", cts: cts);
            device.Connect();
            var config = new NetworkConfiguration(
                WifiMode.ExistingNetwork,
                WifiSecurityType.WpaPskPhrase,
                "NewNetwork",
                "NewPassword");

            // Cancelling as APPLY goes out puts the token in a canceled state before the wait.
            await device.UpdateNetworkConfigurationAsync(config, cts.Token);

            var sentCommands = device.SentMessages.Select(m => m.Data).ToList();
            Assert.Contains("SYSTem:COMMunicate:LAN:SAVE", sentCommands);
            Assert.Contains("SYSTem:COMMunicate:LAN:APPLY", sentCommands);

            // Local state must reflect what the device actually committed.
            Assert.Equal("NewNetwork", device.NetworkConfiguration.Ssid);
            Assert.Equal(WifiMode.ExistingNetwork, device.NetworkConfiguration.Mode);
        }

        [Fact]
        public void PrepareSdInterface_WhenDisconnected_ThrowsDeviceNotConnectedException()
        {
            // Arrange
            var device = new DaqifiStreamingDevice("TestDevice");

            // Act & Assert
            var exception = Assert.Throws<DeviceNotConnectedException>(() => device.PrepareSdInterface());
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
        public void PrepareLanInterface_WhenDisconnected_ThrowsDeviceNotConnectedException()
        {
            // Arrange
            var device = new DaqifiStreamingDevice("TestDevice");

            // Act & Assert
            var exception = Assert.Throws<DeviceNotConnectedException>(() => device.PrepareLanInterface());
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

        [Fact]
        public void PrepareSdInterface_OverWifi_LeavesLanEnabled()
        {
            // Over WiFi/TCP (fw >= v3.7.0, #598/#599) the LAN must stay enabled — the SPI driver
            // arbitrates SD/WiFi transactions, and disabling LAN would drop the TCP channel that
            // carries the SD reply. Only the SD subsystem is enabled.
            var device = new TestableNonUsbDaqifiStreamingDevice("TestDevice");
            device.Connect();

            device.PrepareSdInterface();

            var sent = device.SentMessages.Select(m => m.Data).ToList();
            Assert.DoesNotContain("SYSTem:COMMunicate:LAN:ENAbled 0", sent); // no DisableNetworkLan
            Assert.Contains("SYSTem:STORage:SD:ENAble 1", sent);             // SD still enabled
        }

        [Fact]
        public void PrepareLanInterface_OverWifi_LeavesLanUntouched()
        {
            // Over WiFi the LAN was never disabled, so it must not be re-enabled (that would
            // re-initialize the WiFi module and drop the connection). SD is still returned to idle.
            var device = new TestableNonUsbDaqifiStreamingDevice("TestDevice");
            device.Connect();

            device.PrepareLanInterface();

            var sent = device.SentMessages.Select(m => m.Data).ToList();
            Assert.DoesNotContain("SYSTem:COMMunicate:LAN:ENAbled 1", sent); // no EnableNetworkLan
            Assert.Contains("SYSTem:STORage:SD:ENAble 0", sent);             // SD disabled
        }

        [Fact]
        public void NetworkConfiguration_ReturnsClone_PreventingExternalModification()
        {
            // Arrange
            var device = new DaqifiStreamingDevice("TestDevice");
            var config1 = device.NetworkConfiguration;
            var config2 = device.NetworkConfiguration;

            // Act - modify the returned configuration
            config1.Ssid = "ModifiedSSID";

            // Assert - device's internal state should be unchanged
            Assert.NotSame(config1, config2); // Different instances
            Assert.NotEqual("ModifiedSSID", config2.Ssid); // Original unchanged
        }

        [Fact]
        public async Task UpdateNetworkConfigurationAsync_WhenCanceled_ThrowsOperationCanceledException()
        {
            // Arrange
            var device = new TestableDaqifiStreamingDevice("TestDevice");
            device.Connect();
            var config = new NetworkConfiguration(
                WifiMode.ExistingNetwork,
                WifiSecurityType.WpaPskPhrase,
                "TestNetwork",
                "TestPassword");
            var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act & Assert
            await Assert.ThrowsAsync<OperationCanceledException>(
                () => device.UpdateNetworkConfigurationAsync(config, cts.Token));
        }

        [Fact]
        public async Task UpdateNetworkConfigurationAsync_WithUnsupportedWifiMode_ThrowsArgumentOutOfRangeException()
        {
            // Arrange
            var device = new TestableDaqifiStreamingDevice("TestDevice");
            device.Connect();
            var config = new NetworkConfiguration
            {
                Mode = (WifiMode)999, // Invalid enum value
                SecurityType = WifiSecurityType.None,
                Ssid = "Test",
                Password = ""
            };

            // Act & Assert
            var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => device.UpdateNetworkConfigurationAsync(config));
            Assert.Contains("Unsupported WiFi mode", exception.Message);
        }

        [Fact]
        public async Task UpdateNetworkConfigurationAsync_WithUnsupportedSecurityType_ThrowsArgumentOutOfRangeException()
        {
            // Arrange
            var device = new TestableDaqifiStreamingDevice("TestDevice");
            device.Connect();
            var config = new NetworkConfiguration
            {
                Mode = WifiMode.SelfHosted,
                SecurityType = (WifiSecurityType)999, // Invalid enum value
                Ssid = "Test",
                Password = ""
            };

            // Act & Assert
            var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => device.UpdateNetworkConfigurationAsync(config));
            Assert.Contains("Unsupported WiFi security type", exception.Message);
        }

        [Fact]
        public async Task LoadNetworkConfigurationAsync_WhenDisconnected_ThrowsDeviceNotConnectedException()
        {
            var device = new DaqifiStreamingDevice("TestDevice");

            var exception = await Assert.ThrowsAsync<DeviceNotConnectedException>(
                () => device.LoadNetworkConfigurationAsync());
            Assert.Equal("Device is not connected.", exception.Message);
        }

        [Fact]
        public async Task LoadNetworkConfigurationAsync_WhenConnected_SendsLoadCommand()
        {
            var device = new TestableDaqifiStreamingDevice("TestDevice");
            device.Connect();

            await device.LoadNetworkConfigurationAsync();

            var sentMessage = Assert.Single(device.SentMessages);
            Assert.Equal("SYSTem:COMMunicate:LAN:LOAD", sentMessage.Data);
        }

        [Fact]
        public async Task LoadNetworkConfigurationAsync_WhenCanceled_ThrowsOperationCanceledException()
        {
            var device = new TestableDaqifiStreamingDevice("TestDevice");
            device.Connect();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => device.LoadNetworkConfigurationAsync(cts.Token));
            Assert.Empty(device.SentMessages);
        }

        [Fact]
        public async Task FactoryResetNetworkAsync_WhenDisconnected_ThrowsDeviceNotConnectedException()
        {
            var device = new DaqifiStreamingDevice("TestDevice");

            var exception = await Assert.ThrowsAsync<DeviceNotConnectedException>(
                () => device.FactoryResetNetworkAsync());
            Assert.Equal("Device is not connected.", exception.Message);
        }

        [Fact]
        public async Task FactoryResetNetworkAsync_WhenConnected_SendsFactoryResetCommand()
        {
            var device = new TestableDaqifiStreamingDevice("TestDevice");
            device.Connect();

            await device.FactoryResetNetworkAsync();

            var sentMessage = Assert.Single(device.SentMessages);
            Assert.Equal("SYSTem:COMMunicate:LAN:FACRESET", sentMessage.Data);
        }

        /// <summary>
        /// A testable version of DaqifiStreamingDevice that captures sent messages.
        /// </summary>
        /// <summary>
        /// Captures sent messages like <see cref="TestableDaqifiStreamingDevice"/>, but also trips a
        /// <see cref="CancellationTokenSource"/> the moment a nominated command is dispatched. This
        /// is how the cancellation tests land the cancel at an exact point mid-sequence — before the
        /// save (a clean abort) or as the apply goes out (already committed).
        /// </summary>
        private class CancelOnCommandDaqifiStreamingDevice : DaqifiStreamingDevice
        {
            public List<IOutboundMessage<string>> SentMessages { get; } = new();

            private readonly string _cancelAfterCommand;
            private readonly CancellationTokenSource _cts;

            public CancelOnCommandDaqifiStreamingDevice(
                string name, string cancelAfterCommand, CancellationTokenSource cts)
                : base(name)
            {
                _cancelAfterCommand = cancelAfterCommand;
                _cts = cts;
            }

            public override bool IsUsbConnection => true;

            public override void Send<T>(IOutboundMessage<T> message)
            {
                if (message is IOutboundMessage<string> stringMessage)
                {
                    SentMessages.Add(stringMessage);
                    if (stringMessage.Data == _cancelAfterCommand)
                    {
                        _cts.Cancel();
                    }
                }
            }
        }

        private class TestableDaqifiStreamingDevice : DaqifiStreamingDevice
        {
            public List<IOutboundMessage<string>> SentMessages { get; } = new();

            public TestableDaqifiStreamingDevice(string name, IPAddress? ipAddress = null)
                : base(name, ipAddress)
            {
            }

            /// <summary>
            /// Reports a USB connection so the interface-prep tests assert the USB sequence
            /// (LAN disabled to free the shared SPI bus, then restored). The WiFi sequence — where
            /// the LAN is left enabled — is covered by <see cref="PrepareSdInterface_OverWifi_LeavesLanEnabled"/>
            /// and the SD-over-WiFi operation tests.
            /// </summary>
            public override bool IsUsbConnection => true;

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

        /// <summary>Non-USB (WiFi/TCP) variant for the transport-aware interface-prep tests.</summary>
        private class TestableNonUsbDaqifiStreamingDevice : DaqifiStreamingDevice
        {
            public List<IOutboundMessage<string>> SentMessages { get; } = new();

            public TestableNonUsbDaqifiStreamingDevice(string name, IPAddress? ipAddress = null)
                : base(name, ipAddress)
            {
            }

            public override bool IsUsbConnection => false;

            public override void Send<T>(IOutboundMessage<T> message)
            {
                if (message is IOutboundMessage<string> stringMessage)
                {
                    SentMessages.Add(stringMessage);
                }
            }
        }
    }
}
