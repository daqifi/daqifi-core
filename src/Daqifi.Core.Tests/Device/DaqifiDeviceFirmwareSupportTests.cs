using Daqifi.Core.Device;
using Daqifi.Core.Firmware;
using Xunit;

namespace Daqifi.Core.Tests.Device
{
    public class DaqifiDeviceFirmwareSupportTests
    {
        [Fact]
        public void MinSupportedFirmware_IsV3_5_0()
        {
            Assert.Equal(new FirmwareVersion(3, 5, 0, null, 0), DaqifiDevice.MinSupportedFirmware);
        }

        [Fact]
        public void IsFirmwareVersionSupported_WhenFirmwareVersionNotYetReported_ReturnsFalse()
        {
            var device = new DaqifiDevice("TestDevice");

            Assert.Equal(string.Empty, device.Metadata.FirmwareVersion);
            Assert.False(device.IsFirmwareVersionSupported);
        }

        [Theory]
        [InlineData("not-a-version")]
        [InlineData("")]
        public void IsFirmwareVersionSupported_WhenFirmwareVersionUnparseable_ReturnsFalse(string firmwareVersion)
        {
            var device = new DaqifiDevice("TestDevice");
            device.Metadata.FirmwareVersion = firmwareVersion;

            Assert.False(device.IsFirmwareVersionSupported);
        }

        [Theory]
        [InlineData("3.4.6b1")]
        [InlineData("3.4.3")]
        [InlineData("v3.0.0")]
        public void IsFirmwareVersionSupported_WhenFirmwareBelowFloor_ReturnsFalse(string firmwareVersion)
        {
            var device = new DaqifiDevice("TestDevice");
            device.Metadata.FirmwareVersion = firmwareVersion;

            Assert.False(device.IsFirmwareVersionSupported);
        }

        [Theory]
        [InlineData("3.5.0")]
        [InlineData("v3.5.0")]
        [InlineData("3.6.0")]
        [InlineData("3.6.1")]
        public void IsFirmwareVersionSupported_WhenFirmwareAtOrAboveFloor_ReturnsTrue(string firmwareVersion)
        {
            var device = new DaqifiDevice("TestDevice");
            device.Metadata.FirmwareVersion = firmwareVersion;

            Assert.True(device.IsFirmwareVersionSupported);
        }

        [Fact]
        public void IsFirmwareVersionSupported_EvaluatesLiveAgainstCurrentMetadata()
        {
            // Guards against caching a version-derived bool: the property must reflect the
            // firmware version most recently reported, not a value snapshotted earlier.
            var device = new DaqifiDevice("TestDevice");
            device.Metadata.FirmwareVersion = "3.4.3";
            Assert.False(device.IsFirmwareVersionSupported);

            device.Metadata.FirmwareVersion = "3.6.0";
            Assert.True(device.IsFirmwareVersionSupported);
        }
    }
}
