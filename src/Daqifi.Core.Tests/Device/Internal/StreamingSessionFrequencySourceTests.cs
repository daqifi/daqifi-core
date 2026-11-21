using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device.Internal;

namespace Daqifi.Core.Tests.Device.Internal;

/// <summary>
/// A caller's rate choice and a device-reported stored rate racing each other: the choice wins.
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

        public int MaximumStreamingFrequencyHz => 1000;

        public bool PreserveActiveStream => false;

        public void EnsureConnected()
        {
        }

        public void EnsureMessageReaderRunning()
        {
        }

        public void Send<T>(IOutboundMessage<T> message)
        {
        }

        public void SendStreamStart<T>(IOutboundMessage<T> message, Action<Exception> onNotSent)
        {
        }

        public void BeginDecoderSession(int streamingFrequencyHz)
        {
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
