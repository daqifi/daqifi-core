using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// The confirming members of <see cref="IStreamingDevice"/> (issue #1031): each task completes only
/// when the device confirmed the command, and fails otherwise. The replies scripted here are the
/// ones a bench NQ1 (FW 3.9.1) gave.
/// </summary>
public class DaqifiStreamingDeviceConfirmationTests
{
    private const string StateQuery = "SYSTem:StreamData?";
    private const string Stop = "SYSTem:StopStreamData";
    private const string Reboot = "SYSTem:REboot";
    private const string ErrorQuery = "SYSTem:ERRor?";
    private const string RebootAcknowledgement = "System reset initiated";

    private static CancellationToken CanceledToken() => new(canceled: true);

    private static ScriptedDevice ConnectedDevice()
    {
        var device = new ScriptedDevice();
        device.Connect();
        device.Sent.Clear();
        return device;
    }

    private static ScriptedDevice StreamingDevice()
    {
        var device = ConnectedDevice();
        device.StartStreaming();
        device.Sent.Clear();
        return device;
    }

    #region Start

    [Fact]
    public async Task Start_FrameArrives_IsConfirmedWithoutAsking()
    {
        var device = ConnectedDevice();
        device.SendFrameOnStart = true;

        await device.StartStreamingAsync();

        Assert.True(device.IsStreaming);
        Assert.Equal(0, device.Exchanges);
        Assert.Equal(0, device.ErrorQueueReads);
    }

    [Fact]
    public async Task Start_NoFrameButDeviceSaysItIsStreaming_IsConfirmed()
    {
        var device = ConnectedDevice();
        device.Replies.Enqueue(["1"]);

        await device.StartStreamingAsync();

        Assert.True(device.IsStreaming);
        Assert.Equal(StateQuery, device.Sent[^1]);
    }

    [Fact]
    public async Task Start_NoFrameAndDeviceSaysItIsNotStreaming_Throws()
    {
        // A start with nothing enabled is not always refused out loud: the queue is clean, and the
        // device is simply not streaming.
        var device = ConnectedDevice();
        device.Replies.Enqueue(["0"]);

        var failure = await Assert.ThrowsAsync<DeviceCommandFailedException>(() => device.StartStreamingAsync());

        Assert.Null(failure.ErrorCode);
        Assert.Equal("0", failure.DeviceResponse);
        Assert.Contains("StartStreamData", failure.Command, StringComparison.Ordinal);

        // Left true, the next start would be a silent no-op.
        Assert.False(device.IsStreaming);
    }

    [Fact]
    public async Task Start_Refused_ThrowsTheDevicesCode()
    {
        var device = ConnectedDevice();
        device.QueuedErrors.Add("-222,\"Data out of range\"");

        var failure = await Assert.ThrowsAsync<DeviceCommandFailedException>(() => device.StartStreamingAsync());

        Assert.Equal(-222, failure.ErrorCode);
        Assert.Equal(0, device.Exchanges);
        Assert.False(device.IsStreaming);
    }

    [Fact]
    public async Task Start_NoFrameAndNoAnswer_ThrowsAndLeavesTheSessionAsSent()
    {
        var device = ConnectedDevice();

        var failure = await Assert.ThrowsAsync<DeviceCommandFailedException>(() => device.StartStreamingAsync());

        Assert.Null(failure.ErrorCode);
        Assert.Null(failure.DeviceResponse);

        // Unknown, not refused: the start went out and nothing said it did not take.
        Assert.True(device.IsStreaming);
    }

    [Fact]
    public async Task Start_FirstAnswerLostInAStreamFrame_AsksAgain()
    {
        var device = ConnectedDevice();
        device.Replies.Enqueue(["\b\u0096\u008c\u0012\u00020"]);
        device.Replies.Enqueue(["1"]);

        await device.StartStreamingAsync();

        Assert.Equal(2, device.Exchanges);
    }

