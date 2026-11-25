using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Internal;
using Daqifi.Core.Communication.Messages;
using Microsoft.Extensions.Time.Testing;
using System.Collections.Concurrent;
using System.Reflection;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Regression coverage for issue #383 — a connect must not fail with "a previous consumer thread
/// has not yet exited" — and for what replaced the restart that issue was about.
/// </summary>
/// <remarks>
/// A text exchange used to stop the reader and restart it, so a reader parked in a slow read could
/// still be alive at the restart and the double-reader guard rightly refused it. Since issue #900 a
/// text exchange never stops the reader, so on the text path there is nothing to restart. A raw
/// capture still stops and restarts it, and a stream stuck outright still leaves it stopped; what
/// changed is that the next text exchange now says so instead of reporting silence.
/// </remarks>
public class DaqifiDeviceConsumerRestartTests
{
    // Start()'s grace period is covered precisely in StreamMessageConsumerStallingReaderTests.

    [Fact]
    public async Task ExecuteTextCommand_OnAStuckStream_LeavesTheSameReaderRunning()
    {
        // A stream whose read never returns at all. The exchange has nothing to stop, so it
        // neither waits on the stuck read nor replaces the reader parked in it: it hears
        // nothing, says so, and leaves the reader exactly as it found it.
        using var transport = new StallingReadMockTransport(readBlock: Timeout.InfiniteTimeSpan);
        using var device = new ConsumerRestartTestableDevice("Stuck Device", transport);

        device.Connect();
        var original = GetMessageConsumer(device);
        Assert.NotNull(original);
        Assert.True(transport.WaitForReadEntered(TimeSpan.FromSeconds(5)));

        var lines = await device.CallExecuteTextCommandAsync(() => { });
        Assert.Empty(lines);

        var after = GetMessageConsumer(device);
        Assert.Same(original, after);
        Assert.True(after!.IsRunning);

        transport.ReleaseReaders();
        device.Disconnect();
    }

    [Fact]
    public async Task ExecuteTextCommand_WhenReaderExitsPromptly_KeepsSameConsumer()
    {
        using var transport = new StallingReadMockTransport(readBlock: TimeSpan.Zero);
        using var device = new ConsumerRestartTestableDevice("Prompt Device", transport);

        device.Connect();
        var original = GetMessageConsumer(device);
        Assert.NotNull(original);

        await device.CallExecuteTextCommandAsync(() => { });

        var after = GetMessageConsumer(device);
        Assert.Same(original, after);
        Assert.True(after!.IsRunning);

        device.Disconnect();
    }

    [Fact]
    public async Task RawCapture_OnAStuckStream_LeavesTheReaderStoppedWithoutThrowing()
    {
        // The guard #383 is about, on the one path that still restarts the reader. No
        // replacement may be bound to the stuck stream — it would be a second concurrent reader
        // on it — and the refusal must not escape the capture's finally.
        using var transport = new StallingReadMockTransport(readBlock: Timeout.InfiniteTimeSpan);
        using var device = new ConsumerRestartTestableDevice("Stuck Device", transport);

        device.Connect();
        var original = GetMessageConsumer(device);
        Assert.True(transport.WaitForReadEntered(TimeSpan.FromSeconds(5)));

        await device.CallRawCaptureAsync((_, _) => Task.CompletedTask);

        var after = GetMessageConsumer(device);
        Assert.Same(original, after);
        Assert.False(after!.IsRunning);

        transport.ReleaseReaders();
        device.Disconnect();
    }

