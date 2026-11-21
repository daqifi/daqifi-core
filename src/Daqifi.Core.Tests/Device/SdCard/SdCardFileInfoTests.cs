using Daqifi.Core.Device.SdCard;
using System;
using Xunit;

namespace Daqifi.Core.Tests.Device.SdCard;

public class SdCardFileInfoTests
{
    [Fact]
    public void Constructor_WithNullFileName_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new SdCardFileInfo(null!));
    }
}
