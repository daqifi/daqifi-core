using System.Diagnostics;
using Daqifi.Core.Communication.Transport;

namespace Daqifi.Core.Tests.Communication.Transport;

/// <summary>
/// Directly exercises the shared retry/backoff scaffolding used by both
/// <see cref="TcpStreamTransport"/> and <see cref="SerialStreamTransport"/>.
/// These attempt-loop semantics were previously duplicated (and untested) in
/// each transport; centralizing them here pins the behavior both rely on.
/// </summary>
public class ConnectRetryExecutorTests
{
    private static ConnectionRetryOptions FastRetry(int maxAttempts) => new()
    {
        Enabled = true,
        MaxAttempts = maxAttempts,
        InitialDelay = TimeSpan.Zero,
        MaxDelay = TimeSpan.Zero,
        BackoffMultiplier = 1.0,
        ConnectionTimeout = TimeSpan.FromSeconds(1)
    };

    [Fact]
    public async Task ExecuteAsync_SucceedsFirstAttempt_ReportsConnectedOnce()
    {
        var attempts = 0;
        var failedCleanups = 0;
        var statuses = new List<(bool Connected, Exception? Error)>();

        await ConnectRetryExecutor.ExecuteAsync(
            FastRetry(3),
            connectAttempt: _ => { attempts++; return Task.CompletedTask; },
            onAttemptFailed: () => failedCleanups++,
            onStatusChanged: (c, e) => statuses.Add((c, e)));

        Assert.Equal(1, attempts);
        Assert.Equal(0, failedCleanups);
        var status = Assert.Single(statuses);
        Assert.True(status.Connected);
        Assert.Null(status.Error);
    }

    [Fact]
    public async Task ExecuteAsync_ReceivesResolvedOptions_InConnectAttempt()
    {
        var options = FastRetry(2);
        ConnectionRetryOptions? seen = null;

        await ConnectRetryExecutor.ExecuteAsync(
            options,
            connectAttempt: o => { seen = o; return Task.CompletedTask; },
            onAttemptFailed: () => { },
            onStatusChanged: (_, _) => { });

        Assert.Same(options, seen);
    }

    [Fact]
    public async Task ExecuteAsync_NullOptions_UsesSingleNoRetryAttempt()
    {
        var attempts = 0;
        var failedCleanups = 0;
        var boom = new InvalidOperationException("boom");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ConnectRetryExecutor.ExecuteAsync(
                retryOptions: null,
                connectAttempt: _ => { attempts++; throw boom; },
                onAttemptFailed: () => failedCleanups++,
                onStatusChanged: (_, _) => { }));

        Assert.Same(boom, thrown);
        Assert.Equal(1, attempts);          // NoRetry => exactly one attempt
        Assert.Equal(1, failedCleanups);    // cleanup ran for the failed handle
    }

    [Fact]
    public async Task ExecuteAsync_RetriesUntilSuccess_CleansUpEachFailedAttempt()
    {
        var attempts = 0;
        var failedCleanups = 0;
        var statuses = new List<(bool Connected, Exception? Error)>();

        await ConnectRetryExecutor.ExecuteAsync(
            FastRetry(3),
            connectAttempt: _ =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw new InvalidOperationException($"fail {attempts}");
                }
                return Task.CompletedTask;
            },
            onAttemptFailed: () => failedCleanups++,
            onStatusChanged: (c, e) => statuses.Add((c, e)));

        Assert.Equal(3, attempts);
        Assert.Equal(2, failedCleanups);

        // Two "retrying..." failure notifications, then a final success.
        Assert.Equal(3, statuses.Count);
        Assert.False(statuses[0].Connected);
        Assert.Contains("retrying", statuses[0].Error!.Message);
        Assert.False(statuses[1].Connected);
        Assert.Contains("retrying", statuses[1].Error!.Message);
        Assert.True(statuses[2].Connected);
        Assert.Null(statuses[2].Error);
    }

    [Fact]
    public async Task ExecuteAsync_AllAttemptsFail_ThrowsLastExceptionAndReportsItUnwrapped()
    {
        var attempts = 0;
        var failedCleanups = 0;
        var statuses = new List<(bool Connected, Exception? Error)>();
        var lastBoom = new InvalidOperationException("final");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ConnectRetryExecutor.ExecuteAsync(
                FastRetry(3),
                connectAttempt: _ =>
                {
                    attempts++;
                    throw attempts < 3 ? new InvalidOperationException($"fail {attempts}") : lastBoom;
                },
                onAttemptFailed: () => failedCleanups++,
                onStatusChanged: (c, e) => statuses.Add((c, e))));

        Assert.Same(lastBoom, thrown);
        Assert.Equal(3, attempts);
        Assert.Equal(3, failedCleanups);

        // Two "retrying..." notifications, then the terminal failure carrying the
        // original exception (not the retry wrapper).
        Assert.Equal(3, statuses.Count);
        Assert.Contains("retrying", statuses[0].Error!.Message);
        Assert.Contains("retrying", statuses[1].Error!.Message);
        Assert.False(statuses[2].Connected);
        Assert.Same(lastBoom, statuses[2].Error);
    }

    [Fact]
    public async Task ExecuteAsync_RetryDisabledWithMultipleMaxAttempts_TriesOnce()
    {
        var attempts = 0;
        var options = new ConnectionRetryOptions
        {
            Enabled = false,
            MaxAttempts = 5,
            InitialDelay = TimeSpan.Zero
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ConnectRetryExecutor.ExecuteAsync(
                options,
                connectAttempt: _ => { attempts++; throw new InvalidOperationException("boom"); },
                onAttemptFailed: () => { },
                onStatusChanged: (_, _) => { }));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_AppliesBackoffDelayBetweenAttempts()
    {
        var options = new ConnectionRetryOptions
        {
            Enabled = true,
            MaxAttempts = 2,
            InitialDelay = TimeSpan.FromMilliseconds(150),
            MaxDelay = TimeSpan.FromSeconds(1),
            BackoffMultiplier = 2.0
        };

        var attempts = 0;
        var sw = Stopwatch.StartNew();

        await ConnectRetryExecutor.ExecuteAsync(
            options,
            connectAttempt: _ =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new InvalidOperationException("first fails");
                }
                return Task.CompletedTask;
            },
            onAttemptFailed: () => { },
            onStatusChanged: (_, _) => { });

        sw.Stop();
        Assert.Equal(2, attempts);
        // A backoff delay must have elapsed before the second attempt. Use a loose
        // lower bound (well under the ~150ms configured delay) to avoid flakiness.
        Assert.True(sw.ElapsedMilliseconds >= 80,
            $"Expected a backoff delay before retry, but only {sw.ElapsedMilliseconds}ms elapsed.");
    }
}
