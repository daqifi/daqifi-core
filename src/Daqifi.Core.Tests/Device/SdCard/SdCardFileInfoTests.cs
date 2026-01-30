using Daqifi.Core.Device.SdCard;
using System;
using Xunit;

namespace Daqifi.Core.Tests.Device.SdCard
{
    public class SdCardFileInfoTests
    {
        [Fact]
        public void Constructor_SetsFileName()
        {
            // Arrange & Act
            var fileInfo = new SdCardFileInfo("test.bin");

            // Assert
            Assert.Equal("test.bin", fileInfo.FileName);
        }

        [Fact]
        public void Constructor_WithDate_SetsCreatedDate()
        {
            // Arrange
            var date = new DateTime(2024, 1, 15, 10, 30, 0);

            // Act
            var fileInfo = new SdCardFileInfo("log_20240115_103000.bin", date);

            // Assert
            Assert.Equal("log_20240115_103000.bin", fileInfo.FileName);
            Assert.Equal(date, fileInfo.CreatedDate);
        }

        [Fact]
        public void Constructor_WithoutDate_SetsNullCreatedDate()
        {
            // Arrange & Act
            var fileInfo = new SdCardFileInfo("data.bin");

            // Assert
            Assert.Null(fileInfo.CreatedDate);
        }

        [Fact]
        public void Constructor_WithNullFileName_ThrowsArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => new SdCardFileInfo(null!));
        }
    }
}
