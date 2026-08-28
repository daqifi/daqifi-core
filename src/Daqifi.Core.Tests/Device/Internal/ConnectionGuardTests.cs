using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Internal;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Daqifi.Core.Tests.Device.Internal;

/// <summary>
/// Unit tests for <see cref="ConnectionGuard"/>, the single up-front connectivity guard that
/// replaced fifty hand-rolled <c>if (!IsConnected) { throw ... }</c> blocks (#482).
/// </summary>
/// <remarks>
/// <para>
/// That each individual operation still refuses a disconnected device is already pinned across the
/// suite — <c>DeviceNotConnectedExceptionTests</c>, <c>DaqifiStreamingDeviceTests</c>,
/// <c>SdCardOperationsTests</c> and the rest — and none of those were touched. They are the
/// evidence the replacement changed nothing, so they are not repeated here.
/// </para>
/// <para>
/// These pin the part the call sites now delegate: the exception the guard throws, and the order it
/// checks connectivity against cancellation. The ordering matters because a caller that cancels a
/// request against a device that has also gone away should be told the device went away — that is
/// what the hand-rolled code did, connectivity first and the token second, and it is the detail a
/// future edit to the shared helper could silently flip for all fifty sites at once.
/// </para>
/// </remarks>
public class ConnectionGuardTests
{
    [Fact]
    public void EnsureConnected_WhenConnected_DoesNotThrow()
    {
        ConnectionGuard.EnsureConnected(isConnected: true);
    }

    [Fact]
    public void EnsureConnected_WhenNotConnected_ThrowsWithTheHistoricalMessage()
    {
        var ex = Assert.Throws<DeviceNotConnectedException>(
            () => ConnectionGuard.EnsureConnected(isConnected: false));

        // Byte-for-byte what every hand-rolled guard threw, and still an
        // InvalidOperationException so pre-existing catch sites keep working.
        Assert.Equal("Device is not connected.", ex.Message);
        Assert.False(ex.IsShuttingDown);
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
    }

    [Fact]
    public void EnsureConnected_WhenConnectedAndTokenLive_DoesNotThrow()
    {
        using var cts = new CancellationTokenSource();

        ConnectionGuard.EnsureConnected(isConnected: true, cts.Token);
    }

