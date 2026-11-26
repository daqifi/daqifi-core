using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device;

namespace Daqifi.Core.Tests.Device.SdCard;

/// <summary>
/// The step an SD download takes before anything else: turning the device's echo off, so the
/// commands it sends are not repeated into the raw capture it reads the file from (#1083).
/// </summary>
public class SdCardDownloadEchoTests
{
    [Fact]
    public async Task TurnDeviceEchoOffAsync_SendsEchoOffThenTheEchoQuery_InOneExchange()
    {
        // The query's answer ends the exchange promptly, and an echo of the first command, when
        // echo was on, arrives inside the same exchange and is dropped with it.
        var device = new ExchangeRecordingDevice();

        await device.TurnDeviceEchoOffAsync(CancellationToken.None);

        Assert.Equal(1, device.Exchanges);
        Assert.Equal(
            [ScpiMessageProducer.DisableDeviceEcho.Data, "SYSTem:ECHO?"],
            device.SentInExchange);
    }

    [Fact]
    public async Task TurnDeviceEchoOffAsync_ReturnsTheAnswer_ForTheDownloadToJudge()
    {
        var device = new ExchangeRecordingDevice();

        var answer = await device.TurnDeviceEchoOffAsync(CancellationToken.None);

        Assert.Equal(["-1"], answer);
    }

    [Fact]
    public async Task TurnDeviceEchoOffAsync_WhenTheExchangeFails_Throws()
    {
        // The download asks again and, failing that, refuses to start the capture.
        var device = new ExchangeRecordingDevice { Failure = new TimeoutException("no answer") };

        await Assert.ThrowsAsync<TimeoutException>(() => device.TurnDeviceEchoOffAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TurnDeviceEchoOffAsync_WhenCancelled_Throws()
    {
        var device = new ExchangeRecordingDevice { Failure = new OperationCanceledException() };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => device.TurnDeviceEchoOffAsync(CancellationToken.None));
    }

    private sealed class ExchangeRecordingDevice() : DaqifiStreamingDevice("EchoTest")
    {
        private bool _inExchange;

        public int Exchanges { get; private set; }

        public List<string> SentInExchange { get; } = new();

        public Exception? Failure { get; init; }

        public override void Send<T>(IOutboundMessage<T> message)
        {
            if (_inExchange && message is IOutboundMessage<string> text)
            {
                SentInExchange.Add(text.Data);
            }
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
            if (Failure is { } failure)
            {
                throw failure;
            }

            _inExchange = true;
            try
            {
                setupAction();
            }
            finally
            {
                _inExchange = false;
            }

            return Task.FromResult<IReadOnlyList<string>>(["-1"]);
        }
    }
}
