using Daqifi.Core.Device;
using Daqifi.Core.Device.Internal;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Daqifi.Core.Tests.Device.Internal;

/// <summary>
/// Unit tests for <see cref="UsbStreamInterfaceInitializer"/>, which decides whether a device being
/// initialized must have its stream re-routed to USB and how hard to retry a transient rejection.
/// These pin the decision itself; <c>DaqifiDeviceInitializeTests</c> pins what the device does with
/// it end to end.
/// </summary>
public class UsbStreamInterfaceInitializerTests
{
    private const string ScpiError = "**ERROR: -200, \"Execution error\"";

    /// <summary>Bound on every wait so a policy regression fails the run instead of parking it.</summary>
    private static readonly TimeSpan RouteTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Records every attempt and answers each one from a queued script, so a test states the
    /// device's answers rather than the sender's mechanics.
    /// </summary>
    private sealed class ScriptedSender
    {
        private readonly Queue<IReadOnlyList<string>> _responses = new();

        public int AttemptCount { get; private set; }

        public List<CancellationToken> ObservedTokens { get; } = new();

        public ScriptedSender Answers(params string[] lines)
        {
            _responses.Enqueue(lines);
            return this;
        }

        public Task<IReadOnlyList<string>> SendAsync(CancellationToken cancellationToken)
        {
            AttemptCount++;
            ObservedTokens.Add(cancellationToken);

            // An unscripted attempt is a test bug, not a silent success: surface it.
            Assert.True(_responses.Count > 0, "The sender was invoked more times than the test scripted.");
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private static Task RouteAsync(
        ScriptedSender sender,
        bool isUsbConnection = true,
        bool preserveActiveStream = false,
        CancellationToken cancellationToken = default)
        => UsbStreamInterfaceInitializer
            .RouteStreamToUsbAsync(isUsbConnection, preserveActiveStream, sender.SendAsync, cancellationToken)
            .WaitAsync(RouteTimeout);

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void OnlyAUsbSessionThatOwnsTheStream_Routes(
        bool isUsbConnection,
        bool preserveActiveStream,
        bool expected)
    {
        // A non-USB connection has nothing to route, and an observe-only session must not steal the
        // stream from the session already receiving it (#385).
        Assert.Equal(
            expected,
            UsbStreamInterfaceInitializer.ShouldRouteStreamToUsb(isUsbConnection, preserveActiveStream));
    }

    [Fact]
    public async Task ANonUsbConnection_SendsNothing()
    {
        var sender = new ScriptedSender();

        await RouteAsync(sender, isUsbConnection: false);

        Assert.Equal(0, sender.AttemptCount);
    }

    [Fact]
    public async Task AnObserveOnlySession_SendsNothing()
    {
        var sender = new ScriptedSender();

        await RouteAsync(sender, preserveActiveStream: true);

        Assert.Equal(0, sender.AttemptCount);
    }

    [Fact]
    public async Task ASkippedRoute_DoesNotObserveCancellation()
    {
        // Deliberate: there is no work to abandon, and the caller re-checks cancellation before it
        // marks the device ready, so a token cancelled here is still honored — just not by throwing
        // out of a step that did nothing.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var sender = new ScriptedSender();

        await RouteAsync(sender, preserveActiveStream: true, cancellationToken: cts.Token);

        Assert.Equal(0, sender.AttemptCount);
    }

    [Fact]
    public async Task ACleanResponse_RoutesOnce()
    {
        var sender = new ScriptedSender().Answers("0");

        await RouteAsync(sender);

        Assert.Equal(1, sender.AttemptCount);
    }

    [Fact]
    public async Task AnEmptyResponse_IsNotTreatedAsAnError()
    {
        // The firmware answers this command with nothing at all on the happy path, so "no lines"
        // must mean success rather than an unclassifiable failure.
        var sender = new ScriptedSender().Answers();

        await RouteAsync(sender);

        Assert.Equal(1, sender.AttemptCount);
    }

    [Fact]
    public async Task ATransientRejection_IsRetriedAndSucceeds()
    {
        // The firmware persists the last-used stream interface across sessions and can reject the
        // command right after connect; that is the case retrying exists for (#310).
        var sender = new ScriptedSender()
            .Answers(ScpiError)
            .Answers("0");

        await RouteAsync(sender);

        Assert.Equal(2, sender.AttemptCount);
    }

    [Fact]
    public async Task APersistentRejection_FailsAfterExhaustingRetries()
    {
        var sender = new ScriptedSender()
            .Answers(ScpiError)
            .Answers(ScpiError);

        var ex = await Assert.ThrowsAsync<ScpiInitializationErrorException>(() => RouteAsync(sender));

        // Literal rather than MaxRetries + 1: deriving it from the constant would make this test
        // agree with any retry budget, including one silently reduced to zero.
        Assert.Equal(2, sender.AttemptCount);
        Assert.Equal(ScpiError, ex.LastScpiError);
        Assert.Equal(new[] { ScpiError }, ex.RawDeviceResponse);
    }

    [Fact]
    public async Task TheReportedFailure_IsTheLastAttemptsResponse()
    {
        // Reporting the first attempt's response would describe an error the device has already
        // moved past — the caller needs the answer that actually ended the routing.
        var sender = new ScriptedSender()
            .Answers("ERROR: -113, \"Undefined header\"")
            .Answers("noise", "  " + ScpiError + "  ");

        var ex = await Assert.ThrowsAsync<ScpiInitializationErrorException>(() => RouteAsync(sender));

        Assert.Equal(ScpiError, ex.LastScpiError);
        Assert.Equal(new[] { "noise", "  " + ScpiError + "  " }, ex.RawDeviceResponse);
    }

    [Fact]
    public async Task CancellationBetweenAttempts_StopsBeforeTheRetry()
    {
        // The settle delay is cancellable, so a token cancelled while waiting must abandon the retry
        // rather than send a command into a session that is being torn down.
        using var cts = new CancellationTokenSource();
        var sender = new ScriptedSender().Answers(ScpiError);
        cts.CancelAfter(TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RouteAsync(sender, cancellationToken: cts.Token));

        Assert.Equal(1, sender.AttemptCount);
    }

    [Fact]
    public async Task TheCallersToken_ReachesEveryAttempt()
    {
        using var cts = new CancellationTokenSource();
        var sender = new ScriptedSender()
            .Answers(ScpiError)
            .Answers("0");

        await RouteAsync(sender, cancellationToken: cts.Token);

        Assert.Equal(new[] { cts.Token, cts.Token }, sender.ObservedTokens);
    }
}
