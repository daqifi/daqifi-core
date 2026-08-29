using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Pins the parts of <see cref="DaqifiStreamingDevice"/>'s streaming-session behavior that the
/// rest of the suite only asserts weakly, or not at all: the exception metadata on the
/// <see cref="DaqifiStreamingDevice.StreamingFrequency"/> guard, and the order in which
/// <see cref="DaqifiStreamingDevice.IsStreaming"/> moves relative to the command that carries it
/// onto the wire.
/// </summary>
/// <remarks>
/// <para>
/// These exist because the surrounding tests assert the observable end state and stop there.
/// <c>StreamingFrequency_OutOfRange_ThrowsArgumentOutOfRangeException</c> checks the exception
/// type and that the ceiling appears in the message, but never the <c>ParamName</c> — so the
/// name a caller's <c>catch</c> block switches on is unpinned. And
/// <c>StartStreaming_WhenConnected_SendsCorrectCommandAndSetsIsStreaming</c> reads
/// <c>IsStreaming</c> after the call returns, which cannot distinguish "flag set before the
/// send" from "flag set after it" — an ordering the SD-card operations depend on and document
/// (see <c>SdCardOperations</c>, issue #118).
/// </para>
/// <para>
/// Written and run against the unchanged code before the streaming-session state was moved onto
/// a collaborator (#344), so they are a baseline rather than a description of the new shape.
/// </para>
/// </remarks>
public class StreamingSessionGuardTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    public void StreamingFrequency_OutOfRange_NamesTheParameterAndCarriesTheRejectedValue(int frequency)
    {
        var device = new DaqifiStreamingDevice("TestDevice");

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => device.StreamingFrequency = frequency);

        // The name is what a caller's handler keys on, and it is what a mechanical move of this
        // property onto another type would silently change (nameof follows the declaration).
        Assert.Equal(nameof(DaqifiStreamingDevice.StreamingFrequency), exception.ParamName);
        Assert.Equal(frequency, exception.ActualValue);
    }

    [Fact]
    public void StartStreaming_SetsIsStreaming_BeforeTheStartCommandIsSent()
    {
        var device = new SendObservingDevice("TestDevice");
        device.Connect();
        device.StreamingFrequency = 200;

        device.StartStreaming();

        var observed = Assert.Single(device.IsStreamingAtSend);
        Assert.True(observed, "IsStreaming must already be true when the start command is sent.");
    }

    [Fact]
    public void StopStreaming_ClearsIsStreaming_BeforeTheStopCommandIsSent()
    {
        var device = new SendObservingDevice("TestDevice");
        device.Connect();
        device.StartStreaming();
        device.IsStreamingAtSend.Clear();

        device.StopStreaming();

        var observed = Assert.Single(device.IsStreamingAtSend);
        Assert.False(observed, "IsStreaming must already be false when the stop command is sent.");
    }

    [Fact]
    public void StartStreaming_WhileAlreadyStreaming_SendsNothingFurther()
    {
        var device = new SendObservingDevice("TestDevice");
        device.Connect();
        device.StartStreaming();
        device.IsStreamingAtSend.Clear();

        device.StartStreaming();

        Assert.Empty(device.IsStreamingAtSend);
    }

    [Fact]
    public void StopStreaming_WhileNotStreaming_SendsNothing()
    {
        var device = new SendObservingDevice("TestDevice");
        device.Connect();

        device.StopStreaming();

        Assert.Empty(device.IsStreamingAtSend);
    }

    [Fact]
    public async Task RestoreSessionSnapshot_WithNullOptions_NamesTheOptionsParameter()
    {
        var device = new RestoreExposingDevice("TestDevice");

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(
            () => device.RestoreAsync(null!, CancellationToken.None));

        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public async Task RestoreSessionSnapshot_WithNoSnapshot_ClearsIsStreamingAndReportsNoResume()
    {
        // The flag is still set from before the drop — nothing stopped the stream, the
        // connection simply ended — and a reconnected device is never streaming. Pinned here
        // because it is the one effect the restore has even when there is nothing to restore.
        var device = new RestoreExposingDevice("TestDevice");
        device.Connect();
        device.StartStreaming();
        Assert.True(device.IsStreaming);

        var resumed = await device.RestoreAsync(new ReconnectOptions(), CancellationToken.None);

        Assert.False(resumed);
        Assert.False(device.IsStreaming);
    }

    /// <summary>
    /// Records what <see cref="DaqifiStreamingDevice.IsStreaming"/> read at the instant each
    /// command was handed to <c>Send</c>, which is the only way to observe the ordering.
    /// </summary>
    private sealed class SendObservingDevice : DaqifiStreamingDevice
    {
        public SendObservingDevice(string name) : base(name)
        {
        }

        public List<bool> IsStreamingAtSend { get; } = new();

        public override void Send<T>(IOutboundMessage<T> message)
        {
            IsStreamingAtSend.Add(IsStreaming);

            // Deliberately not calling base: this device has no transport, and the session
            // tracking base.Send performs is exercised by DeviceReconnectTests instead.
        }
    }

    /// <summary>
    /// Exposes the protected reconnect-restore hook so its argument contract can be asserted
    /// directly rather than only through a full reconnect scenario.
    /// </summary>
    private sealed class RestoreExposingDevice : DaqifiStreamingDevice
    {
        public RestoreExposingDevice(string name) : base(name)
        {
        }

        public Task<bool> RestoreAsync(ReconnectOptions options, CancellationToken cancellationToken)
            => RestoreSessionSnapshotAsync(options, cancellationToken);

        public override void Send<T>(IOutboundMessage<T> message)
        {
            // No transport in these tests; swallow the wire traffic.
        }
    }
}