    [Fact]
    public void EnsureConnected_WhenConnectedAndTokenAlreadyCancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => ConnectionGuard.EnsureConnected(isConnected: true, cts.Token));
    }

    [Fact]
    public void EnsureConnected_WhenNotConnectedAndTokenAlreadyCancelled_ReportsTheDisconnect()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Connectivity is checked first, exactly as the hand-rolled blocks did: the guard came
        // before the ThrowIfCancellationRequested that followed it.
        Assert.Throws<DeviceNotConnectedException>(
            () => ConnectionGuard.EnsureConnected(isConnected: false, cts.Token));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EnsureConnected_OnAnOperationHost_ReadsIsConnectedAndNothingElse(bool isConnected)
    {
        // The fake throws on every other member, so a guard that started reaching for the
        // transport, the channels lock or device I/O would fail loudly here.
        var host = new GuardOnlyOperationHost { IsConnected = isConnected };

        if (isConnected)
        {
            host.EnsureConnected();
            host.EnsureConnected(CancellationToken.None);
        }
        else
        {
            Assert.Throws<DeviceNotConnectedException>(() => host.EnsureConnected());
            Assert.Throws<DeviceNotConnectedException>(() => host.EnsureConnected(CancellationToken.None));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EnsureConnected_OnATextExchangeHost_ReadsIsConnectedAndNothingElse(bool isConnected)
    {
        var host = new GuardOnlyTextExchangeHost { IsConnected = isConnected };

        if (isConnected)
        {
            host.EnsureConnected();
            host.EnsureConnected(CancellationToken.None);
        }
        else
        {
            Assert.Throws<DeviceNotConnectedException>(() => host.EnsureConnected());
            Assert.Throws<DeviceNotConnectedException>(() => host.EnsureConnected(CancellationToken.None));
        }
    }

    /// <summary>
    /// An <see cref="IDeviceOperationHost"/> that answers <see cref="IsConnected"/> and refuses
    /// everything else.
    /// </summary>
    private sealed class GuardOnlyOperationHost : IDeviceOperationHost
    {
        public bool IsConnected { get; set; }

        public bool IsStreaming { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public int StreamingFrequency => throw new NotSupportedException();
        public DeviceMetadata Metadata => throw new NotSupportedException();
        public long ChannelStateVersion => throw new NotSupportedException();
        public void StopStreaming() => throw new NotSupportedException();
        public void StartStreaming() => throw new NotSupportedException();
        public void Send<T>(IOutboundMessage<T> message) => throw new NotSupportedException();
        public void Disconnect() => throw new NotSupportedException();
        public IReadOnlyList<IChannel> SnapshotChannels() => throw new NotSupportedException();
        public void WithChannelsLock(Action action) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Action setupAction,
            int responseTimeoutMs = 1000,
            int completionTimeoutMs = 250,
            CancellationToken cancellationToken = default,
            Func<CancellationToken, Task>? prepareAsync = null,
            Func<Task>? finalizeAsync = null,
            bool keepBlankLines = false) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> DrainErrorQueueAsync(
            int maxIterations = 256,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ExecuteRawCaptureAsync(
            Func<Stream, CancellationToken, Task> rawAction,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void EnsureSupported(DeviceFeature feature) => throw new NotSupportedException();
        public FeatureNotSupportedException CreateFeatureNotSupportedException(DeviceFeature feature)
            => throw new NotSupportedException();
        public void RaiseStreamFrameDiscarded(StreamFrameDiscardedEventArgs e) => throw new NotSupportedException();
        public void RaiseGapDetected(TimestampGapEventArgs e) => throw new NotSupportedException();
        public void RaiseRawStreamFrame(DaqifiOutMessage message) => throw new NotSupportedException();
        public void RaiseStreamDecodeFailure(Exception error) => throw new NotSupportedException();
    }

    /// <summary>
    /// An <see cref="ITextExchangeHost"/> that answers <see cref="IsConnected"/> and refuses
    /// everything else.
    /// </summary>
    private sealed class GuardOnlyTextExchangeHost : ITextExchangeHost
    {
        public bool IsConnected { get; set; }

        public bool IsShuttingDown => throw new NotSupportedException();
        public IStreamTransport? Transport => throw new NotSupportedException();
        public IMessageConsumer<DaqifiOutMessage>? MessageConsumer => throw new NotSupportedException();
        public bool HoldsOperationLock => throw new NotSupportedException();
        public bool IsInsideTextExchange { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public ILogger Logger => throw new NotSupportedException();
        public TimeProvider TimeProvider => TimeProvider.System;
        public void AttachInboundHandler(IMessageConsumer<DaqifiOutMessage> consumer) => throw new NotSupportedException();
        public void DetachInboundHandler(IMessageConsumer<DaqifiOutMessage> consumer) => throw new NotSupportedException();
        public Task WaitForOperationLockAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public void EnterOperationLockOwnership() => throw new NotSupportedException();
        public void ExitOperationLockOwnership() => throw new NotSupportedException();
        public Task DrainOutboundQueueAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public OutboundWriterSample? SampleOutboundWriter() => throw new NotSupportedException();
        public IDisposable SubscribeConsumerErrors(IMessageConsumer<string> consumer) => throw new NotSupportedException();
        public void OnStaleLineBoundaryCaptured() => throw new NotSupportedException();
        public void OnSendBoundaryCaptured() => throw new NotSupportedException();
        public void OnReplyWaitCompleted(bool sawResponse) => throw new NotSupportedException();
    }
}
