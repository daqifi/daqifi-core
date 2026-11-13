using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Capabilities;
using Xunit;

namespace Daqifi.Core.Tests.Device;

public class DaqifiStreamingDevicePwmFrequencyTests
{
    [Fact]
    public void SetPwmFrequency_FirstCall_SendsAndTracksFrequency()
    {
        var device = new CapturingStreamingDevice();
        device.Connect();

        device.SetPwmFrequency(2000);

        Assert.Equal(new[] { "PWM:CHannel:FREQuency 0,2000" }, device.PwmFrequencySends);
        Assert.Equal(2000, device.PwmFrequencyHz);
    }

    [Fact]
    public void SetPwmFrequency_SameValueTwice_SecondIsSkipped()
    {
        var device = new CapturingStreamingDevice();
        device.Connect();

        device.SetPwmFrequency(2000);
        device.SetPwmFrequency(2000); // unchanged -> no second send

        Assert.Single(device.PwmFrequencySends);
        Assert.Equal(2000, device.PwmFrequencyHz);
    }

    [Fact]
    public void SetPwmFrequency_ChangedValue_Sends()
    {
        var device = new CapturingStreamingDevice();
        device.Connect();

        device.SetPwmFrequency(2000);
        device.SetPwmFrequency(2000); // skipped
        device.SetPwmFrequency(3000); // changed -> sends
        device.SetPwmFrequency(3000); // skipped

        Assert.Equal(
            new[] { "PWM:CHannel:FREQuency 0,2000", "PWM:CHannel:FREQuency 0,3000" },
            device.PwmFrequencySends);
        Assert.Equal(3000, device.PwmFrequencyHz);
    }

    [Fact]
    public void SetPwmFrequency_AfterReconnect_SendsAgainEvenIfUnchanged()
    {
        var device = new CapturingStreamingDevice();

        device.Connect();
        device.SetPwmFrequency(2000);
        device.Disconnect(); // clears the "already-sent" cache

        device.Connect();
        device.SetPwmFrequency(2000); // fresh connection must re-send

        Assert.Equal(
            new[] { "PWM:CHannel:FREQuency 0,2000", "PWM:CHannel:FREQuency 0,2000" },
            device.PwmFrequencySends);
    }

    [Fact]
    public void SetPwmFrequency_NotConnected_ThrowsAndSendsNothing()
    {
        var device = new CapturingStreamingDevice(); // not connected
        Assert.Throws<DeviceNotConnectedException>(() => device.SetPwmFrequency(2000));
        Assert.Empty(device.PwmFrequencySends);
    }

    [Theory]
    [InlineData(5)]      // below MinPwmFrequencyHz (6)
    [InlineData(50_001)] // above MaxPwmFrequencyHz (50000)
    public void SetPwmFrequency_OutOfRange_ThrowsBeforeConnectedCheck(int hz)
    {
        var device = new CapturingStreamingDevice();
        device.Connect();
        Assert.Throws<System.ArgumentOutOfRangeException>(() => device.SetPwmFrequency(hz));
        Assert.Empty(device.PwmFrequencySends);
    }

    /// <summary>
    /// The PWM pins of a capability document in the shape firmware v3.9.0 emits them
    /// (<c>EmitDioChannelJson</c>), where <c>min_freq_hz</c> is the floor the firmware computes
    /// from its clock rather than the fixed 1 Hz earlier releases print.
    /// </summary>
    private static string DocumentAdvertisingPwmFloor(int floorHz)
    {
        var pwm = $"\"pwm\":{{\"min_freq_hz\":{floorHz},\"max_freq_hz\":50000,\"resolution_bits\":16}}";
        var pins = string.Join(",", new[] { 0, 1, 3 }.Select(id => id == 1
            ? $"{{\"id\":{id},\"kind\":\"digital-io\",\"features\":{{\"input\":true,\"output\":true}},\"extensions\":{{}}}}"
            : $"{{\"id\":{id},\"kind\":\"digital-io\",\"features\":{{\"input\":true,\"output\":true,{pwm}}},\"extensions\":{{}}}}"));
        return $"{{\"schema_version\":2,\"channels\":[{pins}]}}";
    }

    private static CapturingStreamingDevice ConnectedDeviceOn(string firmwareVersion, int advertisedFloorHz)
    {
        var device = new CapturingStreamingDevice();
        device.Connect();
        device.Metadata.FirmwareVersion = firmwareVersion;
        Assert.True(CapabilityDocumentParser.TryParse(
            DocumentAdvertisingPwmFloor(advertisedFloorHz), out var document));
        device.Metadata.ApplyCapabilityDocument(document);
        return device;
    }