    [Fact]
    public async Task ExecuteTextCommand_AfterARawCaptureLeftTheReaderStuck_FailsSayingSo()
    {
        // The reader a raw capture could not restart is the reader every exchange now depends
        // on. Collecting nothing from it and reporting "the device did not answer" would blame
        // the device for a stuck host stream; the exchange asks the reader to start first, and
        // the refusal it gets back is the honest diagnosis.
        using var transport = new StallingReadMockTransport(readBlock: Timeout.InfiniteTimeSpan);
        using var device = new ConsumerRestartTestableDevice("Stuck Device", transport);

        device.Connect();
        Assert.True(transport.WaitForReadEntered(TimeSpan.FromSeconds(5)));
        await device.CallRawCaptureAsync((_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<ConsumerThreadNotExitedException>(
            () => device.CallExecuteTextCommandAsync(() => { }));

        transport.ReleaseReaders();
        device.Disconnect();
    }

    [Fact]
    public async Task ExecuteTextCommand_AfterTheStuckReadReturns_RevivesTheReader()
    {
        // Same stuck capture, but the stuck read returns before the next exchange. The exchange's
        // start then succeeds, and the device has its reader back without a reconnect.
        using var transport = new StallingReadMockTransport(readBlock: Timeout.InfiniteTimeSpan);
        using var device = new ConsumerRestartTestableDevice("Recovering Device", transport);

        device.Connect();
        var original = GetMessageConsumer(device);
        Assert.True(transport.WaitForReadEntered(TimeSpan.FromSeconds(5)));
        await device.CallRawCaptureAsync((_, _) => Task.CompletedTask);
        Assert.False(original!.IsRunning);

        transport.ReleaseReaders();
        await device.CallExecuteTextCommandAsync(() => { });

        var after = GetMessageConsumer(device);
        Assert.Same(original, after);
        Assert.True(after!.IsRunning);

        device.Disconnect();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RawCapture_DetachesTheInboundHandlerAndPutsItBackHoweverTheRestartGoes(bool stuckStream)
    {
        // Detached during the capture, so a reader that outlives its stop cannot hand the captured
        // bytes to the device as live frames; re-attached afterwards even when the restart is
        // refused, because the same subscription carries every later exchange's reply lines.
        using var transport = new StallingReadMockTransport(
            readBlock: stuckStream ? Timeout.InfiniteTimeSpan : TimeSpan.Zero);
        using var device = new ConsumerRestartTestableDevice("Capturing Device", transport);

        device.Connect();
        Assert.True(transport.WaitForReadEntered(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, InboundSubscriberCount(device));

        var duringCapture = -1;
        await device.CallRawCaptureAsync((_, _) =>
        {
            duringCapture = InboundSubscriberCount(device);
            return Task.CompletedTask;
        });

        Assert.Equal(0, duringCapture);
        Assert.Equal(1, InboundSubscriberCount(device));
        Assert.Equal(!stuckStream, GetMessageConsumer(device)!.IsRunning);

        transport.ReleaseReaders();
        device.Disconnect();
    }

    [Fact]
    public async Task RawCapture_OnAStuckStream_RaisesErrorOccurredForTheReader()
    {
        // Issue #975: the refusal used to reach a log line only, and a caller that gives Core no
        // logger never saw it while the device kept reporting Connected.
        using var transport = new StallingReadMockTransport(readBlock: Timeout.InfiniteTimeSpan);
        using var device = new ConsumerRestartTestableDevice("Stuck Device", transport);
        var errors = new ConcurrentQueue<DeviceErrorEventArgs>();
        device.ErrorOccurred += (_, e) => errors.Enqueue(e);

        device.Connect();
        Assert.True(transport.WaitForReadEntered(TimeSpan.FromSeconds(5)));
        await device.CallRawCaptureAsync((_, _) => Task.CompletedTask);

        var error = Assert.Single(errors);
        Assert.Equal(DeviceErrorSource.MessageConsumer, error.Source);
        var refusal = Assert.IsType<ConsumerThreadNotExitedException>(error.Error);
        Assert.IsType<ConsumerThreadNotExitedException>(refusal.InnerException);
        Assert.Contains("raw capture", refusal.Message);

        transport.ReleaseReaders();
        device.Disconnect();
    }

    [Fact]
    public async Task RawCapture_WhenTheReaderRestarts_RaisesNoError()
    {
        using var transport = new StallingReadMockTransport(readBlock: TimeSpan.Zero);
        using var device = new ConsumerRestartTestableDevice("Prompt Device", transport);
        var errors = 0;
        device.ErrorOccurred += (_, _) => Interlocked.Increment(ref errors);

        device.Connect();
        Assert.True(transport.WaitForReadEntered(TimeSpan.FromSeconds(5)));
        await device.CallRawCaptureAsync((_, _) => Task.CompletedTask);

        Assert.True(GetMessageConsumer(device)!.IsRunning);
        Assert.Equal(0, Volatile.Read(ref errors));

        device.Disconnect();
    }

    [Fact]
    public async Task StartStreaming_AfterARawCaptureLeftTheReaderStuck_ThrowsAndSendsNoStartCommand()
    {
        // Issue #975: a stream start is not a text exchange, so it used to send the start
        // command to a stopped reader and report a running session that delivered nothing.
        using var transport = new StallingReadMockTransport(readBlock: Timeout.InfiniteTimeSpan);
        using var device = new StreamingConsumerRestartTestableDevice("Stuck Device", transport);

        device.Connect();
        Assert.True(transport.WaitForReadEntered(TimeSpan.FromSeconds(5)));
        await device.CallRawCaptureAsync((_, _) => Task.CompletedTask);
        Assert.False(GetMessageConsumer(device)!.IsRunning);

        var refusal = Assert.Throws<ConsumerThreadNotExitedException>(() => device.StartStreaming());

        Assert.Contains(nameof(DaqifiStreamingDevice.StartStreaming), refusal.Message);
        Assert.False(device.IsStreaming);
        Assert.DoesNotContain(device.SentCommands, c => c.Contains("StartStreamData", StringComparison.OrdinalIgnoreCase));

        transport.ReleaseReaders();
        device.Disconnect();
    }

    [Fact]
    public async Task StartStreaming_AfterTheStuckReadReturns_RevivesTheReaderAndSendsTheStart()
    {
        using var transport = new StallingReadMockTransport(readBlock: Timeout.InfiniteTimeSpan);
        using var device = new StreamingConsumerRestartTestableDevice("Recovering Device", transport);

        device.Connect();
        var original = GetMessageConsumer(device);
        Assert.True(transport.WaitForReadEntered(TimeSpan.FromSeconds(5)));
        await device.CallRawCaptureAsync((_, _) => Task.CompletedTask);
        Assert.False(original!.IsRunning);

        transport.ReleaseReaders();
        device.StartStreaming();

        var after = GetMessageConsumer(device);
        Assert.Same(original, after);
        Assert.True(after!.IsRunning);
        Assert.True(device.IsStreaming);
        Assert.Contains(device.SentCommands, c => c.Contains("StartStreamData", StringComparison.OrdinalIgnoreCase));

        device.Disconnect();
    }

    [Fact]
    public async Task StartStreaming_DuringARawCaptureOnAnotherFlow_LeavesTheStoppedReaderAlone()
    {
        // The capture stopped the reader on purpose. Starting it now would put a reader on the
        // stream that the capture reads, so the start's command is parked and the reader waits
        // for the capture's own restart.
        using var transport = new StallingReadMockTransport(readBlock: TimeSpan.Zero);
        using var device = new StreamingConsumerRestartTestableDevice("Capturing Device", transport);

        device.Connect();
        Assert.True(transport.WaitForReadEntered(TimeSpan.FromSeconds(5)));

        using var inCapture = new ManualResetEventSlim(false);
        using var endCapture = new ManualResetEventSlim(false);
        var capture = Task.Run(() => device.CallRawCaptureAsync((_, _) =>
        {
            inCapture.Set();
            endCapture.Wait(TimeSpan.FromSeconds(10));
            return Task.CompletedTask;
        }));

        Assert.True(inCapture.Wait(TimeSpan.FromSeconds(10)));
        device.StartStreaming();
        var runningDuringCapture = GetMessageConsumer(device)!.IsRunning;

        endCapture.Set();
        await capture;

        Assert.False(runningDuringCapture);
        Assert.True(GetMessageConsumer(device)!.IsRunning);
        Assert.True(device.IsStreaming);
        Assert.True(
            SpinWait.SpinUntil(() => transport.Written.Contains("StartStreamData", StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(5)),
            "The parked start must reach the wire once the capture restarts the reader.");

        device.Disconnect();
    }

    public enum HeldBack
    {
        TypedStop,
        RawStop,
        StopAndStart,
    }

    [Theory]
    [InlineData(HeldBack.TypedStop)]
    [InlineData(HeldBack.RawStop)]
    [InlineData(HeldBack.StopAndStart)]
    public async Task AStopParkedBehindAnOperationThatStarts_GoesOutAheadOfThatStart(HeldBack heldBack)
    {
        // Replayed after the operation's start, it would stop the stream that start began, with
        // the session still recorded as running.
        var (written, isStreaming) = await ParkBehindAnOperationAsync(
            device => device.StartStreaming(),
            device =>
            {
                if (heldBack == HeldBack.RawStop)
                {
                    device.Send(ScpiMessageProducer.StopStreaming);
                }
                else
                {
                    device.StopStreaming();
                }

                if (heldBack == HeldBack.StopAndStart)
                {
                    device.StartStreaming();
                    device.StopStreaming();
                }
            },
            device => device.StartStreaming());

        Assert.True(isStreaming);
        Assert.Equal(["Start", "Stop", "Start"], written);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AStartParkedBehindAnOperationThatStops_IsNotSent(bool raw)
    {
        // Replayed after the operation's stop, it would leave the device streaming with no session
        // recorded.
        var (written, isStreaming) = await ParkBehindAnOperationAsync(
            _ => { },
            device =>
            {
                if (raw)
                {
                    device.Send(ScpiMessageProducer.StartStreaming(100));
                }
                else
                {
                    device.StartStreaming();
                }
            },
            device => device.Send(ScpiMessageProducer.StopStreaming));

        Assert.False(isStreaming);
        Assert.Equal(["Stop"], written);
    }

    [Fact]
    public async Task AStopParkedBehindAnOperationThatStopsToo_IsOutWhenThatReturns()
    {
        // Nothing is recorded as running, but the device is, and the operation has asked for a stop.
        var (written, isStreaming) = await ParkBehindAnOperationAsync(
            device => device.StartStreaming(),
            device => device.StopStreaming(),
            device =>
            {
                device.StopStreaming();
                device.Send(new ScpiMessage(InOperation));
            });

        Assert.False(isStreaming);
        Assert.Equal(["Start", "Stop", InOperation], written);
    }

    [Fact]
    public async Task AStartParkedBehindAnOperationThatStartsToo_IsOutWhenThatReturns()
    {
        // A stream is recorded, but it waits for the operation, which has asked for one.
        var (written, isStreaming) = await ParkBehindAnOperationAsync(
            _ => { },
            device => device.StartStreaming(),
            device =>
            {
                device.StartStreaming();
                device.Send(new ScpiMessage(InOperation));
            });

        Assert.True(isStreaming);
        Assert.Equal(["Start", InOperation], written);
    }

    [Fact]
    public async Task AStartEvictedFromTheBacklog_LeavesNoSession_SoARetryStartsTheDevice()
    {
        var (written, isStreaming) = await ParkBehindAnOperationAsync(
            _ => { },
            device =>
            {
                device.BacklogCap = 2;
                device.StartStreaming();
                SendFillers(device, 2);
            },
            _ => { },
            device =>
            {
                Assert.False(device.IsStreaming);
                device.StartStreaming();
            });

        Assert.True(isStreaming);
        Assert.Equal(["Start"], written);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AStopEvictedFromTheBacklog_LeavesTheSession_SoARetryStopsTheDevice(bool raw)
    {
        var (written, isStreaming) = await ParkBehindAnOperationAsync(
            device => device.StartStreaming(),
            device =>
            {
                device.BacklogCap = 2;
                if (raw)
                {
                    device.Send(ScpiMessageProducer.StopStreaming);
                }
                else
                {
                    device.StopStreaming();
                }

                SendFillers(device, 2);
            },
            _ => { },
            device =>
            {
                Assert.True(device.IsStreaming);
                device.StopStreaming();
            });

        Assert.False(isStreaming);
        Assert.Equal(["Start", "Stop"], written);
    }

    [Fact]
    public async Task ARateChangeEvictedFromTheBacklog_LeavesTheRateTheStreamRunsAt()
    {
        await ParkBehindAnOperationAsync(
            device =>
            {
                device.SampleRateHz = 100;
                device.StartStreaming();
            },
            device =>
            {
                device.BacklogCap = 2;
                device.Send(ScpiMessageProducer.StartStreaming(200));
                SendFillers(device, 2);
            },
            _ => { },
            device =>
            {
                Assert.True(device.IsStreaming);
                Assert.Equal(100, device.SampleRateHz);
            });
    }

    [Fact]
    public async Task ARateChangeHeldBack_IsReportedOnceItIsOut()
    {
        await ParkBehindAnOperationAsync(
            device =>
            {
                device.SampleRateHz = 100;
                device.StartStreaming();
            },
            device =>
            {
                device.Send(ScpiMessageProducer.StartStreaming(200));
                Assert.Equal(100, device.SampleRateHz);
            },
            _ => { },
            device => Assert.Equal(200, device.SampleRateHz));
    }

    [Fact]
    public async Task ARateChosenWhileARateChangeIsHeldBack_IsKept()
    {
        await ParkBehindAnOperationAsync(
            device => device.StartStreaming(),
            device =>
            {
                device.Send(ScpiMessageProducer.StartStreaming(200));
                device.SampleRateHz = 300;
            },
            _ => { },
            device => Assert.Equal(300, device.SampleRateHz));
    }

    [Fact]
    public async Task AStartEvictedFromTheBacklog_DoesNotLowerTheRate()
    {
        await ParkBehindAnOperationAsync(
            device => device.SampleRateHz = 1000,
            device =>
            {
                device.BacklogCap = 2;
                device.UpdateMetadata(metadata => metadata with
                {
                    Capabilities = metadata.Capabilities with { MaximumSampleRateHz = 500 }
                });
                device.StartStreaming();
                SendFillers(device, 2);
            },
            _ => { },
            device => Assert.Equal(1000, device.SampleRateHz));
    }

    [Fact]
    public async Task AStopAndAStartEvictedFromTheBacklog_LeaveTheDecoderOfTheStreamThatIsRunning()
    {
        // Reset under the running stream, it would anchor that stream's clock again half-way.
        await ParkBehindAnOperationAsync(
            device => device.StartStreaming(),
            device =>
            {
                for (var i = 0; i < 3; i++)
                {
                    device.DeliverFrame();
                }

                device.BacklogCap = 2;
                device.StopStreaming();
                device.StartStreaming();
                Assert.Equal(3, AdmittedFrames(device));
                SendFillers(device, 2);
            },
            _ => { },
            device => Assert.Equal(3, AdmittedFrames(device)));
    }

    private static long AdmittedFrames(DaqifiDevice device) =>
        ((StreamFrameDecoder)typeof(DaqifiStreamingDevice)
            .GetField("_frameDecoder", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(device)!).AdmittedStreamFrameCount;

    [Fact]
    public async Task AStopAndAStartEvictedFromTheBacklog_LeaveTheStreamThatIsRunning()
    {
        var (written, isStreaming) = await ParkBehindAnOperationAsync(
            device => device.StartStreaming(),
            device =>
            {
                device.BacklogCap = 2;
                device.StopStreaming();
                device.StartStreaming();
                SendFillers(device, 2);
            },
            _ => { });

        Assert.True(isStreaming);
        Assert.Equal(["Start"], written);
    }

    [Fact]
    public async Task AStopEvictedFromTheBacklogAheadOfAStart_StillGoesOutAheadOfIt()
    {
        // The start is for a device that has stopped: what it streams after the stop is not what
        // it streamed before, and it may have been an SD log.
        var (written, isStreaming) = await ParkBehindAnOperationAsync(
            device => device.StartStreaming(),
            device =>
            {
                device.BacklogCap = 2;
                device.StopStreaming();
                device.StartStreaming();
                SendFillers(device, 1);
            },
            _ => { });

        Assert.True(isStreaming);
        Assert.Equal(["Start", "Stop", "Start"], written);
    }

    [Fact]
    public async Task AStartEvictedFromTheBacklogByAStop_LeavesNoSession()
    {
        var (_, isStreaming) = await ParkBehindAnOperationAsync(
            _ => { },
            device =>
            {
                device.BacklogCap = 1;
                device.Send(ScpiMessageProducer.StartStreaming(100));
                device.Send(ScpiMessageProducer.StopStreaming);
            },
            _ => { });

        Assert.False(isStreaming);
    }

    [Fact]
    public async Task StartStreamingAsync_EvictedFromTheBacklog_SaysSo()
    {
        using var transport = new StallingReadMockTransport(readBlock: TimeSpan.Zero);
        using var device = new StreamingConsumerRestartTestableDevice("Parking Device", transport) { BacklogCap = 1 };
        device.Connect();

        var inOperation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var endOperation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = Task.Run(() => device.RunExclusiveAsync(async _ =>
        {
            inOperation.SetResult();
            await endOperation.Task;
        }));
        await inOperation.Task;

        var start = device.StartStreamingAsync();
        SendFillers(device, 1);
        var failure = await Assert.ThrowsAsync<DeviceCommandFailedException>(() => start.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains("backlog", failure.Message, StringComparison.Ordinal);
        Assert.False(device.IsStreaming);
        endOperation.SetResult();
        await operation;
    }

    private static void SendFillers(DaqifiDevice device, int count)
    {
        for (var i = 0; i < count; i++)
        {
            device.Send(new ScpiMessage("FILLER"));
        }
    }

    private const string InOperation = "IN-OPERATION";

    [Fact]
    public async Task CommandsParkedBehindAnOperationThatSendsNone_GoOutInOrder()
    {
        var (written, isStreaming) = await ParkBehindAnOperationAsync(
            device => device.StartStreaming(),
            device =>
            {
                device.StopStreaming();
                device.StartStreaming();
                device.Send(ScpiMessageProducer.StopStreaming);
                device.Send(ScpiMessageProducer.StartStreaming(200));
            },
            _ => { });

        Assert.True(isStreaming);
        Assert.Equal(["Start", "Stop", "Start", "Stop", "Start"], written);
    }

    [Fact]
    public async Task StartStreamingAsync_Parked_IsNotConfirmedByFramesOfTheStreamStillRunning()
    {
        // The stop ahead of it is parked too, so the old stream keeps delivering meanwhile. Nothing
        // counts before the start has gone out.
        using var transport = new StallingReadMockTransport(readBlock: TimeSpan.Zero);
        using var device = new StreamingConsumerRestartTestableDevice("Parking Device", transport);
        var clock = new FakeTimeProvider();
        device.SetTimeProviderForTesting(clock);
        device.Connect();
        device.SampleRateHz = 1;
        device.StartStreaming();

        var inOperation = new TaskCompletionSource();
        var endOperation = new TaskCompletionSource();
        var operation = Task.Run(() => device.RunExclusiveAsync(async _ =>
        {
            inOperation.SetResult();
            await endOperation.Task;
        }));
        await inOperation.Task;

        device.StopStreaming();
        var start = device.StartStreamingAsync();
        Assert.True(SpinWait.SpinUntil(() => device.IsStreaming, TimeSpan.FromSeconds(5)));
        await DeliverFramesAsync(10);
        Assert.False(start.IsCompleted);

        // It follows the stop straight out, so the old stream's last frames are still to come.
        endOperation.SetResult();
        await operation;
        await DeliverFramesAsync(5);
        Assert.False(start.IsCompleted);

        for (var i = 0; i < 50 && !start.IsCompleted; i++)
        {
            await DeliverFramesAsync(1);
        }

        await start.WaitAsync(TimeSpan.FromSeconds(5));

        // One frame every 10 ms of the device's clock.
        async Task DeliverFramesAsync(int count)
        {
            for (var i = 0; i < count; i++)
            {
                device.DeliverFrame();
                clock.Advance(TimeSpan.FromMilliseconds(10));
                await Task.Delay(5);
            }
        }
    }

    /// <summary>
    /// Has an operation take the device and <paramref name="operation"/> run in it once
    /// <paramref name="parked"/> has been called on another flow, and <paramref name="afterwards"/>
    /// once the operation has ended.
    /// </summary>
    /// <returns>
    /// The stream commands on the wire, and <see cref="InOperation"/>, in order; and whether a
    /// session is recorded.
    /// </returns>
    private static async Task<(string[] Written, bool IsStreaming)> ParkBehindAnOperationAsync(
        Action<StreamingConsumerRestartTestableDevice> first,
        Action<StreamingConsumerRestartTestableDevice> parked,
        Action<DaqifiStreamingDevice> operation,
        Action<DaqifiStreamingDevice>? afterwards = null)
    {
        using var transport = new StallingReadMockTransport(readBlock: TimeSpan.Zero);
        using var device = new StreamingConsumerRestartTestableDevice("Parking Device", transport);
        device.Connect();
        first(device);

        var inOperation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var endOperation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exclusive = Task.Run(() => device.RunExclusiveAsync(async _ =>
        {
            inOperation.SetResult();
            await endOperation.Task;
            operation(device);
        }));

        await inOperation.Task;
        parked(device);
        endOperation.SetResult();
        await exclusive;
        afterwards?.Invoke(device);

        var isStreaming = device.IsStreaming;
        device.Send(new ScpiMessage("MARKER"));
        Assert.True(
            SpinWait.SpinUntil(() => transport.Written.Contains("MARKER", StringComparison.Ordinal), TimeSpan.FromSeconds(5)),
            "The marker must reach the wire.");
        var written = transport.Written
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains("StreamData", StringComparison.Ordinal) || line == InOperation)
            .Select(line => line == InOperation ? line
                : line.Contains("StartStreamData", StringComparison.Ordinal) ? "Start" : "Stop")
            .ToArray();
        device.Disconnect();

        return (written, isStreaming);
    }

    [Fact]
    public async Task StartStreaming_StoppedByTheOperationItIsParkedBehind_IsNotSent()
    {
        // The operation's own stop goes straight out, so replaying the parked start behind it would
        // leave the device streaming with no session recorded.
        using var transport = new StallingReadMockTransport(readBlock: TimeSpan.Zero);
        using var device = new StreamingConsumerRestartTestableDevice("Capturing Device", transport);

        device.Connect();
        Assert.True(transport.WaitForReadEntered(TimeSpan.FromSeconds(5)));

        using var inCapture = new ManualResetEventSlim(false);
        using var endCapture = new ManualResetEventSlim(false);
        var capture = Task.Run(() => device.CallRawCaptureAsync((_, _) =>
        {
            inCapture.Set();
            endCapture.Wait(TimeSpan.FromSeconds(10));
            device.StopStreaming();
            return Task.CompletedTask;
        }));

        Assert.True(inCapture.Wait(TimeSpan.FromSeconds(10)));
        device.StartStreaming();
        endCapture.Set();
        await capture;

        Assert.False(device.IsStreaming);
        device.Send(new ScpiMessage("MARKER"));
        Assert.True(
            SpinWait.SpinUntil(() => transport.Written.Contains("MARKER", StringComparison.Ordinal), TimeSpan.FromSeconds(5)),
            "The marker must reach the wire.");
        Assert.Contains("StopStreamData", transport.Written, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StartStreamData", transport.Written, StringComparison.OrdinalIgnoreCase);

        device.Disconnect();
    }

    [Fact]
    public async Task StartStreaming_DuringARawCaptureWhoseRestartFails_SendsNoStartAndEndsTheSession()
    {
        // Issue #975, the concurrent half. A start on another flow during the capture cannot check
        // the reader, because the capture stopped it on purpose, so the start is parked. When the
        // capture then cannot restart the reader, the parked start must not go out: the session
        // would report running and deliver nothing, and a later start would return early on it.
        using var transport = new StallingReadMockTransport(readBlock: Timeout.InfiniteTimeSpan);
        using var device = new StreamingConsumerRestartTestableDevice("Stuck Device", transport);
        var errors = new ConcurrentQueue<DeviceErrorEventArgs>();
        device.ErrorOccurred += (_, e) => errors.Enqueue(e);

        device.Connect();
        Assert.True(transport.WaitForReadEntered(TimeSpan.FromSeconds(5)));

        using var inCapture = new ManualResetEventSlim(false);
        using var endCapture = new ManualResetEventSlim(false);
        var capture = Task.Run(() => device.CallRawCaptureAsync((_, _) =>
        {
            inCapture.Set();
            endCapture.Wait(TimeSpan.FromSeconds(10));
            return Task.CompletedTask;
        }));

        Assert.True(inCapture.Wait(TimeSpan.FromSeconds(10)));
        device.StartStreaming();
        Assert.True(device.IsStreaming);

        endCapture.Set();
        await capture;

        Assert.False(GetMessageConsumer(device)!.IsRunning);
        Assert.False(device.IsStreaming);

        // Writes still work on a stuck reader. A marker sent now is written after anything that
        // was parked, so once it is on the wire, a start that went out would be there too.
        device.Send(new ScpiMessage("MARKER"));
        Assert.True(
            SpinWait.SpinUntil(() => transport.Written.Contains("MARKER", StringComparison.Ordinal), TimeSpan.FromSeconds(5)),
            "The marker must reach the wire.");
        Assert.DoesNotContain("StartStreamData", transport.Written, StringComparison.OrdinalIgnoreCase);

        // The caller hears that the reader is stuck. The dropped start's refusal has the same
        // source and type a moment later, so ErrorOccurred's throttle folds it into this report.
        var error = Assert.Single(errors);
        Assert.Equal(DeviceErrorSource.MessageConsumer, error.Source);
        Assert.IsType<ConsumerThreadNotExitedException>(error.Error);

        // Not an early return on a session that never ran: the next start checks the reader and
        // says why it cannot.
        Assert.Throws<ConsumerThreadNotExitedException>(() => device.StartStreaming());
        Assert.False(device.IsStreaming);

        transport.ReleaseReaders();
        device.Disconnect();
    }

    [Fact]
    public async Task StartStreamingAsync_DuringARawCaptureWhoseRestartFails_ThrowsTheReaderRefusal()
    {
        // Issue #975, review of #976: the async start awaits its confirmation while the start is
        // parked. When the capture then cannot restart the reader, the parked start is dropped and
        // the session ends; the awaiting call must say so rather than complete as a success.
        using var transport = new StallingReadMockTransport(readBlock: Timeout.InfiniteTimeSpan);
        using var device = new StreamingConsumerRestartTestableDevice("Stuck Device", transport);

        device.Connect();
        Assert.True(transport.WaitForReadEntered(TimeSpan.FromSeconds(5)));

        using var inCapture = new ManualResetEventSlim(false);
        using var endCapture = new ManualResetEventSlim(false);
        var capture = Task.Run(() => device.CallRawCaptureAsync((_, _) =>
        {
            inCapture.Set();
            endCapture.Wait(TimeSpan.FromSeconds(10));
            return Task.CompletedTask;
        }));

        Assert.True(inCapture.Wait(TimeSpan.FromSeconds(10)));
        var start = Task.Run(() => device.StartStreamingAsync());
        Assert.True(
            SpinWait.SpinUntil(() => device.IsStreaming, TimeSpan.FromSeconds(5)),
            "The start must be parked behind the capture, with the session recorded as live.");

        endCapture.Set();
        await capture;

        var refusal = await Assert.ThrowsAsync<ConsumerThreadNotExitedException>(() => start);
        Assert.Contains(nameof(DaqifiStreamingDevice.StartStreaming), refusal.Message);
        Assert.False(device.IsStreaming);
        Assert.DoesNotContain("StartStreamData", transport.Written, StringComparison.OrdinalIgnoreCase);

        transport.ReleaseReaders();
        device.Disconnect();
    }

    private static int InboundSubscriberCount(DaqifiDevice device)
    {
        var consumer = GetMessageConsumer(device)!;
        var handler = (Delegate?)consumer.GetType()
            .GetField("MessageParsed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(consumer);
        return handler?.GetInvocationList().Length ?? 0;
    }

    private static IMessageConsumer<WireMessage>? GetMessageConsumer(DaqifiDevice device)
    {
        return (IMessageConsumer<WireMessage>?)typeof(DaqifiDevice)
            .GetField("_messageConsumer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(device);
    }

    /// <summary>
    /// Exposes the protected text-exchange and raw-capture entry points so they can be driven
    /// directly, without needing a device that answers SCPI.
    /// </summary>
    private class ConsumerRestartTestableDevice : DaqifiDevice
    {
        public ConsumerRestartTestableDevice(string name, IStreamTransport transport)
            : base(name, transport)
        {
        }

        public Task<IReadOnlyList<string>> CallExecuteTextCommandAsync(Action setupAction)
        {
            return ExecuteTextCommandAsync(setupAction, responseTimeout: TimeSpan.FromMilliseconds(100), completionTimeout: TimeSpan.FromMilliseconds(50));
        }

        public Task CallRawCaptureAsync(Func<Stream, CancellationToken, Task> rawAction)
        {
            return ExecuteRawCaptureAsync(rawAction);
        }
    }

    /// <summary>
    /// The streaming counterpart of <see cref="ConsumerRestartTestableDevice"/>: exposes the raw
    /// capture and records every text command the device sends.
    /// </summary>
    private sealed class StreamingConsumerRestartTestableDevice : DaqifiStreamingDevice
    {
        public StreamingConsumerRestartTestableDevice(string name, IStreamTransport transport)
            : base(name, transport)
        {
        }

        public ConcurrentQueue<string> SentCommands { get; } = new();

        public int BacklogCap { get; set; } = DefaultMaximumDeferredSends;

        internal override int MaxDeferredSends => BacklogCap;

        private uint _deviceTicks;

        public void DeliverFrame() =>
            OnStreamMessageReceived(new DaqifiOutMessage { MsgTimeStamp = _deviceTicks += 50_000_000 });

        public Task CallRawCaptureAsync(Func<Stream, CancellationToken, Task> rawAction)
        {
            return ExecuteRawCaptureAsync(rawAction);
        }

        public override void Send<T>(IOutboundMessage<T> message)
        {
            if (message is IOutboundMessage<string> text)
            {
                SentCommands.Enqueue(text.Data);
            }

            base.Send(message);
        }
    }

    /// <summary>
    /// Transport whose stream models a reader parked in a blocking socket read, mirroring a WiFi
    /// connection whose receive timeout outlasts the reader's stop budget. Writes are accepted
    /// and discarded.
    /// </summary>
    /// <remarks>
    /// <paramref name="readBlock"/> selects the scenario: <see cref="TimeSpan.Zero"/> for a prompt
    /// reader, a finite span for one that outlives the stop budget but does return, and
    /// <see cref="Timeout.InfiniteTimeSpan"/> for a stream that is stuck outright.
    /// </remarks>
    private sealed class StallingReadMockTransport : IStreamTransport
    {
        public bool IsUsbConnection => false;

        private readonly StallingStream _stream;
        private bool _isConnected;
        private bool _disposed;

        public StallingReadMockTransport(TimeSpan readBlock)
        {
            _stream = new StallingStream(readBlock);
        }

        public Stream Stream => _disposed
            ? throw new ObjectDisposedException(nameof(StallingReadMockTransport))
            : _stream;

        public bool IsConnected => _isConnected && !_disposed;

        public string ConnectionInfo => _isConnected ? "Stalling: Connected" : "Stalling: Disconnected";

        public event EventHandler<TransportStatusEventArgs>? StatusChanged;

        public bool WaitForReadEntered(TimeSpan timeout) => _stream.WaitForReadEntered(timeout);

        public void ReleaseReaders() => _stream.Release();

        /// <summary>Everything written to the stream so far, as ASCII.</summary>
        public string Written => _stream.Written;

        public Task ConnectAsync(ConnectionRetryOptions? retryOptions = null, CancellationToken cancellationToken = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(StallingReadMockTransport));
            _isConnected = true;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(true, ConnectionInfo));
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            _isConnected = false;
            _stream.Release();
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(false, ConnectionInfo));
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _isConnected = false;
            _stream.Release();
            _disposed = true;
        }

        private sealed class StallingStream : Stream
        {
            private readonly TimeSpan _readBlock;
            private readonly ManualResetEventSlim _release = new(false);
            private readonly ManualResetEventSlim _readEntered = new(false);

            public StallingStream(TimeSpan readBlock) => _readBlock = readBlock;

            public bool WaitForReadEntered(TimeSpan timeout) => _readEntered.Wait(timeout);

            public void Release() => _release.Set();

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count)
            {
                _readEntered.Set();
                if (_readBlock == TimeSpan.Zero)
                {
                    Thread.Sleep(10);
                }
                else
                {
                    // Infinite means "stuck stream": only an explicit Release() ever frees it.
                    _release.Wait(_readBlock);
                }

                return 0;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            private readonly System.Text.StringBuilder _written = new();

            public string Written
            {
                get
                {
                    lock (_written)
                    {
                        return _written.ToString();
                    }
                }
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                lock (_written)
                {
                    _written.Append(System.Text.Encoding.ASCII.GetString(buffer, offset, count));
                }
            }
        }
    }
}
