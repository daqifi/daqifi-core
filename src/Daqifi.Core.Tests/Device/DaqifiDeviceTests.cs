using Daqifi.Core.Device;
using System.Collections.Generic;
using System.Net;
using Xunit;

namespace Daqifi.Core.Tests.Device
{
    public class DaqifiDeviceTests
    {
        [Fact]
        public void Constructor_InitializesPropertiesCorrectly()
        {
            // Arrange
            const string deviceName = "TestDevice";
            var ipAddress = IPAddress.Parse("192.168.1.1");

            // Act
            var device = new DaqifiDevice(deviceName, ipAddress);

            // Assert
            Assert.Equal(deviceName, device.Name);
            Assert.Equal(ipAddress, device.IpAddress);
            Assert.Equal(ConnectionStatus.Disconnected, device.Status);
            Assert.False(device.IsConnected);
        }

        [Fact]
        public void Connect_ChangesStatusAndRaisesEvents()
        {
            // Arrange
            var device = new DaqifiDevice("TestDevice");
            var statusChanges = new List<ConnectionStatus>();
            device.StatusChanged += (_, args) =>
            {
                statusChanges.Add(args.Status);
            };

            // Act
            device.Connect();

            // Assert
            Assert.Equal(2, statusChanges.Count);
            Assert.Equal(ConnectionStatus.Connecting, statusChanges[0]);
            Assert.Equal(ConnectionStatus.Connected, statusChanges[1]);
            Assert.Equal(ConnectionStatus.Connected, device.Status);
            Assert.True(device.IsConnected);
        }

        [Fact]
        public void Disconnect_ChangesStatusAndRaisesEvent()
        {
            // Arrange
            var device = new DaqifiDevice("TestDevice");
            device.Connect(); // Connect first

            var receivedArgs = new List<DeviceStatusEventArgs>();
            device.StatusChanged += (_, args) =>
            {
                receivedArgs.Add(args);
            };

            // Act
            device.Disconnect();

            // Assert
            var arg = Assert.Single(receivedArgs);
            Assert.Equal(ConnectionStatus.Disconnected, arg.Status);
            Assert.Equal(ConnectionStatus.Disconnected, device.Status);
            Assert.False(device.IsConnected);
        }

        [Fact]
        public void Send_WhenDisconnected_ThrowsInvalidOperationException()
        {
            // Arrange
            var device = new DaqifiDevice("TestDevice");

            // Act & Assert
            Assert.Throws<System.InvalidOperationException>(() => device.Send(new Daqifi.Core.Communication.Messages.ScpiMessage("")));
        }
    }
} 