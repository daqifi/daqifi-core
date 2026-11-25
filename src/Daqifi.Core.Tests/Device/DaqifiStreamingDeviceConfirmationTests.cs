using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Internal;
using Daqifi.Core.Device.SdCard;
using Daqifi.Core.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
        Assert.Equal(["0"], failure.RawDeviceResponse);
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
        Assert.Empty(failure.RawDeviceResponse);

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
    public async Task Start_RunningStreamHasGoneQuiet_IsNotConfirmedByItsOldFrames()
    {
        var device = ConnectedDevice();
        device.SendFrameOnStart = true;
        await device.StartStreamingAsync();

        // The device stops by itself, and says so when asked.
        device.Replies.Enqueue(["0"]);

        await Assert.ThrowsAsync<DeviceCommandFailedException>(() => device.StartStreamingAsync());

        Assert.False(device.IsStreaming);
        Assert.Equal(1, device.Exchanges);
    }

    [Fact]
    public async Task Start_RunningStreamStillDelivering_IsConfirmedByItsNextFrame()
    {
        var device = ConnectedDevice();
        device.StreamingFrequency = 10;
        device.SendFrameOnStart = true;
        await device.StartStreamingAsync();

        var again = device.StartStreamingAsync();
        Assert.False(again.IsCompleted);
        device.SendFrame();
        await again;

        Assert.Equal(0, device.Exchanges);
    }

    [Fact]
    public async Task Start_OnlyFrameIsLeftOverFromThePreviousSession_IsNotConfirmedByIt()
    {
        // The frame the device latches at a stop and sends at the next start: one sample period past
        // the last one seen, where a genuine first frame is a whole stop-to-start gap away.
        var device = ConnectedDevice();
        device.SendFrameOnStart = true;
        await device.StartStreamingAsync();
        device.StopStreaming();

        device.SendFrameOnStart = false;
        device.OnStartSent = () => device.SendFrame(ticksSinceLast: 50_000);
        device.Replies.Enqueue(["0"]);

        await Assert.ThrowsAsync<DeviceCommandFailedException>(() => device.StartStreamingAsync());
    }

    [Fact]
    public async Task Start_RightAfterAStop_LetsTheOldStreamsFramesArriveFirst()
    {
        // Frames already on their way when the stop went out must not pass for the new stream's.
        var device = ConnectedDevice();
        device.SendFrameOnStart = true;
        await device.StartStreamingAsync();
        device.StopStreaming();
        device.Sent.Clear();

        device.SendFrameOnStart = false;
        device.Replies.Enqueue(["0"]);
        var start = device.StartStreamingAsync();
        Assert.Empty(device.Sent);
        for (var i = 0; i < 20; i++)
        {
            device.SendFrame(ticksSinceLast: 50_000);
        }

        await Assert.ThrowsAsync<DeviceCommandFailedException>(() => start);
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

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData(null)]
    public async Task Start_ReplacedByAnotherCallersStreamWhileBeingAsked_LeavesThatStreamAlone(string? reply)
    {
        // Whatever the device answered, the answer is about a stream this call did not start.
        var device = ConnectedDevice();
        if (reply != null)
        {
            device.Replies.Enqueue([reply]);
        }

        device.DuringExchange = () =>
        {
            device.DuringExchange = null;
            device.StopStreaming();
            device.StartStreaming();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => device.StartStreamingAsync());

        Assert.True(device.IsStreaming);
    }

    [Fact]
    public async Task Start_ReplacedWhileTheErrorQueueIsRead_LeavesTheReplacementAlone()
    {
        var device = ConnectedDevice();
        device.QueuedErrors.Add("-222,\"Data out of range\"");
        device.DuringErrorQueueRead = () =>
        {
            device.StopStreaming();
            device.StartStreaming();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => device.StartStreamingAsync());

        Assert.True(device.IsStreaming);
    }

    [Fact]
    public async Task Start_ReplacedWhileWaitingForAFrame_IsNotConfirmedByTheReplacementsFrames()
    {
        var device = ConnectedDevice();
        device.StreamingFrequency = 10;
        var start = device.StartStreamingAsync();

        device.StopStreaming();
        device.StartStreaming();
        device.SendFrame();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.True(device.IsStreaming);
    }

    [Fact]
    public async Task Start_ConnectionEndsWhileWaitingForAFrame_SaysSo()
    {
        var device = ConnectedDevice();
        var start = device.StartStreamingAsync();

        device.Disconnect();

        await Assert.ThrowsAsync<DeviceNotConnectedException>(() => start);
    }

    [Fact]
    public async Task Start_LosingToAnotherCallersStart_LeavesTheWinnersDecoderAlone()
    {
        // The loser gets as far as the reader check and is held there while the winner starts the
        // stream and receives its first frame. What the loser does then must not cost the winner
        // that frame.
        var clock = new FakeTimeProvider();
        var device = ConnectedDevice();
        device.SetTimeProviderForTesting(clock);
        using var reader = new HeldReader(device);

        var loser = Task.Run(device.StartStreaming);
        reader.WaitUntilHeld();
        var winner = device.StartStreamingAsync();
        device.SendFrame();
        reader.Release();
        await loser;
        await FakeClockPump.UntilAsync(clock, winner, TimeSpan.FromMilliseconds(20));
        await winner;

        Assert.Equal(["SYSTem:StartStreamData 1000"], device.Sent);
        Assert.Equal(0, device.Exchanges);
    }

    [Fact]
    public async Task Start_LosingToAnotherCallersStart_LeavesTheRateTheStreamRunsAt()
    {
        var device = ConnectedDevice();
        using var reader = new HeldReader(device);

        var loser = Task.Run(device.StartStreaming);
        reader.WaitUntilHeld();
        device.StartStreaming();
        device.UpdateMetadata(metadata => metadata with
        {
            Capabilities = metadata.Capabilities with { MaxSamplingRate = 10 }
        });
        reader.Release();
        await loser;

        Assert.Equal(["SYSTem:StartStreamData 1000"], device.Sent);
        Assert.Equal(1000, device.StreamingFrequency);
    }

    [Fact]
    public async Task Start_AnotherCallerStopsBeforeTheCommandIsOut_GoesOutAheadOfThatStop()
    {
        // The other way round, the device streams with no session recorded.
        var device = ConnectedDevice();
        var stop = device.DuringSendOf("StartStreamData", device.StopStreaming);

        device.StartStreaming();
        await stop();

        Assert.Equal(["SYSTem:StartStreamData 1000", Stop], device.Sent);
        Assert.False(device.IsStreaming);
    }

    [Fact]
    public void Start_CommandCannotBeSent_LeavesNoSession()
    {
        var device = ConnectedDevice();
        device.BeforeSend = _ => throw new TransportNotConnectedException("gone");

        Assert.Throws<TransportNotConnectedException>(device.StartStreaming);

        Assert.False(device.IsStreaming);
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

        Assert.True(device.LastResponseTimeout > TimeSpan.FromMilliseconds(5700), $"waited only {device.LastResponseTimeout}");
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
        Assert.Equal(["1"], failure.RawDeviceResponse);
        Assert.True(device.IsStreaming);
    }

    [Fact]
    public async Task Stop_RestartedAndStoppedByAnotherCallerWhileBeingAsked_DoesNotReviveTheOldSession()
    {
        var device = StreamingDevice();
        device.Replies.Enqueue(["1"]);
        device.DuringExchange = () =>
        {
            device.StartStreaming();
            device.StopStreaming();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => device.StopStreamingAsync());

        Assert.False(device.IsStreaming);
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
        Assert.Empty(failure.RawDeviceResponse);
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

    [Fact]
    public async Task Stop_AnotherCallerStartsBeforeTheCommandIsOut_GoesOutAheadOfThatStart()
    {
        // The other way round, the device is stopped with a session recorded.
        var device = StreamingDevice();
        var start = device.DuringSendOf(Stop, device.StartStreaming);

        device.StopStreaming();
        await start();

        Assert.Equal([Stop, "SYSTem:StartStreamData 1000"], device.Sent);
        Assert.True(device.IsStreaming);
    }

    [Fact]
    public async Task SdOperationsStop_AnotherCallerStartsMeanwhile_LeavesThatStartRecorded()
    {
        var device = StreamingDevice();
        var start = device.DuringSendOf(Stop, device.StartStreaming);

        Assert.True(((IDeviceOperationHost)device).SendStreamStop());
        await start();

        Assert.Equal([Stop, "SYSTem:StartStreamData 1000"], device.Sent);
        Assert.True(device.IsStreaming);
    }

    [Fact]
    public async Task SdLogStart_AnotherCallerStopsMeanwhile_EndsTheLog()
    {
        var device = ConnectedDevice();
        var stop = device.DuringSendOf("StartStreamData", device.StopStreaming);

        ((ISdCardOperationHost)device).StartSdCardLogStream(1000);
        await stop();

        Assert.Equal(["SYSTem:StartStreamData 1000", Stop], device.Sent.Take(2));
        Assert.False(device.IsStreaming);
    }

    [Fact]
    public async Task Stop_OvertakenByAnotherCallersStart_LeavesThatStartToBeConfirmed()
    {
        var device = StreamingDevice();
        device.StreamingFrequency = 10;
        device.Replies.Enqueue(["1"]);
        Task? start = null;
        device.DuringExchange = () =>
        {
            device.DuringExchange = null;
            start = device.StartStreamingAsync();
            SpinWait.SpinUntil(() => device.IsStreaming, TimeSpan.FromSeconds(5));
        };

        // The "1" is that stream's, so it is neither a failure of this stop nor a reason to bring
        // the stopped session back.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => device.StopStreamingAsync());

        Assert.True(device.IsStreaming);
        device.SendFrame();
        await start!;
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
        Assert.Equal(["-200,\"Execution error\""], failure.RawDeviceResponse);
        Assert.True(device.IsConnected);
    }

    [Fact]
    public async Task Reboot_NoAnswer_ThrowsAndDisconnects()
    {
        var device = ConnectedDevice();

        var failure = await Assert.ThrowsAsync<DeviceCommandFailedException>(() => device.RebootAsync());

        Assert.Equal(Reboot, failure.Command);
        Assert.Empty(failure.RawDeviceResponse);
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

        public Action? DuringErrorQueueRead { get; set; }

        public int Exchanges { get; private set; }

        public TimeSpan? LastResponseTimeout { get; private set; }

        public bool SendFrameOnStart { get; set; }

        /// <summary>
        /// Delivers a stream frame: by default one second of device clock after the last, which is
        /// what a genuine first frame looks like.
        /// </summary>
        public void SendFrame(uint ticksSinceLast = 50_000_000) =>
            OnStreamMessageReceived(new DaqifiOutMessage { MsgTimeStamp = _deviceTicks += ticksSinceLast });

        private uint _deviceTicks;

        /// <summary>Runs after a start command was sent.</summary>
        public Action? OnStartSent { get; set; }

        /// <summary>Runs once the exchange has sent its commands, before its reply is read.</summary>
        public Action? DuringExchange { get; set; }

        /// <summary>Runs before a command is recorded as sent.</summary>
        public Action<string>? BeforeSend { get; set; }

        /// <summary>
        /// Runs <paramref name="action"/> on another thread when the first command containing
        /// <paramref name="text"/> is on its way out, and gives it time to overtake that command.
        /// </summary>
        /// <returns>Waits for the other thread to finish.</returns>
        public Func<Task> DuringSendOf(string text, Action action)
        {
            Task? other = null;
            BeforeSend = command =>
            {
                if (command.Contains(text, StringComparison.Ordinal))
                {
                    BeforeSend = null;
                    other = Task.Factory.StartNew(
                        action, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    other.Wait(TimeSpan.FromMilliseconds(200));
                }
            };

            return () => other ?? throw new InvalidOperationException($"{text} was not sent.");
        }

        public override void Send<T>(IOutboundMessage<T> message)
        {
            if (message is not IOutboundMessage<string> command)
            {
                return;
            }

            BeforeSend?.Invoke(command.Data);
            Sent.Add(command.Data);

            if (command.Data.Contains("StartStreamData", StringComparison.Ordinal))
            {
                OnStartSent?.Invoke();
            }

            if (SendFrameOnStart && command.Data.Contains("StartStreamData", StringComparison.Ordinal))
            {
                SendFrame();
            }
        }

        public override Task<IReadOnlyList<string>> DrainErrorQueueAsync(
            int maxIterations = 256,
            CancellationToken cancellationToken = default)
        {
            ErrorQueueReads++;
            DuringErrorQueueRead?.Invoke();
            return ErrorQueueFailure != null
                ? Task.FromException<IReadOnlyList<string>>(ErrorQueueFailure)
                : Task.FromResult<IReadOnlyList<string>>(QueuedErrors.ToList());
        }

        protected override Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Action setupAction,
            TimeSpan? responseTimeout = null,
            TimeSpan? completionTimeout = null,
            Func<CancellationToken, Task>? prepareAsync = null,
            Func<Task>? finalizeAsync = null,
            bool keepBlankLines = false,
            CancellationToken cancellationToken = default)
        {
            Exchanges++;
            LastResponseTimeout = responseTimeout;
            setupAction();
            DuringExchange?.Invoke();
            return Task.FromResult<IReadOnlyList<string>>(
                Replies.TryDequeue(out var reply) ? reply : []);
        }
    }

    /// <summary>
    /// Stands in for the device's message reader and holds the first caller that asks whether it is
    /// running, which is a start that has seen an idle device and not yet claimed it.
    /// </summary>
    private sealed class HeldReader : IMessageConsumer<WireMessage>
    {
        private readonly ManualResetEventSlim _held = new();
        private readonly ManualResetEventSlim _release = new();
        private readonly FieldInfo _field =
            typeof(DaqifiDevice).GetField("_messageConsumer", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private readonly DaqifiDevice _device;
        private int _asked;

        public HeldReader(DaqifiDevice device)
        {
            _device = device;
            _field.SetValue(device, this);
        }

        public bool IsRunning
        {
            get
            {
                if (Interlocked.Increment(ref _asked) == 1)
                {
                    _held.Set();
                    _release.Wait(TimeSpan.FromSeconds(10));
                }

                return true;
            }
        }

        public int QueuedMessageCount => 0;

        public event EventHandler<MessageReceivedEventArgs<WireMessage>>? MessageReceived { add { } remove { } }

        public event EventHandler<MessageConsumerErrorEventArgs>? ErrorOccurred { add { } remove { } }

        public void WaitUntilHeld() => Assert.True(_held.Wait(TimeSpan.FromSeconds(10)));

        public void Release() => _release.Set();

        public void Start()
        {
        }

        public void Stop()
        {
        }

        public bool StopSafely(int timeoutMs = 1000) => true;

        public void ClearBuffer()
        {
        }

        public void Dispose()
        {
            _release.Set();
            _field.SetValue(_device, null);
        }
    }
}
