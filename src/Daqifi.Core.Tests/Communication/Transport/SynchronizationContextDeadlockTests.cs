using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Daqifi.Core.Tests.Communication.Transport;

/// <summary>
/// Pins the fix for issue #495: the library's synchronous facades block on their own async
/// work (<c>ConnectAsync().GetAwaiter().GetResult()</c>), so every await underneath them must
/// resume on the thread pool rather than on the caller's <see cref="SynchronizationContext"/>.
/// A WPF/WinForms app that calls <see cref="DaqifiDeviceFactory.ConnectTcp(IPAddress,int,DeviceConnectionOptions?)"/>
/// on the UI thread otherwise freezes permanently: the continuation is posted back to a context
/// that is already blocked inside <c>GetResult()</c>, with no timeout and no exception.
///
/// Each test runs the blocking call on a dedicated thread that has a UI-like, single-threaded
/// context installed and never pumps it. If a naked <c>await</c> survives anywhere on the path,
/// the thread never returns and the test fails on the join timeout instead of hanging the run.
/// </summary>
public class SynchronizationContextDeadlockTests
{
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(10);

    #region Harness

    /// <summary>
    /// A stand-in for a UI dispatcher: continuations posted to it are queued and only run when
    /// the owning thread pumps. The owning thread here is blocked inside a synchronous facade
    /// and never pumps, which is exactly the condition that deadlocks a WPF app.
    /// </summary>
    private sealed class BlockedSynchronizationContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state) =>
            throw new NotSupportedException(
                "Nothing on the connect path may call Send: a synchronous dispatch from a thread " +
                "that is already blocked deadlocks just as surely as a posted continuation.");

        public override SynchronizationContext CreateCopy() => this;

        /// <summary>
        /// Runs the queued continuations from another thread so a failed (i.e. genuinely
        /// deadlocked) run does not leak a permanently blocked thread into the rest of the suite.
        /// Only cleanup — the assertion has already been decided by the time this runs.
        /// </summary>
        public void DrainUntil(Thread blocked)
        {
            var elapsed = Stopwatch.StartNew();
            while (blocked.IsAlive && elapsed.Elapsed < TimeSpan.FromSeconds(10))
            {
                if (!_queue.TryTake(out var work, millisecondsTimeout: 50))
                {
                    continue;
                }

                try
                {
                    work.Callback(work.State);
                }
                catch
                {
                    // Cleanup path: an exception here cannot change a verdict already reached.
                }
            }
        }
    }

    private sealed record BlockingRun(bool Completed, Exception? Error);

    /// <summary>
    /// Runs <paramref name="body"/> on a fresh thread under a never-pumped single-threaded
    /// context and reports whether it returned within <paramref name="timeout"/>.
    /// </summary>
    private static BlockingRun RunBlocking(Action body, TimeSpan? timeout = null)
    {
        var context = new BlockedSynchronizationContext();
        Exception? error = null;

        var thread = new Thread(() =>
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                body();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        })
        {
            IsBackground = true,
            Name = nameof(BlockedSynchronizationContext)
        };

        thread.Start();
        var completed = thread.Join(timeout ?? JoinTimeout);
        if (!completed)
        {
            context.DrainUntil(thread);
        }

        return new BlockingRun(completed, error);
    }

    private static async Task NakedDelayAsync() => await Task.Delay(20);

    private static async Task ConfiguredDelayAsync() => await Task.Delay(20).ConfigureAwait(false);

    #endregion

    #region Harness self-checks

    /// <summary>
    /// Without this test the others could pass vacuously — a harness that never reproduces the
    /// deadlock proves nothing about the fix. A naked await under the blocked context must hang.
    /// </summary>
    [Fact]
    public void Harness_NakedAwaitBlockedOnTheContext_Deadlocks()
    {
        var run = RunBlocking(() => NakedDelayAsync().GetAwaiter().GetResult(), TimeSpan.FromSeconds(2));

        Assert.False(
            run.Completed,
            "The harness failed to reproduce the #495 deadlock, so the other tests in this class prove nothing.");
    }

    [Fact]
    public void Harness_ConfiguredAwaitBlockedOnTheContext_Completes()
    {
        var run = RunBlocking(() => ConfiguredDelayAsync().GetAwaiter().GetResult());

        Assert.True(run.Completed, "ConfigureAwait(false) should resume on the thread pool, not the blocked context.");
        Assert.Null(run.Error);
    }

    #endregion

    #region Transports

    [Fact]
    public void TcpStreamTransport_ConnectAndDisconnect_UnderBlockedContext_Complete()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        // No explicit accept: the OS completes the handshake into the listen backlog, which is
        // all the dial needs, and it keeps a blocking Task.Result out of the test body.
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            using var transport = new TcpStreamTransport(IPAddress.Loopback, port);

            var run = RunBlocking(() =>
            {
                transport.Connect();
                transport.Disconnect();
            });

            Assert.True(run.Completed, "TcpStreamTransport.Connect()/Disconnect() deadlocked under a UI-like context.");
            Assert.Null(run.Error);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// The serial case named in #495: the first <c>Open()</c> after a re-plug fails, retry engages,
    /// and the connect path parks on <c>await Task.Delay(backoff)</c>. That delay's continuation is
    /// what a blocked UI thread would never run.
    /// </summary>
    [Fact]
    public void SerialStreamTransport_ConnectWithRetry_UnderBlockedContext_ThrowsInsteadOfHanging()
    {
        using var transport = new SerialStreamTransport("COM999");
        var retry = new ConnectionRetryOptions
        {
            Enabled = true,
            MaxAttempts = 2,
            InitialDelay = TimeSpan.FromMilliseconds(50),
            MaxDelay = TimeSpan.FromMilliseconds(50),
            BackoffMultiplier = 1.0,
            ConnectionTimeout = TimeSpan.FromSeconds(1)
        };

        var run = RunBlocking(() => transport.ConnectAsync(retry).GetAwaiter().GetResult());

        Assert.True(run.Completed, "SerialStreamTransport connect-with-retry deadlocked under a UI-like context.");
        Assert.NotNull(run.Error);
        Assert.False(transport.IsConnected);
    }

    [Fact]
    public void UdpTransport_OpenAndClose_UnderBlockedContext_Complete()
    {
        using var transport = new UdpTransport();

        var run = RunBlocking(() =>
        {
            transport.Open();
            transport.Close();
        });

        Assert.True(run.Completed, "UdpTransport.Open()/Close() deadlocked under a UI-like context.");
        Assert.Null(run.Error);
        Assert.False(transport.IsOpen);
    }

    #endregion

    #region Retry scaffolding

    /// <summary>
    /// <see cref="ConnectRetryExecutor"/> is the one place both transports share, and the backoff
    /// delay it awaits is the single await most likely to strand a blocked caller.
    /// </summary>
    [Fact]
    public void ConnectRetryExecutor_BackoffDelay_UnderBlockedContext_ResumesAndRetries()
    {
        var attempts = 0;
        var retry = new ConnectionRetryOptions
        {
            Enabled = true,
            MaxAttempts = 2,
            InitialDelay = TimeSpan.FromMilliseconds(50),
            MaxDelay = TimeSpan.FromMilliseconds(50),
            BackoffMultiplier = 1.0,
            ConnectionTimeout = TimeSpan.FromSeconds(1)
        };

        var run = RunBlocking(() =>
            ConnectRetryExecutor.ExecuteAsync(
                retry,
                connectAttempt: (_, _) =>
                {
                    // First attempt fails the way a just-re-plugged port does, forcing the
                    // executor through its backoff delay before the second attempt.
                    attempts++;
                    return attempts == 1
                        ? Task.FromException(new IOException("port busy"))
                        : Task.CompletedTask;
                },
                onAttemptFailed: () => { },
                onStatusChanged: (_, _) => { }).GetAwaiter().GetResult());

        Assert.True(run.Completed, "ConnectRetryExecutor deadlocked on its backoff delay under a UI-like context.");
        Assert.Null(run.Error);
        Assert.Equal(2, attempts);
    }

    #endregion

    #region Factory facades

    /// <summary>
    /// The entry point the issue names: a WPF app calling the sync factory helper on its UI thread.
    /// Device initialization is switched off because a bare loopback listener answers no SCPI —
    /// the deadlock being pinned is in the connect path, which runs either way.
    /// </summary>
    [Fact]
    public void DaqifiDeviceFactory_ConnectTcp_UnderBlockedContext_Completes()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        DaqifiStreamingDevice? device = null;

        try
        {
            var run = RunBlocking(() =>
                device = DaqifiDeviceFactory.ConnectTcp(
                    IPAddress.Loopback,
                    port,
                    new DeviceConnectionOptions { InitializeDevice = false }));

            Assert.True(run.Completed, "DaqifiDeviceFactory.ConnectTcp deadlocked under a UI-like context.");
            Assert.Null(run.Error);
            Assert.NotNull(device);
        }
        finally
        {
            device?.Dispose();
            listener.Stop();
        }
    }

    #endregion
}
