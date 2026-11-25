using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Diagnostics;
using Daqifi.Core.Device.SdCard;
using Daqifi.Core.Firmware;
using Xunit;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Pins which failures one <c>catch (DaqifiDeviceException)</c> covers, and that all of them report
/// the device's reply through the one inherited property (issue #1046).
/// </summary>
public class DaqifiDeviceExceptionTests
{
    /// <summary>
    /// The exceptions deliberately left off the base, with the type each must stay catchable as.
    /// </summary>
    private static readonly Dictionary<Type, Type> OffTheBase = new()
    {
        [typeof(DeviceNotConnectedException)] = typeof(InvalidOperationException),
        [typeof(ChannelPopulationTimeoutException)] = typeof(TimeoutException),
        [typeof(SerialPortConnectException)] = typeof(IOException),
        [typeof(TransportNotConnectedException)] = typeof(InvalidOperationException),
        [typeof(ConsumerThreadNotExitedException)] = typeof(InvalidOperationException),
        [typeof(ProducerThreadNotExitedException)] = typeof(InvalidOperationException),
        [typeof(FirmwareUpdateException)] = typeof(Exception),
        [typeof(AmbiguousBootloaderTargetException)] = typeof(FirmwareUpdateException),
    };

    private static IEnumerable<Type> PublicExceptions =>
        typeof(DaqifiDeviceException).Assembly.GetExportedTypes().Where(t => t.IsSubclassOf(typeof(Exception)));

    /// <summary>
    /// A new exception type has to pick a side: derive from the base, or be listed above.
    /// </summary>
    [Fact]
    public void EveryPublicException_DerivesFromTheBase_OrIsListedAsKeptOffIt()
    {
        var offTheBase = PublicExceptions
            .Where(t => !typeof(DaqifiDeviceException).IsAssignableFrom(t))
            .ToDictionary(t => t, t => t.BaseType!);

        Assert.Equal(
            OffTheBase.OrderBy(p => p.Key.FullName, StringComparer.Ordinal),
            offTheBase.OrderBy(p => p.Key.FullName, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(typeof(DeviceCommandFailedException))]
    [InlineData(typeof(ScpiInitializationErrorException))]
    [InlineData(typeof(FeatureNotSupportedException))]
    [InlineData(typeof(DeviceReconnectFailedException))]
    [InlineData(typeof(DeviceDiagnosticsException))]
    [InlineData(typeof(SdCardOperationException))]
    [InlineData(typeof(WifiNotInitializedException))]
    public void DeviceFailure_DerivesDirectlyFromTheBase(Type exceptionType)
        => Assert.Equal(typeof(DaqifiDeviceException), exceptionType.BaseType);

    [Fact]
    public void RawDeviceResponse_IsDeclaredOnceOnTheBase_AndNoTypeHasASecondNameForTheReply()
    {
        var family = PublicExceptions.Where(t => typeof(DaqifiDeviceException).IsAssignableFrom(t)).ToList();

        Assert.All(family, t =>
        {
            Assert.Equal(typeof(DaqifiDeviceException), t.GetProperty("RawDeviceResponse")!.DeclaringType);
            Assert.Null(t.GetProperty("DeviceResponse"));
        });
    }

    public static TheoryData<DaqifiDeviceException> FailuresCarryingAReply => new()
    {
        new DeviceCommandFailedException("CMD", -200, "reply"),
        new DeviceCommandFailedException("CMD", 0, "reply"),
        new DeviceCommandFailedException("CMD", "reply"),
        new ScpiInitializationErrorException("failed", ["reply"]),
        new FeatureNotSupportedException(DeviceFeature.Identify, null, null, null, ["reply"]),
        new DeviceDiagnosticsException("failed", ["reply"]),
        new DeviceDiagnosticsCorruptedResponseException("failed", ["reply"]),
        new SdCardOperationException("failed", ["reply"]),
        new SdCardNotPresentException(["reply"]),
        new WifiNotInitializedException("reply"),
    };

    [Theory]
    [MemberData(nameof(FailuresCarryingAReply), DisableDiscoveryEnumeration = true)]
    public void RawDeviceResponse_CarriesTheReply(DaqifiDeviceException failure)
        => Assert.Equal(new[] { "reply" }, failure.RawDeviceResponse);

    public static TheoryData<DaqifiDeviceException> FailuresWithoutAReply => new()
    {
        new DeviceCommandFailedException("CMD"),
        new DeviceCommandFailedException("CMD", -200, null!),
        new ScpiInitializationErrorException("failed", null!),
        new FeatureNotSupportedException(DeviceFeature.Identify),
        new DeviceReconnectFailedException("Nyquist", 3),
        new DeviceDiagnosticsException("failed", null!),
        new SdCardOperationException("failed", null!),
        new SdCardEmptyTransferException("log.bin"),
        new SdCardRequiresUsbException("USB only"),
        new WifiNotInitializedException(null!),
    };

    [Theory]
    [MemberData(nameof(FailuresWithoutAReply), DisableDiscoveryEnumeration = true)]
    public void RawDeviceResponse_WithoutAReply_IsEmptyRatherThanNull(DaqifiDeviceException failure)
        => Assert.Empty(failure.RawDeviceResponse);

    [Fact]
    public void InnerException_SurvivesTheBase()
    {
        var cause = new IOException("link dropped");

        Assert.All(
            new DaqifiDeviceException[]
            {
                new ScpiInitializationErrorException("failed", [], innerException: cause),
                new DeviceReconnectFailedException("Nyquist", 3, cause),
                new DeviceDiagnosticsException("failed", [], cause),
                new SdCardOperationException("failed", [], innerException: cause),
            },
            failure => Assert.Same(cause, failure.InnerException));
    }
}
