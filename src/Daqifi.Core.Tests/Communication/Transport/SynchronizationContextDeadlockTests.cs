using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Daqifi.Core.Tests.TestSupport;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Daqifi.Core.Tests.Communication.Transport;

/// <summary>
/// Pins the fix for issue #495: <see cref="DaqifiDevice.Connect"/> and
/// <see cref="DaqifiDevice.Disconnect"/> block on their transport's async work, so nothing
/// underneath them may resume on the caller's <see cref="SynchronizationContext"/>. A WPF/WinForms
/// app that calls them on the UI thread otherwise freezes permanently: the continuation is posted
/// back to a context that is already blocked inside <c>GetResult()</c>, with no timeout and no
/// exception.
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

    #region Device facades

    [Fact]
    public void DaqifiDevice_ConnectAndDisconnectOverTcp_UnderBlockedContext_Complete()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        // No explicit accept: the OS completes the handshake into the listen backlog, which is
        // all the dial needs, and it keeps a blocking Task.Result out of the test body.
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            using var device = new DaqifiDevice("Blocked", new TcpStreamTransport(IPAddress.Loopback, port));

            var run = RunBlocking(() =>
            {
                device.Connect();
                device.Disconnect();
            });

            Assert.True(run.Completed, "DaqifiDevice.Connect()/Disconnect() deadlocked under a UI-like context.");
            Assert.Null(run.Error);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void DaqifiDevice_ConnectOverSerial_UnderBlockedContext_ThrowsTheTypedExceptionUnwrapped()
    {
        var portName = AbsentSerialPorts.Create();
        using var device = new DaqifiDevice("Blocked", new SerialStreamTransport(portName));

        var run = RunBlocking(device.Connect);

        Assert.True(run.Completed, "DaqifiDevice.Connect() over serial deadlocked under a UI-like context.");
        // The exact type proves the facade unwraps the failure rather than surfacing an
        // AggregateException.
        Assert.IsType<SerialPortConnectException>(run.Error);
    }

    /// <summary>
    /// <see cref="IStreamTransport"/> is a consumer's to implement, and a consumer has no analyzer
    /// insisting on <c>ConfigureAwait(false)</c>. Its one plain await must not hang the UI thread.
    /// </summary>
    [Fact]
    public void DaqifiDevice_OverATransportWithNakedAwaits_UnderBlockedContext_Completes()
    {
        using var device = new DaqifiDevice("Blocked", new NakedAwaitTransport());
        SynchronizationContext? afterwards = null;
        SynchronizationContext? before = null;

        var run = RunBlocking(() =>
        {
            before = SynchronizationContext.Current;
            device.Connect();
            device.Disconnect();
            afterwards = SynchronizationContext.Current;
        });

        Assert.True(run.Completed, "A transport's naked await deadlocked DaqifiDevice.Connect()/Disconnect().");
        Assert.Null(run.Error);
        Assert.NotNull(before);
        Assert.Same(before, afterwards);
    }

    private sealed class NakedAwaitTransport : IStreamTransport
    {
        public Stream Stream { get; } = new MemoryStream();

        public bool IsConnected { get; private set; }

        public string ConnectionInfo => "Naked";

        public bool IsUsbConnection => false;

        public event EventHandler<TransportStatusEventArgs>? StatusChanged;

        public async Task ConnectAsync(
            ConnectionRetryOptions? retryOptions = null,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(20, cancellationToken);
            IsConnected = true;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(true, ConnectionInfo));
        }

        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(20, CancellationToken.None);
            IsConnected = false;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(false, ConnectionInfo));
        }

        public void Dispose() => Stream.Dispose();
    }

    #endregion
}
