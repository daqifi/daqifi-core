using System;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Xunit;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Pins how a device publishes its metadata (#1048): whole immutable snapshots, so no caller can
/// change what the device reads and no reader sees part of a status message.
/// </summary>
public class DeviceMetadataSnapshotTests
{
    [Fact]
    public void ACopyDerivedFromTheDevicesMetadata_DoesNotChangeWhatTheDeviceSupports()
    {
        var device = new StatusDrivenDevice();
        device.ReceiveStatus(new DaqifiOutMessage { DevicePn = "Nq1", DeviceFwRev = "3.4.0" });

        var copy = device.Metadata with { FirmwareVersion = "9.9.9" };

        Assert.Equal("9.9.9", copy.FirmwareVersion);
        Assert.Equal("3.4.0", device.Metadata.FirmwareVersion);
        Assert.False(device.IsFirmwareVersionSupported);
        Assert.False(device.Supports(DeviceFeature.CapabilityDocument));
    }

    [Fact]
    public void AStatusMessage_ReplacesTheSnapshot_AndLeavesOneAlreadyReadAlone()
    {
        var device = new StatusDrivenDevice();
        device.ReceiveStatus(new DaqifiOutMessage { DevicePn = "Nq1", DeviceFwRev = "3.8.0" });
        var held = device.Metadata;

        device.ReceiveStatus(new DaqifiOutMessage { DevicePn = "Nq3", DeviceFwRev = "3.9.1" });

        Assert.Equal("3.8.0", held.FirmwareVersion);
        Assert.Equal(DeviceType.Nyquist1, held.DeviceType);
        Assert.Equal("3.9.1", device.Metadata.FirmwareVersion);
        Assert.Equal(DeviceType.Nyquist3, device.Metadata.DeviceType);
    }

    [Fact]
    public async Task AReaderOnAnotherThread_NeverSeesPartOfAStatusMessage()
    {
        var device = new StatusDrivenDevice();
        DaqifiOutMessage[] statuses =
        [
            new() { DevicePn = "Nq1", DeviceFwRev = "1", DeviceSn = 1, AnalogInPortNum = 1, BattStatus = 1, Ssid = "1" },
            new() { DevicePn = "Nq3", DeviceFwRev = "2", DeviceSn = 2, AnalogInPortNum = 2, BattStatus = 2, Ssid = "2" },
        ];
        device.ReceiveStatus(statuses[0]);

        using var stop = new CancellationTokenSource();
        var writer = Task.Factory.StartNew(
            () =>
            {
                for (var i = 0; !stop.IsCancellationRequested; i++)
                {
                    device.ReceiveStatus(statuses[i % 2]);
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        try
        {
            for (var i = 0; i < 200_000; i++)
            {
                var metadata = device.Metadata;
                var expected = metadata.SerialNumber;

                Assert.Equal(expected == "1" ? "Nq1" : "Nq3", metadata.PartNumber);
                Assert.Equal(expected, metadata.FirmwareVersion);
                Assert.Equal(expected, metadata.Ssid);
                Assert.Equal(int.Parse(expected), metadata.Capabilities.AnalogInputChannels);
                Assert.Equal(expected == "1" ? 22000 : 16000, metadata.Capabilities.MaxSamplingRate);
                Assert.Equal(int.Parse(expected), metadata.Health.BatteryPercent);
            }
        }
        finally
        {
            stop.Cancel();
            await writer;
        }
    }

    [Fact]
    public async Task UpdatesFromTwoThreads_AreBothKept()
    {
        // A status message arrives on the reader thread while a caller's friendly-name write or
        // capability read updates the same object.
        const int count = 20_000;
        var device = new StatusDrivenDevice();

        var name = Task.Run(() => Repeat(m => m with { DevicePort = m.DevicePort + 1 }));
        var battery = Task.Run(() => Repeat(m => m with
        {
            Health = m.Health with { PowerStatus = m.Health.PowerStatus + 1 }
        }));
        await Task.WhenAll(name, battery);

        Assert.Equal(count, device.Metadata.DevicePort);
        Assert.Equal((uint)count, device.Metadata.Health.PowerStatus);

        void Repeat(Func<DeviceMetadata, DeviceMetadata> change)
        {
            for (var i = 0; i < count; i++)
            {
                device.UpdateMetadata(change);
            }
        }
    }

    [Fact]
    public void UpdateMetadata_RejectsNull_AndAChangeThatReturnsNull()
    {
        var device = new StatusDrivenDevice();
        var before = device.Metadata;

        Assert.Throws<ArgumentNullException>(() => device.UpdateMetadata(null!));
        Assert.Throws<InvalidOperationException>(() => device.UpdateMetadata(_ => null!));
        Assert.Same(before, device.Metadata);
    }

    private sealed class StatusDrivenDevice() : DaqifiDevice("Test Device")
    {
        public void ReceiveStatus(DaqifiOutMessage message) => OnStatusMessageReceived(message);
    }
}
