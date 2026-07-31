using Daqifi.Core.Communication.Transport;
using System.Diagnostics;
using System.Net;

namespace Daqifi.Core.Tests.Communication.Transport;

/// <summary>
/// Pins the cancellation contract added to <see cref="IStreamTransport.ConnectAsync(ConnectionRetryOptions?, CancellationToken)"/>
/// in issue #341: an in-flight dial can be abandoned, a caller-driven cancel is never disguised as
/// a connect timeout, and a transport written against the pre-#341 interface still works.
/// </summary>
public class StreamTransportCancellationTests
{
    [Fact]
    public async Task TcpConnectAsync_CanceledMidAttempt_ThrowsOperationCanceledPromptly()
    {
        // A never-completing connect task plus a 30s timeout: without the token being honored this
        // would sit here for the full half-minute.
        using var transport = new TcpStreamTransport(IPAddress.Parse("192.0.2.1"), 9760);
        transport.ConnectTaskFactory = _ => Task.Delay(Timeout.Infinite);
        var options = new ConnectionRetryOptions
        {
            Enabled = false,
            MaxAttempts = 1,
            ConnectionTimeout = TimeSpan.FromSeconds(30)
        };

        using var cts = new CancellationTokenSource();
        var connect = transport.ConnectAsync(options, cts.Token);

        var sw = Stopwatch.StartNew();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);
        sw.Stop();

        Assert.IsNotType<TimeoutException>(thrown);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"Cancellation took {sw.ElapsedMilliseconds}ms; the connect timeout was not short-circuited.");
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task TcpConnectAsync_TokenAlreadyCanceled_NeverStartsAnAttempt()
    {
        using var transport = new TcpStreamTransport(IPAddress.Parse("192.0.2.1"), 9760);
        var attempts = 0;
        transport.ConnectTaskFactory = _ =>
        {
            attempts++;
            return Task.Delay(Timeout.Infinite);
        };

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.ConnectAsync(cts.Token));
        Assert.Equal(0, attempts);
    }

    [Fact]
    public async Task TcpConnectAsync_TimeoutWithALiveToken_StillSurfacesAsTimeoutException()
    {
        // Regression guard for the linked cancellation source: now that the caller's token shares a
        // source with the timeout, the two must still be told apart. A timeout is a device problem
        // the caller must see as such (daqifi-desktop#517), not a TaskCanceledException.
        using var transport = new TcpStreamTransport(IPAddress.Parse("192.0.2.1"), 9760);
        transport.ConnectTaskFactory = _ => Task.Delay(Timeout.Infinite);
        var options = new ConnectionRetryOptions
        {
            Enabled = false,
            MaxAttempts = 1,
            ConnectionTimeout = TimeSpan.FromMilliseconds(250)
        };

        using var cts = new CancellationTokenSource();

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => transport.ConnectAsync(options, cts.Token));
        Assert.Contains("192.0.2.1:9760", ex.Message);
        Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerException);
    }

    [Fact]
    public async Task SerialConnectAsync_TokenAlreadyCanceled_ThrowsWithoutOpeningThePort()
    {
        // A port name nothing can open: if the token were ignored the attempt would fail with an
        // IO/argument exception from Open() instead of the cancellation the caller asked for.
        using var transport = new SerialStreamTransport("/dev/null-daqifi-does-not-exist");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.ConnectAsync(cts.Token));
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task LegacyTransport_WrittenBeforeTheTokenExisted_StillConnectsThroughTheNewOverload()
    {
        // Source-compatibility guard: this transport implements only the pre-#341 members. It must
        // keep compiling, and the cancellable overload must fall back to the uncancellable one.
        IStreamTransport transport = new LegacyStreamTransport();

        await transport.ConnectAsync(CancellationToken.None);

        Assert.True(transport.IsConnected);
        transport.Dispose();
    }

    /// <summary>
    /// An <see cref="IStreamTransport"/> implementation frozen at the pre-#341 shape — it does not
    /// override either cancellable overload, so it exercises their default implementations.
    /// </summary>
    private sealed class LegacyStreamTransport : IStreamTransport
    {
        private readonly MemoryStream _stream = new();
        private bool _isConnected;

        public Stream Stream => _stream;
        public bool IsConnected => _isConnected;
        public string ConnectionInfo => "Legacy";

        public event EventHandler<TransportStatusEventArgs>? StatusChanged;

        public Task ConnectAsync() => ConnectAsync(null);

        public Task ConnectAsync(ConnectionRetryOptions? retryOptions)
        {
            _isConnected = true;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(true, ConnectionInfo));
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            _isConnected = false;
            return Task.CompletedTask;
        }

        public void Connect() => ConnectAsync().GetAwaiter().GetResult();

        public void Disconnect() => DisconnectAsync().GetAwaiter().GetResult();

        public void Dispose()
        {
            _isConnected = false;
            _stream.Dispose();
        }
    }
}
