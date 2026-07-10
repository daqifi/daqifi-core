using Daqifi.Core.Device;
using Daqifi.Core.Firmware;
using Xunit;

namespace Daqifi.Core.Tests.Device
{
    public class FeatureNotSupportedExceptionTests
    {
        [Fact]
        public void Constructor_SetsAllProperties()
        {
            var requiredVersion = new FirmwareVersion(3, 5, 0, null, 0);

            var ex = new FeatureNotSupportedException(
                DeviceFeature.SdStorageQuery,
                requiredVersion,
                "3.4.3",
                DeviceType.Nyquist1);

            Assert.Equal(DeviceFeature.SdStorageQuery, ex.Feature);
            Assert.Equal(requiredVersion, ex.RequiredVersion);
            Assert.Equal("3.4.3", ex.ActualVersion);
            Assert.Equal(DeviceType.Nyquist1, ex.Board);
        }

        [Fact]
        public void Constructor_WithOnlyFeature_LeavesOptionalPropertiesNull()
        {
            var ex = new FeatureNotSupportedException(DeviceFeature.AnalogOutput);

            Assert.Equal(DeviceFeature.AnalogOutput, ex.Feature);
            Assert.Null(ex.RequiredVersion);
            Assert.Null(ex.ActualVersion);
            Assert.Null(ex.Board);
        }

        [Fact]
        public void Message_IncludesFeatureRequiredAndActualVersion()
        {
            var requiredVersion = new FirmwareVersion(3, 5, 0, null, 0);

            var ex = new FeatureNotSupportedException(
                DeviceFeature.SdStorageQuery,
                requiredVersion,
                "3.4.3");

            Assert.Contains("SdStorageQuery", ex.Message);
            Assert.Contains("3.5.0", ex.Message);
            Assert.Contains("3.4.3", ex.Message);
        }

        [Fact]
        public void Message_WithUnknownActualVersion_SaysUnknown()
        {
            var requiredVersion = new FirmwareVersion(3, 5, 0, null, 0);

            var ex = new FeatureNotSupportedException(DeviceFeature.SdStorageQuery, requiredVersion, null);

            Assert.Contains("unknown", ex.Message);
        }

        [Fact]
        public void Message_WithBoard_IncludesBoard()
        {
            var ex = new FeatureNotSupportedException(
                DeviceFeature.AnalogOutput,
                board: DeviceType.Nyquist1);

            Assert.Contains("Nyquist1", ex.Message);
        }
    }
}