    [Fact]
    public async Task Start_ErrorQueueUnreadable_StillAsksTheDevice()
    {
        var device = ConnectedDevice();
        device.ErrorQueueFailure = new InvalidOperationException("unreadable");
        device.Replies.Enqueue(["1"]);

        await device.StartStreamingAsync();

        Assert.True(device.IsStreaming);
    }

    [Fact]
    public async Task Start_AlreadyStreaming_SendsNoStartAndConfirmsTheRunningStream()
    {
        var device = StreamingDevice();
        device.QueuedErrors.Add("-200,\"Execution error\"");
        device.Replies.Enqueue(["1"]);

        await device.StartStreamingAsync();

        // The queue's entry is from something else, so it is neither read nor blamed on the start.
        Assert.Equal([StateQuery], device.Sent);
        Assert.Equal(0, device.ErrorQueueReads);
    }

    [Fact]
    public async Task Start_AfterASynchronousStartTheDeviceDidNotTake_Throws()
    {
        var device = StreamingDevice();
        device.Replies.Enqueue(["0"]);

        await Assert.ThrowsAsync<DeviceCommandFailedException>(() => device.StartStreamingAsync());

        Assert.False(device.IsStreaming);
    }

    [Fact]
    public async Task Start_StoppedByAnotherCallerWhileBeingAsked_IsCancelled()
    {
        var device = ConnectedDevice();
        device.Replies.Enqueue(["0"]);
        device.DuringExchange = device.StopStreaming;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => device.StartStreamingAsync());

