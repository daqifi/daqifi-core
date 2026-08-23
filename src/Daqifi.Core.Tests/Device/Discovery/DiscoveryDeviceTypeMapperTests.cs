using Daqifi.Core.Device.Discovery;

namespace Daqifi.Core.Tests.Device.Discovery;

/// <summary>
/// Unit tests for <see cref="DiscoveryDeviceTypeMapper"/>, the single part-number to
/// discovery <see cref="DeviceType"/> mapping shared by every transport's finder.
/// </summary>
public class DiscoveryDeviceTypeMapperTests
{
    // Issue #283: the WiFi finder kept its own part-number table with no "nq2" case,
    // so a Nyquist2's part number was silently reported as Unknown.
    [Theory]
    [InlineData("nq1", DeviceType.Nyquist1)]
    [InlineData("Nq1", DeviceType.Nyquist1)]
    [InlineData("nq2", DeviceType.Nyquist2)]
    [InlineData("Nq2", DeviceType.Nyquist2)]
    [InlineData("nq3", DeviceType.Nyquist3)]
    [InlineData("Nq3", DeviceType.Nyquist3)]
    [InlineData("nq4", DeviceType.Unknown)]
    [InlineData("", DeviceType.Unknown)]
    [InlineData(null, DeviceType.Unknown)]
    public void FromPartNumber_ReturnsCorrectType(string? partNumber, DeviceType expected)
    {
        var result = DiscoveryDeviceTypeMapper.FromPartNumber(partNumber);

        Assert.Equal(expected, result);
    }

    // Issue #283: Discovery.DeviceType lacked a Nyquist2 member, so the conversion
    // silently downgraded a correctly-detected Nyquist2 to Unknown.
    [Theory]
    [InlineData(Daqifi.Core.Device.DeviceType.Unknown, DeviceType.Unknown)]
    [InlineData(Daqifi.Core.Device.DeviceType.Nyquist1, DeviceType.Nyquist1)]
    [InlineData(Daqifi.Core.Device.DeviceType.Nyquist2, DeviceType.Nyquist2)]
    [InlineData(Daqifi.Core.Device.DeviceType.Nyquist3, DeviceType.Nyquist3)]
    public void FromCoreDeviceType_MapsToMatchingDiscoveryType(
        Daqifi.Core.Device.DeviceType deviceType, DeviceType expected)
    {
        var result = DiscoveryDeviceTypeMapper.FromCoreDeviceType(deviceType);

        Assert.Equal(expected, result);
    }
}
