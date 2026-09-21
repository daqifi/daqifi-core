using System.Net;
using System.Net.Sockets;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Daqifi.Core.Tests.TestSupport;

/// <summary>
/// A <see cref="FactAttribute"/> that reports the test as <em>skipped</em> when this host
/// cannot send a UDP broadcast, rather than letting it catch the socket error and assert
/// nothing.
/// </summary>
/// <remarks>
/// <para>
/// <c>SendBroadcastAsync</c> talks to the real network. Sandboxes, missing routes, and
/// parallel-load buffer exhaustion raise <see cref="SocketException"/> that is not a
/// <c>UdpTransport</c> defect. Swallowing those used to report <em>passed</em> with zero
/// asserts — the same silent-pass class as the off-platform bare <c>return</c>s (issue
/// #663). xunit 2.9.3 has no dynamic skip, so the persistent-host decision is made here
/// at discovery, the same way <see cref="PlatformFactAttribute"/> decides the platform.
/// </para>
/// <para>
/// Discovery is not enough on its own. A probe that runs while the machine is idle will
/// almost always succeed, and then the real send runs under parallel test load — which is
/// how <c>NoBufferSpaceAvailable</c> used to flake these two tests (#140) and why the
/// first skip gate (#749) was closed. The companion test case therefore turns an
/// environmental <see cref="SocketException"/> from the send itself into a skip as well.
/// <see cref="SocketError.AccessDenied"/> from <c>UdpTransport</c> still fails: that is
/// <c>EnableBroadcast</c> not being set, which is the bug these tests exist to catch.
/// AccessDenied from the probe (which sets <c>EnableBroadcast</c> on its own socket) is
/// an environment limitation and skips.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
[XunitTestCaseDiscoverer(
    "Daqifi.Core.Tests.TestSupport.UdpBroadcastFactDiscoverer",
    "Daqifi.Core.Tests")]
public sealed class UdpBroadcastFactAttribute : FactAttribute
{
    /// <summary>
    /// Why the two <c>SendBroadcastAsync</c> tests cannot be observed when broadcast is
    /// unavailable. Surfaced as the skip reason so a reader of the results learns that
    /// the run did not silently pass.
    /// </summary>
    public const string BecauseBroadcastIsUnavailable =
        "UDP broadcast is not available on this host (sandbox, no route, or send-buffer " +
        "exhaustion); that is an OS/environment limitation, not a UdpTransport defect.";

    /// <summary>
    /// Initializes a new instance of the <see cref="UdpBroadcastFactAttribute"/> class.
    /// </summary>
    /// <param name="because">
    /// Why the test cannot be observed when broadcast is unavailable. Required, and
    /// surfaced as the skip reason (with the probe's socket error appended).
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="because"/> is null, empty, or white space.
    /// </exception>
    public UdpBroadcastFactAttribute(string because)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(because);