        Assert.False(device.IsStreaming);
    }

    [Fact]
    public async Task Start_ConnectionEndsWhileWaitingForAFrame_SaysSo()
    {
        var device = ConnectedDevice();
        var start = device.StartStreamingAsync();

        device.Disconnect();

        await Assert.ThrowsAsync<DeviceNotConnectedException>(() => start);
    }

    #endregion

    #region Stop

    [Fact]
    public async Task Stop_DeviceSaysItIsNotStreaming_IsConfirmed()
    {
        var device = StreamingDevice();
        device.Replies.Enqueue(["0"]);

        await device.StopStreamingAsync();

        Assert.False(device.IsStreaming);
        Assert.Equal([Stop, StateQuery], device.Sent);
    }

    [Fact]
    public async Task Stop_WaitsForTheDeviceToCloseAnSdLog()
    {
        // The firmware answers nothing until the stop has finished, and allows itself 5 s.
        var device = StreamingDevice();
        device.Replies.Enqueue(["0"]);

        await device.StopStreamingAsync();

        Assert.True(device.LastResponseTimeoutMs > 5700, $"waited only {device.LastResponseTimeoutMs} ms");
    }

    [Fact]
    public async Task Stop_WhenNotStreaming_StillStopsTheDevice()
    {
        // What this object believes is not what the device is doing: another client, or an earlier
        // session, may have left it streaming.
        var device = ConnectedDevice();
        device.Replies.Enqueue(["0"]);

        await device.StopStreamingAsync();

        Assert.Equal([Stop, StateQuery], device.Sent);
    }

    [Fact]
    public async Task Stop_DeviceSaysItIsStillStreaming_ThrowsAndKeepsTheSession()
    {
        var device = StreamingDevice();
        device.Replies.Enqueue(["1"]);

        var failure = await Assert.ThrowsAsync<DeviceCommandFailedException>(() => device.StopStreamingAsync());

        Assert.Equal(Stop, failure.Command);
        Assert.Equal("1", failure.DeviceResponse);
        Assert.True(device.IsStreaming);
    }

    [Fact]
    public async Task Stop_StillStreamingForAnotherClient_DoesNotInventASession()
    {
        var device = ConnectedDevice();
        device.Replies.Enqueue(["1"]);

        await Assert.ThrowsAsync<DeviceCommandFailedException>(() => device.StopStreamingAsync());

        Assert.False(device.IsStreaming);
    }

    [Fact]
    public async Task Stop_NoAnswer_ThrowsAndCanBeRepeated()
    {
        var device = StreamingDevice();

        var failure = await Assert.ThrowsAsync<DeviceCommandFailedException>(() => device.StopStreamingAsync());
        Assert.Null(failure.DeviceResponse);
        Assert.Null(failure.ErrorCode);

        device.Sent.Clear();
        device.Replies.Enqueue(["0"]);
        await device.StopStreamingAsync();

        Assert.Equal([Stop, StateQuery], device.Sent);
    }

    [Fact]
    public async Task Stop_LastFrameRanIntoTheAnswer_AsksAgain()
    {
        var device = StreamingDevice();
        device.Replies.Enqueue(["\búÒ\u0012\u00020"]);
        device.Replies.Enqueue(["0"]);

        await device.StopStreamingAsync();

        // One stop, two questions: the stop is not what went missing.
        Assert.Equal([Stop, StateQuery, StateQuery], device.Sent);
    }

    [Fact]
    public async Task Stop_CanceledToken_SendsNothing()
    {
        var device = StreamingDevice();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => device.StopStreamingAsync(CanceledToken()));

        Assert.True(device.IsStreaming);
        Assert.Empty(device.Sent);
    }

    #endregion

    #region Reboot

    [Fact]
    public async Task Reboot_Acknowledged_IsConfirmedAndDisconnects()
    {
        var device = ConnectedDevice();
        device.Replies.Enqueue([RebootAcknowledgement]);

        await device.RebootAsync();

        Assert.Equal([Reboot, ErrorQuery], device.Sent);
        Assert.False(device.IsConnected);
    }

    [Fact]
    public async Task Reboot_WhileStreaming_StopsFirst()
    {
        var device = StreamingDevice();
        device.Replies.Enqueue([RebootAcknowledgement]);

        await device.RebootAsync();

        Assert.Equal([Stop, Reboot, ErrorQuery], device.Sent);
        Assert.False(device.IsStreaming);
        Assert.False(device.IsConnected);
    }

    [Fact]
    public async Task Reboot_AcknowledgementBehindStrayBytes_IsStillRead()
    {
        var device = ConnectedDevice();
        device.Replies.Enqueue(["\b\u0012\u0002" + RebootAcknowledgement]);

        await device.RebootAsync();

        Assert.False(device.IsConnected);
    }

    [Fact]
    public async Task Reboot_Refused_ThrowsTheDevicesCodeAndStaysConnected()
    {
        var device = ConnectedDevice();
        device.Replies.Enqueue(["**ERROR: -113, \"Undefined header\"", "-113,\"Undefined header\""]);

        var failure = await Assert.ThrowsAsync<DeviceCommandFailedException>(() => device.RebootAsync());

        Assert.Equal(-113, failure.ErrorCode);
        Assert.True(device.IsConnected);
    }

    [Fact]
    public async Task Reboot_DeviceAnswersTheQueryBehindIt_DidNotReboot()
    {
        // The entry may be an older command's, so its code is not reported as this one's.
        var device = ConnectedDevice();
        device.Replies.Enqueue(["-200,\"Execution error\""]);

        var failure = await Assert.ThrowsAsync<DeviceCommandFailedException>(() => device.RebootAsync());

        Assert.Null(failure.ErrorCode);
        Assert.Equal("-200,\"Execution error\"", failure.DeviceResponse);
        Assert.True(device.IsConnected);
    }

    [Fact]
    public async Task Reboot_NoAnswer_ThrowsAndDisconnects()
    {
        var device = ConnectedDevice();

        var failure = await Assert.ThrowsAsync<DeviceCommandFailedException>(() => device.RebootAsync());

        Assert.Equal(Reboot, failure.Command);
        Assert.Null(failure.DeviceResponse);
        Assert.False(device.IsConnected);
    }

    [Fact]
    public async Task Reboot_LinkFailsAfterTheCommandWentOut_ThrowsAndDisconnects()
    {
        var device = ConnectedDevice();
        device.DuringExchange = () => throw new TransportNotConnectedException("gone");

        await Assert.ThrowsAsync<TransportNotConnectedException>(() => device.RebootAsync());

        Assert.False(device.IsConnected);
    }

    [Fact]
    public async Task Reboot_CanceledWhileWaiting_ThrowsAndDisconnects()
    {
        using var cancellation = new CancellationTokenSource();
        var device = ConnectedDevice();
        device.DuringExchange = () =>
        {
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => device.RebootAsync(cancellation.Token));

        Assert.False(device.IsConnected);
    }

    [Fact]
    public async Task Reboot_CanceledToken_SendsNothingAndStaysConnected()
    {
        var device = ConnectedDevice();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => device.RebootAsync(CanceledToken()));

        Assert.True(device.IsConnected);
        Assert.Empty(device.Sent);
    }

    [Fact]
    public async Task Reboot_CanceledTokenOnADisconnectedDevice_IsACancellation()
    {
        // A caller can then tell "this call was cancelled" from "the device was not connected" even
        // when both are true at once.
        var device = new ScriptedDevice();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => device.RebootAsync(CanceledToken()));
    }

    #endregion

    [Fact]
    public void IStreamingDevice_HasNoDefaultBodiedAsyncMember()
    {
        // A default body is how an ...Async member came to mean "sent" on the interface and
        // "confirmed" on the class.
        var defaultBodied = typeof(IStreamingDevice).GetMethods()
            .Where(m => m.Name.EndsWith("Async", StringComparison.Ordinal) && !m.IsAbstract)
            .Select(m => m.Name);

        Assert.Empty(defaultBodied);
    }

    /// <summary>
    /// A device whose text exchanges are scripted: each one takes the next entry of
    /// <see cref="Replies"/>, and a device with none left is silent.
    /// </summary>
    private sealed class ScriptedDevice : DaqifiStreamingDevice
    {
        public ScriptedDevice()
            : base("Scripted")
        {
            StreamingFrequency = 1000;
        }

        public List<string> Sent { get; } = new();

        public Queue<string[]> Replies { get; } = new();

        public List<string> QueuedErrors { get; } = new();

        public Exception? ErrorQueueFailure { get; set; }

        public int ErrorQueueReads { get; private set; }

        public int Exchanges { get; private set; }

        public int LastResponseTimeoutMs { get; private set; }

        public bool SendFrameOnStart { get; set; }

        /// <summary>Runs once the exchange has sent its commands, before its reply is read.</summary>
        public Action? DuringExchange { get; set; }

        public override void Send<T>(IOutboundMessage<T> message)
        {
            if (message is not IOutboundMessage<string> command)
            {
                return;
            }

            Sent.Add(command.Data);

            // What the real Send does for a raw command: the session follows what went out.
            if (command.Data == ScpiMessageProducer.StopStreaming.Data)
            {
                ((Daqifi.Core.Device.Internal.IDeviceOperationHost)this).EndStreamingSession();
            }

            if (SendFrameOnStart && command.Data.Contains("StartStreamData", StringComparison.Ordinal))
            {
                OnStreamMessageReceived(new DaqifiOutMessage { MsgTimeStamp = 1000 });
            }
        }

        public override Task<IReadOnlyList<string>> DrainErrorQueueAsync(
            int maxIterations = 256,
            CancellationToken cancellationToken = default)
        {
            ErrorQueueReads++;
            return ErrorQueueFailure != null
                ? Task.FromException<IReadOnlyList<string>>(ErrorQueueFailure)
                : Task.FromResult<IReadOnlyList<string>>(QueuedErrors.ToList());
        }

        protected override Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Action setupAction,
            int responseTimeoutMs = 1000,
            int completionTimeoutMs = 250,
            CancellationToken cancellationToken = default,
            Func<CancellationToken, Task>? prepareAsync = null,
            Func<Task>? finalizeAsync = null,
            bool keepBlankLines = false)
        {
            Exchanges++;
            LastResponseTimeoutMs = responseTimeoutMs;
            setupAction();
            DuringExchange?.Invoke();
            return Task.FromResult<IReadOnlyList<string>>(
                Replies.TryDequeue(out var reply) ? reply : []);
        }
    }
}
