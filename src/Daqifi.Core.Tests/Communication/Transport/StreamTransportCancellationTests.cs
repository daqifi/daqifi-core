using Daqifi.Core.Communication.Transport;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;

namespace Daqifi.Core.Tests.Communication.Transport;

/// <summary>
/// Pins the cancellation contract added to <see cref="IStreamTransport.ConnectAsync(ConnectionRetryOptions?, CancellationToken)"/>
/// in issue #341 and made real during a dial in #1032: an attempt in flight is aborted, a
/// caller-driven cancel is never disguised as a connect timeout, and the transport can be connected
/// again afterwards.
/// </summary>
public class StreamTransportCancellationTests
{
    [Fact]
    public async Task TcpConnectAsync_CanceledMidAttempt_ThrowsOperationCanceledPromptly()
    {
        // A never-completing connect task plus a 30s timeout: without the token being honored this
        // would sit here for the full half-minute.
        using var transport = new TcpStreamTransport(IPAddress.Parse("192.0.2.1"), 9760);
        transport.ConnectTaskFactory = (_, token) => Task.Delay(Timeout.Infinite, token);
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
        transport.ConnectTaskFactory = (_, token) =>
        {
            attempts++;
            return Task.Delay(Timeout.Infinite, token);
        };

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.ConnectAsync(cancellationToken: cts.Token));
        Assert.Equal(0, attempts);
    }

    [Fact]
    public async Task TcpConnectAsync_TimeoutWithALiveToken_StillSurfacesAsTimeoutException()
    {
        // Regression guard for the linked cancellation source: now that the caller's token shares a
        // source with the timeout, the two must still be told apart. A timeout is a device problem
        // the caller must see as such (daqifi-desktop#517), not a TaskCanceledException.
        using var transport = new TcpStreamTransport(IPAddress.Parse("192.0.2.1"), 9760);
        transport.ConnectTaskFactory = (_, token) => Task.Delay(Timeout.Infinite, token);
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

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.ConnectAsync(cancellationToken: cts.Token));
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public async Task TcpConnectAsync_CanceledMidDial_AbortsTheDialAndLeavesTheTransportReusable()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var transport = new TcpStreamTransport(
                IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var dialing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var dialToken = CancellationToken.None;
            transport.ConnectTaskFactory = (_, token) =>
            {
                dialToken = token;
                dialing.SetResult();
                return Task.Delay(Timeout.Infinite, token);
            };
            var reported = new List<TransportStatusEventArgs>();
            transport.StatusChanged += (_, e) => reported.Add(e);

            using var cts = new CancellationTokenSource();
            var connect = transport.ConnectAsync(LongConnect, cts.Token);
            await dialing.Task.WaitAsync(Budget);
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect.WaitAsync(Budget));
            Assert.True(dialToken.IsCancellationRequested, "The cancel must reach the dial, not just the wait for it.");
            Assert.IsAssignableFrom<OperationCanceledException>(Assert.Single(reported).Error);
            Assert.False(transport.IsConnected);

            transport.ConnectTaskFactory = null;
            await transport.ConnectAsync().WaitAsync(Budget);

            Assert.True(transport.IsConnected);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task TcpConnectAsync_CanceledMidDial_DoesNotRetry()
    {
        using var transport = new TcpStreamTransport(IPAddress.Parse("192.0.2.1"), 9760);
        using var cts = new CancellationTokenSource();
        var attempts = 0;
        transport.ConnectTaskFactory = (_, token) =>
        {
            attempts++;
            cts.Cancel();
            return Task.Delay(Timeout.Infinite, token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.ConnectAsync(
            new ConnectionRetryOptions { MaxAttempts = 5, InitialDelay = TimeSpan.Zero }, cts.Token));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task TcpConnectAsync_CanceledAsTheDialCompletes_DoesNotAnnounceAConnection()
    {
        // The token is signalled, but too late for the dial to notice. Announcing the connection
        // would leave it open with nobody holding it.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var transport = new TcpStreamTransport(IPAddress.Loopback, port);
            using var cts = new CancellationTokenSource();
            transport.ConnectTaskFactory = async (client, _) =>
            {
                await client.ConnectAsync(IPAddress.Loopback, port, CancellationToken.None);
                await cts.CancelAsync();
            };
            var reported = new List<bool>();
            transport.StatusChanged += (_, e) => reported.Add(e.IsConnected);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => transport.ConnectAsync(cancellationToken: cts.Token));

            Assert.False(transport.IsConnected);
            Assert.False(transport.IsDropDetectionArmed);
            Assert.Equal(new[] { false }, reported);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task SerialConnectAsync_OpensThePortOffTheCallersThread()
    {
        // An open can block for as long as the driver likes; ConnectAsync used to run it inline, so
        // "await ConnectAsync()" froze a UI thread for exactly as long as Connect() did.
        using var serial = new HeldSerialTransport();
        var callerThread = Environment.CurrentManagedThreadId;

        var connect = serial.Transport.ConnectAsync(LongConnect);

        Assert.False(connect.IsCompleted);
        var open = await serial.NextOpenAsync();
        Assert.NotEqual(callerThread, open.ThreadId);

        open.Release();
        await connect.WaitAsync(Budget);
    }

    [Fact]
    public async Task SerialConnectAsync_CanceledMidOpen_ThrowsAtOnceAndLeavesTheTransportReusable()
    {
        using var serial = new HeldSerialTransport();
        var reported = new List<TransportStatusEventArgs>();
        serial.Transport.StatusChanged += (_, e) => reported.Add(e);
        using var cts = new CancellationTokenSource();

        var connect = serial.Transport.ConnectAsync(LongConnect, cts.Token);
        var first = await serial.NextOpenAsync();
        await cts.CancelAsync();

        // The open is still stuck in native code, and the caller is free regardless.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect.WaitAsync(Budget));
        Assert.IsAssignableFrom<OperationCanceledException>(Assert.Single(reported).Error);
        Assert.False(serial.Transport.IsConnected);
        Assert.False(first.PortDisposed.IsCompleted, "The port must not be disposed under an open still using it.");

        // Reconnecting waits for that open to let go instead of opening the port a second time.
        var reconnect = serial.Transport.ConnectAsync(LongConnect);
        await Task.Delay(50);
        Assert.Equal(1, serial.OpenCount);

        first.Release();
        await first.PortDisposed.WaitAsync(Budget);
        var second = await serial.NextOpenAsync();
        second.Release();
        await reconnect.WaitAsync(Budget);

        Assert.Equal(new[] { false, true }, reported.Select(e => e.IsConnected));
        Assert.False(second.PortDisposed.IsCompleted);
    }

    [Fact]
    public async Task SerialConnectAsync_WhenTheAbandonedOpenFails_TheTransportIsStillReusable()
    {
        using var serial = new HeldSerialTransport();
        using var cts = new CancellationTokenSource();
        var connect = serial.Transport.ConnectAsync(LongConnect, cts.Token);
        var first = await serial.NextOpenAsync();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect.WaitAsync(Budget));

        first.Release(new IOException("the device went away"));

        var reconnect = serial.Transport.ConnectAsync(LongConnect);
        (await serial.NextOpenAsync()).Release();
        await reconnect.WaitAsync(Budget);
    }

    [Fact]
    public async Task SerialConnectAsync_WhileAnAbandonedOpenIsStillStuck_TimesOutWithoutOpeningAgain()
    {
        using var serial = new HeldSerialTransport();
        using var cts = new CancellationTokenSource();
        var connect = serial.Transport.ConnectAsync(LongConnect, cts.Token);
        await serial.NextOpenAsync();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect.WaitAsync(Budget));

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => serial.Transport.ConnectAsync(
            new ConnectionRetryOptions { Enabled = false, ConnectionTimeout = TimeSpan.FromMilliseconds(50) })
            .WaitAsync(Budget));

        Assert.Contains(HeldSerialTransport.PortName, ex.Message);
        Assert.Equal(1, serial.OpenCount);
    }

    [Fact]
    public async Task SerialConnectAsync_CanceledWhileWaitingForAnAbandonedOpen_Throws()
    {
        using var serial = new HeldSerialTransport();
        using var first = new CancellationTokenSource();
        var connect = serial.Transport.ConnectAsync(LongConnect, first.Token);
        await serial.NextOpenAsync();
        await first.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect.WaitAsync(Budget));

        using var second = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => serial.Transport.ConnectAsync(LongConnect, second.Token).WaitAsync(Budget));

        Assert.Equal(1, serial.OpenCount);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task SerialConnectAsync_TornDownMidOpen_EndsTheWholeConnect(bool dispose, bool retry)
    {
        using var serial = new HeldSerialTransport();
        var reported = new List<bool>();
        serial.Transport.StatusChanged += (_, e) => reported.Add(e.IsConnected);
        var connect = serial.Transport.ConnectAsync(retry ? RetryAtOnce : LongConnect);
        var open = await serial.NextOpenAsync();

        await TearDownAsync(serial.Transport, dispose);

        // Ended without waiting for the stuck open, and never retried: a second open would pulse
        // DTR and reconnect a device the caller has just disconnected.
        var ex = await Record.ExceptionAsync(() => connect.WaitAsync(Budget));
        Assert.IsType(dispose ? typeof(ObjectDisposedException) : typeof(TransportNotConnectedException), ex);
        Assert.False(open.PortDisposed.IsCompleted);

        open.Release();
        await open.PortDisposed.WaitAsync(Budget);
        Assert.Equal(1, serial.OpenCount);
        Assert.False(serial.Transport.IsConnected);
        Assert.Empty(reported);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SerialConnectAsync_TornDownWhileWaitingOutABackoff_DoesNotRetry(bool dispose)
    {
        using var serial = new HeldSerialTransport();
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        serial.Transport.StatusChanged += (_, _) => failed.TrySetResult();
        var connect = serial.Transport.ConnectAsync(new ConnectionRetryOptions
        {
            MaxAttempts = 2,
            InitialDelay = TimeSpan.FromMinutes(1),
            MaxDelay = TimeSpan.FromMinutes(1)
        });
        (await serial.NextOpenAsync()).Release(new IOException("not yet"));
        await failed.Task.WaitAsync(Budget);

        await TearDownAsync(serial.Transport, dispose);

        var ex = await Record.ExceptionAsync(() => connect.WaitAsync(Budget));
        Assert.IsType(dispose ? typeof(ObjectDisposedException) : typeof(TransportNotConnectedException), ex);
        Assert.Equal(1, serial.OpenCount);
    }

    [Fact]
    public async Task SerialConnectAsync_DisconnectedWhileWaitingForAnAbandonedOpen_DoesNotOpenAfterwards()
    {
        using var serial = new HeldSerialTransport();
        using var cts = new CancellationTokenSource();
        var canceled = serial.Transport.ConnectAsync(LongConnect, cts.Token);
        var open = await serial.NextOpenAsync();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.WaitAsync(Budget));
        var waiting = serial.Transport.ConnectAsync(LongConnect);

        await serial.Transport.DisconnectAsync();

        await Assert.ThrowsAsync<TransportNotConnectedException>(() => waiting.WaitAsync(Budget));
        open.Release();
        await open.PortDisposed.WaitAsync(Budget);
        Assert.Equal(1, serial.OpenCount);
    }

    [Fact]
    public async Task SerialConnectAsync_AfterADisconnectMidOpen_ConnectsAndKeepsItsOwnPort()
    {
        // The disconnected connect is still unwinding when the next one starts. Neither its cleanup
        // nor its late open may touch the new connection.
        using var serial = new HeldSerialTransport();
        var first = serial.Transport.ConnectAsync(RetryAtOnce);
        var firstOpen = await serial.NextOpenAsync();
        await serial.Transport.DisconnectAsync();
        var reported = new List<bool>();
        serial.Transport.StatusChanged += (_, e) => reported.Add(e.IsConnected);

        var second = serial.Transport.ConnectAsync(LongConnect);
        firstOpen.Release();
        var secondOpen = await serial.NextOpenAsync();
        Assert.True(firstOpen.PortDisposed.IsCompleted, "The second open must not start until the first port is closed.");
        secondOpen.Release();
        await second.WaitAsync(Budget);
        await Assert.ThrowsAsync<TransportNotConnectedException>(() => first.WaitAsync(Budget));

        Assert.False(secondOpen.PortDisposed.IsCompleted);
        Assert.Equal(new[] { true }, reported);
        Assert.Equal(2, serial.OpenCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TcpConnectAsync_TornDownMidDial_AbortsTheDialAndDoesNotRetry(bool dispose)
    {
        using var transport = new TcpStreamTransport(IPAddress.Parse("192.0.2.1"), 9760);
        var dials = new ConcurrentQueue<CancellationToken>();
        var dialing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.ConnectTaskFactory = (_, token) =>
        {
            dials.Enqueue(token);
            dialing.TrySetResult();
            return Task.Delay(Timeout.Infinite, token);
        };
        var reported = new List<bool>();
        transport.StatusChanged += (_, e) => reported.Add(e.IsConnected);
        var connect = transport.ConnectAsync(RetryAtOnce);
        await dialing.Task.WaitAsync(Budget);

        await TearDownAsync(transport, dispose);

        var ex = await Record.ExceptionAsync(() => connect.WaitAsync(Budget));
        Assert.IsType(dispose ? typeof(ObjectDisposedException) : typeof(TransportNotConnectedException), ex);
        Assert.True(Assert.Single(dials).IsCancellationRequested);
        Assert.Empty(reported);
    }

    [Fact]
    public async Task TcpConnectAsync_DisconnectedAsTheDialCompletes_DoesNotPublishTheSocket()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var transport = new TcpStreamTransport(IPAddress.Loopback, port);
            transport.ConnectTaskFactory = async (client, _) =>
            {
                await client.ConnectAsync(IPAddress.Loopback, port, CancellationToken.None);
                await transport.DisconnectAsync();
            };

            await Assert.ThrowsAsync<TransportNotConnectedException>(() => transport.ConnectAsync(RetryAtOnce));

            Assert.False(transport.IsConnected);
            Assert.False(transport.IsDropDetectionArmed);

            transport.ConnectTaskFactory = null;
            await transport.ConnectAsync().WaitAsync(Budget);
            Assert.True(transport.IsConnected);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task TearDownAsync(IStreamTransport transport, bool dispose)
    {
        if (dispose)
        {
            transport.Dispose();
        }
        else
        {
            await transport.DisconnectAsync();
        }
    }

    /// <summary>Several attempts with no backoff between them.</summary>
    private static ConnectionRetryOptions RetryAtOnce => new()
    {
        MaxAttempts = 3,
        InitialDelay = TimeSpan.Zero,
        ConnectionTimeout = TimeSpan.FromMinutes(1)
    };

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    /// <summary>A single attempt with a timeout no passing test comes near.</summary>
    private static ConnectionRetryOptions LongConnect => new()
    {
        Enabled = false,
        ConnectionTimeout = TimeSpan.FromMinutes(1)
    };

    /// <summary>
    /// A serial transport whose opens block until the test releases them, standing in for a
    /// <see cref="SerialPort.Open"/> stuck in native code.
    /// </summary>
    private sealed class HeldSerialTransport : IDisposable
    {
        public const string PortName = "/dev/daqifi-held-open";

        private readonly System.Threading.Channels.Channel<HeldOpen> _opens =
            System.Threading.Channels.Channel.CreateUnbounded<HeldOpen>();
        private readonly ConcurrentBag<HeldOpen> _all = [];

        public HeldSerialTransport()
        {
            Transport = new SerialStreamTransport(PortName, new SerialTransportOptions { LivenessCheckInterval = TimeSpan.Zero })
            {
                PortOpener = port =>
                {
                    var open = new HeldOpen(port);
                    _all.Add(open);
                    _opens.Writer.TryWrite(open);
                    open.Block();
                }
            };
        }

        public SerialStreamTransport Transport { get; }

        public int OpenCount => _all.Count;

        public Task<HeldOpen> NextOpenAsync() => _opens.Reader.ReadAsync().AsTask().WaitAsync(Budget);

        public void Dispose()
        {
            foreach (var open in _all)
            {
                open.Release();
            }

            Transport.Dispose();
        }
    }

    private sealed class HeldOpen
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HeldOpen(SerialPort port) => port.Disposed += (_, _) => _disposed.TrySetResult();

        public int ThreadId { get; } = Environment.CurrentManagedThreadId;

        public Task PortDisposed => _disposed.Task;

        public void Block() => _released.Task.GetAwaiter().GetResult();

        public void Release(Exception? failure = null)
        {
            if (failure != null)
            {
                _released.TrySetException(failure);
            }
            else
            {
                _released.TrySetResult();
            }
        }
    }
}
