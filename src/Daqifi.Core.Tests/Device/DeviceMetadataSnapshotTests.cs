using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Capabilities;
using Daqifi.Core.Tests.Device.Capabilities;
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

    [Fact]
    public void AConsumerCannotChangeTheChannelCount_ThroughAHeldSnapshotsDocument()
    {
        Assert.True(CapabilityDocumentParser.TryParse(
            "{\"schema_version\":2,\"channels\":[{\"id\":0,\"kind\":\"analog-input\"},{\"id\":1,\"kind\":\"analog-input\"}]}",
            out var document));
        var device = new StatusDrivenDevice();
        device.UpdateMetadata(metadata => metadata.WithCapabilityDocument(document!));
        var held = device.Metadata;

        Assert.Throws<NotSupportedException>(
            () => ((IList<CapabilityChannel>)held.CapabilityDocument!.Channels).RemoveAt(0));
        device.ReceiveStatus(new DaqifiOutMessage { DevicePn = "Nq1" });

        Assert.Equal(2, held.CapabilityDocument!.Channels.Count);
        Assert.Equal(2, device.Metadata.Capabilities.AnalogInputChannels);
    }

    [Fact]
    public void EveryObjectReachableFromTheMetadata_IsReadOnly()
    {
        Assert.True(CapabilityDocumentParser.TryParse(
            CapabilityDocumentSamples.Nyquist1Firmware372, out var document));
        var visited = new HashSet<Type>();

        AssertReadOnly(new DeviceMetadata().WithCapabilityDocument(document!), visited);

        // The walk reached both ends of the graph, including the nested streaming block.
        Assert.Contains(typeof(DeviceHealth), visited);
        Assert.Contains(typeof(CapabilityChannel), visited);
        Assert.Contains(typeof(CapabilityRateModel), visited);
    }

    [Fact]
    public void ADocumentBuiltByACaller_OwnsItsCollections()
    {
        // A wrapper or test double builds its own document and keeps the list it passed in.
        var channels = new List<CapabilityChannel> { new() { Id = 0 } };
        string[] encodings = ["protobuf"];
        string[] transports = ["usb"];
        var document = new CapabilityDocument
        {
            Channels = channels,
            Streaming = new CapabilityStreaming { Encodings = encodings, Transports = transports },
        };

        channels.Clear();
        encodings[0] = transports[0] = "changed";

        Assert.Single(document.Channels);
        Assert.Equal(["protobuf"], document.Streaming.Encodings);
        Assert.Equal(["usb"], document.Streaming.Transports);
        AssertReadOnly(document, []);
    }

    /// <summary>
    /// Fails on a collection that can be written through a cast, or a property with a plain
    /// setter, anywhere in the object graph.
    /// </summary>
    private static void AssertReadOnly(object? value, HashSet<Type> visited)
    {
        if (value is null or string || value.GetType().IsValueType)
        {
            return;
        }

        if (value is IEnumerable items)
        {
            // An array reports IList.IsReadOnly as false through the non-generic interface, and
            // its elements can be assigned; a ReadOnlyCollection reports true.
            Assert.True(
                value is IList { IsReadOnly: true } or Array { Length: 0 },
                $"{value.GetType()} can be written through a cast.");
            foreach (var item in items)
            {
                AssertReadOnly(item, visited);
            }

            return;
        }

        visited.Add(value.GetType());
        foreach (var property in value.GetType().GetProperties())
        {
            Assert.True(
                property.SetMethod is not { IsPublic: true } setter
                || setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit)),
                $"{property.DeclaringType}.{property.Name} has a public setter.");
            AssertReadOnly(property.GetValue(value), visited);
        }
    }

    private sealed class StatusDrivenDevice() : DaqifiDevice("Test Device")
    {
        public void ReceiveStatus(DaqifiOutMessage message) => OnStatusMessageReceived(message);
    }
}
