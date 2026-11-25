using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device.Internal;

namespace Daqifi.Core.Tests.Device.Internal;

/// <summary>
/// A caller's rate choice and a device-reported stored rate racing each other: the choice wins.
/// And a raw start and a raw stop racing each other: what is recorded follows what went out.
/// </summary>
public class StreamingSessionFrequencySourceTests
{
    [Fact]
    public void AStoredRateRead_ThatACallersChoiceInterrupts_LeavesTheChoiceInForce()
    {
        // The caller chooses 250 Hz while the stored-rate read is in flight. Adopting the stored
        // 500 Hz afterwards would both lose the choice and relabel the rate as inherited, so a
        // later capability read could replace it again.
        var host = new FakeHost();
        var session = new StreamingSessionController(host);
        session.SetDefaultFrequency(StreamingSessionController.CoreDefaultFrequencyHz);
        host.OnMaxSamplingRateRead = () => session.StreamingFrequency = 250;

        var adopted = session.TryAdoptStoredFrequency(500, rateConfigured: true);

        Assert.False(adopted);
        Assert.Equal(250, session.StreamingFrequency);
        Assert.False(session.IsFrequencyInherited);
    }

    [Fact]
    public void AStartsRateCap_ThatACallersLowerChoiceInterrupts_LeavesTheLowerChoice()
    {
        // The caller lowers the rate to 100 Hz while the start reads the 500 Hz ceiling. Clamping
        // the rate as it stood before the read (1000 Hz) would overwrite the choice with 500 Hz.
        var host = new FakeHost { MaximumStreamingFrequencyHz = 500 };
        var session = new StreamingSessionController(host);
        session.SetDefaultFrequency(StreamingSessionController.CoreDefaultFrequencyHz);
        session.StreamingFrequency = 1000;
        host.OnMaximumStreamingFrequencyRead = () => session.StreamingFrequency = 100;

        session.StartStreaming();

        Assert.Equal(100, session.StreamingFrequency);
    }

    [Fact]
    public void AStart_UsesOneRateForTheDecoderAndTheWire()
    {
        // A caller choosing a rate while the start is configuring the decoder must not leave the
        // decoder at one rate and the device started at another (or at a rate the cap never saw).
        var host = new FakeHost { MaximumStreamingFrequencyHz = 500 };
        var session = new StreamingSessionController(host);
        session.SetDefaultFrequency(StreamingSessionController.CoreDefaultFrequencyHz);
        session.StreamingFrequency = 200;
        host.OnBeginDecoderSession = () => session.StreamingFrequency = 900;

        session.StartStreaming();

        Assert.Equal(200, host.DecoderRateHz);
        Assert.Equal("SYSTem:StartStreamData 200", host.StartCommand);
    }

    [Fact]
    public async Task ARawStop_ThatFollowsARawStartOut_IsRecordedAfterIt()
    {
        // The start is recorded after it went out. A stop that got in between would be recorded
        // first, leaving a session recorded on a device that has stopped.
        var session = new StreamingSessionController(new FakeHost());
        Task? stop = null;

        session.SendTracked(
            "SYSTem:StartStreamData 100",
            _ =>
            {
                stop = Task.Factory.StartNew(
                    () => session.SendTracked("SYSTem:StopStreamData", _ => false),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
                stop.Wait(TimeSpan.FromMilliseconds(200));
                return false;
            });
        await stop!;

        Assert.False(session.IsStreaming);
    }

    [Fact]
    public void ARawStart_WhoseConnectionEndsAsItGoesOut_BeginsNoSession()
    {
        var session = new StreamingSessionController(new FakeHost());

        session.SendTracked(
            "SYSTem:StartStreamData 100",
            _ =>
            {
                session.EndSessionOnConnectionEnd();
                return false;
            });

        Assert.False(session.IsStreaming);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AHeldBackCommandThatWillNotBeSent_TakesTheRecordBack(bool isStart)
    {
        var session = new StreamingSessionController(new FakeHost());
        if (!isStart)
        {
            session.StartStreaming();
        }

        IHeldBackSend? heldBack = null;
        session.SendTracked(
            isStart ? "SYSTem:StartStreamData 100" : "SYSTem:StopStreamData",
            command =>
            {
                heldBack = command;
                return true;
            });
        Assert.Equal(isStart, session.IsStreaming);

        heldBack!.NotSent(new InvalidOperationException("not sent"));

        Assert.Equal(!isStart, session.IsStreaming);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AHeldBackCommandThatWillNotBeSent_LeavesWhatWasRecordedSinceAlone(bool connectionEnded)
    {
        var session = new StreamingSessionController(new FakeHost());
        session.StartStreaming();
        IHeldBackSend? stop = null;
        session.SendTracked(
            "SYSTem:StopStreamData",
            command =>
            {
                stop = command;
                return true;
            });
        if (connectionEnded)
        {
            session.EndSessionOnConnectionEnd();
        }

        session.StartStreaming();
        var current = session.Current;
        stop!.NotSent(null);

        Assert.Same(current, session.Current);
    }

    private sealed class FakeHost : IStreamingSessionHost
    {
        private Action? _onMaxSamplingRateRead;

        /// <summary>Runs once, on the next read of <see cref="MaxSamplingRate"/>.</summary>
        public Action? OnMaxSamplingRateRead
        {
            set => _onMaxSamplingRateRead = value;
        }

        public int MaxSamplingRate
        {
            get
            {
                var action = _onMaxSamplingRateRead;
                _onMaxSamplingRateRead = null;
                action?.Invoke();
                return 1000;
            }
        }

        private Action? _onMaximumStreamingFrequencyRead;
        private int _maximumStreamingFrequencyHz = 1000;

        /// <summary>Runs once, on the next read of <see cref="MaximumStreamingFrequencyHz"/>.</summary>
        public Action? OnMaximumStreamingFrequencyRead
        {
            set => _onMaximumStreamingFrequencyRead = value;
        }

        public int MaximumStreamingFrequencyHz
        {
            get
            {
                var action = _onMaximumStreamingFrequencyRead;
                _onMaximumStreamingFrequencyRead = null;
                action?.Invoke();
                return _maximumStreamingFrequencyHz;
            }
            init => _maximumStreamingFrequencyHz = value;
        }

        public bool SendsAreNeverHeldBack => false;

        public bool PreserveActiveStream => false;

        public TimeProvider Clock => TimeProvider.System;

        public void EnsureConnected()
        {
        }

        public void EnsureMessageReaderRunning()
        {
        }

        public void Send<T>(IOutboundMessage<T> message) => StartCommand = message.Data as string;

        public void SendNow<T>(IOutboundMessage<T> message) => throw new NotSupportedException();

        public string? StartCommand { get; private set; }

        public int? DecoderRateHz { get; private set; }

        public Action? OnBeginDecoderSession { get; set; }

        public bool ReaderRunsForHeldBackStart(Action<Exception> onNotSent) => true;

        public void BeginDecoderSession(int streamingFrequencyHz)
        {
            DecoderRateHz = streamingFrequencyHz;
            OnBeginDecoderSession?.Invoke();
        }

        public void EndSdCardLogSession()
        {
        }

        public void WithChannelsLock(Action action) => action();

        public IReadOnlyList<IChannel> SnapshotChannels() => [];

        public void DisableAllChannels()
        {
        }

        public void EnableChannels(IEnumerable<IChannel> channels)
        {
        }
    }
}