        Skip = SkipReason(because, ProbeFailure);
    }

    /// <summary>
    /// Gets the exception the discovery-time probe caught, or <c>null</c> when this host
    /// can send a UDP broadcast. Split out so tests can assert the skip message against
    /// a supplied failure without opening a second socket.
    /// </summary>
    internal static Exception? ProbeFailure { get; } = TryProbe();

    /// <summary>
    /// Decides the skip message for a probe outcome.
    /// </summary>
    internal static string? SkipReason(string because, Exception? probeFailure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(because);

        if (probeFailure is null)
        {
            return null;
        }

        if (probeFailure is SocketException socket)
        {
            return $"{because} ({socket.SocketErrorCode})";
        }

        return $"{because} ({probeFailure.GetType().Name}: {probeFailure.Message})";
    }

    /// <summary>
    /// Whether a <see cref="SocketException"/> from a broadcast send is an environment
    /// limitation rather than the <c>EnableBroadcast</c> bug these tests exist to catch.
    /// </summary>
    internal static bool IsEnvironmental(SocketException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.SocketErrorCode != SocketError.AccessDenied;
    }

    /// <summary>
    /// Completes <paramref name="send"/>, or throws <see cref="UdpBroadcastUnavailableException"/>
    /// so the test case records Skipped. <see cref="SocketError.AccessDenied"/> still
    /// propagates — that is EnableBroadcast not being set.
    /// </summary>
    internal static async Task AwaitSendAsync(Func<Task> send)
    {
        ArgumentNullException.ThrowIfNull(send);

        try
        {
            await send();
        }
        catch (SocketException ex) when (IsEnvironmental(ex))
        {
            throw new UdpBroadcastUnavailableException(ex);
        }
    }

    private static Exception? TryProbe()
    {
        try
        {
            using var client = new UdpClient(0);
            client.EnableBroadcast = true;
            // Port 9 is discard — a one-byte payload, not the DAQiFi discovery query.
            _ = client.Send([0x00], new IPEndPoint(IPAddress.Broadcast, 9));
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}

/// <summary>
/// Thrown from a <see cref="UdpBroadcastFactAttribute"/> test when the send failed for an
/// environmental reason. The test case reports Skipped instead of Failed or Passed.
/// </summary>
internal sealed class UdpBroadcastUnavailableException : Exception
{
    public UdpBroadcastUnavailableException(SocketException inner)
        : base(
            UdpBroadcastFactAttribute.SkipReason(
                UdpBroadcastFactAttribute.BecauseBroadcastIsUnavailable,
                inner),
            inner)
    {
        SocketErrorCode = inner.SocketErrorCode;
    }

    public SocketError SocketErrorCode { get; }
}

/// <summary>
/// Discovers <see cref="UdpBroadcastFactAttribute"/> methods as test cases that can skip
/// at execution when the send hits an environmental socket error.
/// </summary>
public sealed class UdpBroadcastFactDiscoverer : FactDiscoverer
{
    public UdpBroadcastFactDiscoverer(IMessageSink diagnosticMessageSink)
        : base(diagnosticMessageSink)
    {
    }

    protected override IXunitTestCase CreateTestCase(
        ITestFrameworkDiscoveryOptions discoveryOptions,
        ITestMethod testMethod,
        IAttributeInfo factAttribute)
        => new UdpBroadcastFactTestCase(
            DiagnosticMessageSink,
            discoveryOptions.MethodDisplayOrDefault(),
            discoveryOptions.MethodDisplayOptionsOrDefault(),
            testMethod);
}

/// <summary>
/// An <see cref="XunitTestCase"/> whose message bus turns
/// <see cref="UdpBroadcastUnavailableException"/> into a skip. Discovery-time
/// <see cref="FactAttribute.Skip"/> still applies first, for hosts that cannot broadcast
/// at all.
/// </summary>
public sealed class UdpBroadcastFactTestCase : XunitTestCase
{
    [Obsolete("Called by the de-serializer; should only be called by deriving classes for de-serialization purposes")]
    public UdpBroadcastFactTestCase()
    {
    }

    public UdpBroadcastFactTestCase(
        IMessageSink diagnosticMessageSink,
        TestMethodDisplay defaultMethodDisplay,
        TestMethodDisplayOptions defaultMethodDisplayOptions,
        ITestMethod testMethod)
        : base(diagnosticMessageSink, defaultMethodDisplay, defaultMethodDisplayOptions, testMethod)
    {
    }

    public override async Task<RunSummary> RunAsync(
        IMessageSink diagnosticMessageSink,
        IMessageBus messageBus,
        object[] constructorArguments,
        ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource)
    {
        var bus = new UdpBroadcastSkipMessageBus(messageBus);
        var summary = await base.RunAsync(
            diagnosticMessageSink,
            bus,
            constructorArguments,
            aggregator,
            cancellationTokenSource);

        if (bus.SkippedCount > 0)
        {
            summary.Failed -= bus.SkippedCount;
            summary.Skipped += bus.SkippedCount;
            if (summary.Failed < 0)
            {
                summary.Failed = 0;
            }
        }

        return summary;
    }
}

/// <summary>
/// Replaces a failed result caused by <see cref="UdpBroadcastUnavailableException"/> with
/// a skip, so parallel-load socket errors are not silent passes and not hard failures.
/// </summary>
internal sealed class UdpBroadcastSkipMessageBus : IMessageBus
{
    private static readonly string UnavailableTypeName =
        typeof(UdpBroadcastUnavailableException).FullName!;

    private readonly IMessageBus _inner;

    public UdpBroadcastSkipMessageBus(IMessageBus inner)
    {
        _inner = inner;
    }

    public int SkippedCount { get; private set; }

    public void Dispose()
    {
        // The runner owns the inner bus.
    }

    public bool QueueMessage(IMessageSinkMessage message)
    {
        if (message is ITestFailed failed && IsUnavailable(failed))
        {
            SkippedCount++;
            var reason = failed.Messages is { Length: > 0 } messages
                ? messages[0]
                : UdpBroadcastFactAttribute.BecauseBroadcastIsUnavailable;
            return _inner.QueueMessage(new TestSkipped(failed.Test, reason));
        }

        return _inner.QueueMessage(message);
    }

    private static bool IsUnavailable(ITestFailed failed)
        => failed.ExceptionTypes is { Length: > 0 } types
           && types.Contains(UnavailableTypeName, StringComparer.Ordinal);
}
