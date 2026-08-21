using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Device;
using Daqifi.Core.Device.SdCard;
using Xunit;

namespace Daqifi.Core.Tests.Device.SdCard;

/// <summary>
/// Tests for building an <see cref="SdCardDeviceConfiguration"/> from a live device.
/// </summary>
public class SdCardDeviceConfigurationTests
{
    [Fact]
    public void FromDevice_NullDevice_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SdCardDeviceConfiguration.FromDevice(null!));
    }

    [Fact]
    public void FromDevice_ReportsAnalogAndDigitalCounts()
    {
        var device = new DaqifiDevice("TestDevice");
        device.PopulateChannelsFromStatus(new DaqifiOutMessage { AnalogInPortNum = 4, DigitalPortNum = 2 });

        var config = SdCardDeviceConfiguration.FromDevice(device);

        Assert.NotNull(config);
        Assert.Equal(4, config.AnalogPortCount);
        Assert.Equal(2, config.DigitalPortCount);
    }

    /// <summary>
    /// The natural moment to build one of these is right before parsing a download — on whichever
    /// thread the caller is on, while the device's consumer thread is still decoding status
    /// messages and repopulating the channel collection. Folding the live <c>Channels</c> view
    /// there throws "Collection was modified"; the snapshot exists so it cannot.
    /// </summary>
    [Fact]
    public async Task FromDevice_WhileStatusMessagesRepopulateChannels_DoesNotThrow()
    {
        var device = new DaqifiDevice("TestDevice");
        var status = new DaqifiOutMessage { AnalogInPortNum = 16, DigitalPortNum = 16, TimestampFreq = 42_000_000 };
        device.PopulateChannelsFromStatus(status);

        using var stop = new CancellationTokenSource();
        Exception? failure = null;

        // Stands in for the consumer thread: repopulating clears and refills the backing list, so
        // an enumeration of the live view that spans it observes a half-built collection.
        var repopulate = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                device.PopulateChannelsFromStatus(status);
            }
        });

        try
        {
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < 5_000 && failure is null && watch.Elapsed < TimeSpan.FromSeconds(5); i++)
            {
                try
                {
                    Assert.NotNull(SdCardDeviceConfiguration.FromDevice(device));
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            }
        }
        finally
        {
            stop.Cancel();
            await repopulate;
        }

        Assert.Null(failure);
    }
}
