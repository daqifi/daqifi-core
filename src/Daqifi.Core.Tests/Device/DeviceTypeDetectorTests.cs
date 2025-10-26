using Daqifi.Core.Device;
using Xunit;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Unit tests for the <see cref="DeviceTypeDetector"/> class.
/// </summary>
public class DeviceTypeDetectorTests
{
    [Theory]
    [InlineData("nq1", DeviceType.Nyquist1)]
    [InlineData("Nq1", DeviceType.Nyquist1)]
    [InlineData("NQ1", DeviceType.Nyquist1)]
    [InlineData("nq2", DeviceType.Nyquist2)]
    [InlineData("Nq2", DeviceType.Nyquist2)]
    [InlineData("NQ2", DeviceType.Nyquist2)]
    [InlineData("nq3", DeviceType.Nyquist3)]
    [InlineData("Nq3", DeviceType.Nyquist3)]
    [InlineData("NQ3", DeviceType.Nyquist3)]
    public void DetectFromPartNumber_ShortForm_ReturnsCorrectType(string partNumber, DeviceType expected)
    {
        // Act
        var result = DeviceTypeDetector.DetectFromPartNumber(partNumber);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("dqf-1000", DeviceType.Nyquist1)]
    [InlineData("DQF-1000", DeviceType.Nyquist1)]
    [InlineData("dqf-2000", DeviceType.Nyquist2)]
    [InlineData("DQF-2000", DeviceType.Nyquist2)]
    [InlineData("dqf-3000", DeviceType.Nyquist3)]
    [InlineData("DQF-3000", DeviceType.Nyquist3)]
    public void DetectFromPartNumber_FullForm_ReturnsCorrectType(string partNumber, DeviceType expected)
    {
        // Act
        var result = DeviceTypeDetector.DetectFromPartNumber(partNumber);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void DetectFromPartNumber_EmptyOrNull_ReturnsUnknown(string partNumber)
    {
        // Act
        var result = DeviceTypeDetector.DetectFromPartNumber(partNumber);

        // Assert
        Assert.Equal(DeviceType.Unknown, result);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("nq4")]
    [InlineData("dqf-4000")]
    [InlineData("invalid")]
    public void DetectFromPartNumber_UnknownPartNumber_ReturnsUnknown(string partNumber)
    {
        // Act
        var result = DeviceTypeDetector.DetectFromPartNumber(partNumber);

        // Assert
        Assert.Equal(DeviceType.Unknown, result);
    }
}