    [Theory]
    [InlineData(8)] // a unit whose clock puts the floor above the fixed 6 Hz
    [InlineData(5)] // and one whose clock puts it below
    public void SetPwmFrequency_OnV3_9_0_UsesTheFloorTheDeviceAdvertises(int floorHz)
    {
        var device = ConnectedDeviceOn("3.9.0", floorHz);

        Assert.Equal(floorHz, device.PwmFrequencyFloorHz);
        Assert.Throws<ArgumentOutOfRangeException>(() => device.SetPwmFrequency(floorHz - 1));
        Assert.Empty(device.PwmFrequencySends);

        device.SetPwmFrequency(floorHz);

        Assert.Equal(new[] { $"PWM:CHannel:FREQuency 0,{floorHz}" }, device.PwmFrequencySends);
    }

    [Fact]
    public void SetPwmFrequency_BeforeV3_9_0_KeepsTheFixedFloorOverTheAdvertisedOne()
    {
        // v3.8.0 prints min_freq_hz 1 on every PWM pin, a frequency the hardware cannot produce.
        var device = ConnectedDeviceOn("3.8.0", 1);

        Assert.Equal(DaqifiStreamingDevice.MinPwmFrequencyHz, device.PwmFrequencyFloorHz);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => device.SetPwmFrequency(DaqifiStreamingDevice.MinPwmFrequencyHz - 1));
        Assert.Empty(device.PwmFrequencySends);
    }

    [Fact]
    public void PwmFrequencyFloorHz_WithNoCapabilityDocument_IsTheFixedFloor()
    {
        var device = new CapturingStreamingDevice();
        device.Metadata.FirmwareVersion = "3.9.0";

        Assert.Equal(DaqifiStreamingDevice.MinPwmFrequencyHz, device.PwmFrequencyFloorHz);
    }

    [Fact]
    public void SetPwmFrequency_AfterConnectionLost_ReconnectReSendsEvenIfUnchanged()
    {
        // An unexpected transport drop sets ConnectionStatus.Lost (not Disconnected); the cache
        // must still clear so a reconnect re-sends (the device's PWM state isn't trustworthy).
        // using var guarantees disposal even if an assertion throws — device before transport
        // (reverse declaration order), so the consumer/producer stop before the stream closes.
        using var transport = new DropTransport();
        using var device = new CapturingStreamingDevice(transport);

        device.Connect();
        device.SetPwmFrequency(2000);

        transport.SimulateLoss(); // device -> Lost, cache cleared

        device.Connect();
        device.SetPwmFrequency(2000); // unchanged value, but must re-send after a Lost

        Assert.Equal(2, device.PwmFrequencySends.Count);
    }

    private sealed class DropTransport : IStreamTransport
    {
        private readonly MemoryStream _stream = new();
        public Stream Stream => _stream;
        public bool IsConnected { get; private set; }
        public string ConnectionInfo => IsConnected ? "Mock: Connected" : "Mock: Disconnected";
        public event EventHandler<TransportStatusEventArgs>? StatusChanged;

        public Task ConnectAsync() => ConnectAsync(null);
        public Task ConnectAsync(ConnectionRetryOptions? retryOptions)
        {
            IsConnected = true;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(true, ConnectionInfo));
            return Task.CompletedTask;
        }
        public Task DisconnectAsync()
        {
            IsConnected = false;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(false, ConnectionInfo));
            return Task.CompletedTask;
        }
        public void Connect() => ConnectAsync().Wait();
        public void Disconnect() => DisconnectAsync().Wait();

        /// <summary>Raises an unexpected drop (StatusChanged(false) without an intentional Disconnect).</summary>
        public void SimulateLoss()
        {
            IsConnected = false;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(false, "Connection lost"));
        }

        public void Dispose() => _stream.Dispose();
    }

    private sealed class CapturingStreamingDevice : DaqifiStreamingDevice
    {
        public CapturingStreamingDevice() : base("TestDevice") { }
        public CapturingStreamingDevice(IStreamTransport transport) : base("TestDevice", transport) { }

        private readonly List<string> _sent = new();

        /// <summary>The PWM-frequency SCPI commands actually sent (skips are absent).</summary>
        public IReadOnlyList<string> PwmFrequencySends =>
            _sent.Where(s => s.StartsWith("PWM:CHannel:FREQuency")).ToList();

        public override void Send<T>(IOutboundMessage<T> message)
        {
            if (message is IOutboundMessage<string> stringMessage)
            {
                _sent.Add(stringMessage.Data);
            }
        }
    }
}
