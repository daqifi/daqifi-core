using Daqifi.Core.Communication.Transport;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Daqifi.Core.Tests.Communication.Transport;

public class UdpTransportTests
{
    [Fact]
    public void Constructor_ShouldCreateInstance()
    {
        // Act
        using var transport = new UdpTransport();

        // Assert
        Assert.False(transport.IsOpen);
    }

    [Fact]
    public void Constructor_WithPort_ShouldCreateInstance()
    {
        // Act
        using var transport = new UdpTransport(30303);

        // Assert
        Assert.False(transport.IsOpen);
    }

    [Fact]
    public async Task OpenAsync_ShouldOpenTransport()
    {
        // Arrange
        using var transport = new UdpTransport(0); // Use any available port
        var statusChanged = false;
        transport.StatusChanged += (sender, args) =>
        {
            if (args.IsConnected) statusChanged = true;
        };

        // Act
        await transport.OpenAsync();

        // Assert
        Assert.True(transport.IsOpen);
        Assert.True(statusChanged);
    }

    [Fact]
    public async Task CloseAsync_ShouldCloseTransport()
    {
        // Arrange
        using var transport = new UdpTransport(0);
        await transport.OpenAsync();
        var closedStatusChanged = false;
        transport.StatusChanged += (sender, args) =>
        {
            if (!args.IsConnected) closedStatusChanged = true;
        };

        // Act
        await transport.CloseAsync();

        // Assert
        Assert.False(transport.IsOpen);
        Assert.True(closedStatusChanged);
    }

    // Sandboxes, missing routes, and send-buffer exhaustion are not UdpTransport defects;
    // swallowing the SocketException used to report passed with zero asserts. The gate
    // records Skipped instead. AccessDenied from UdpTransport itself still fails — that
    // is EnableBroadcast not being set, which is the bug these two exist to catch.
    [UdpBroadcastFact(UdpBroadcastFactAttribute.BecauseBroadcastIsUnavailable)]
    public async Task SendBroadcastAsync_ShouldSendData()
    {
        using var transport = new UdpTransport(0);
        await transport.OpenAsync();
        var testData = Encoding.ASCII.GetBytes("DAQiFi?\r\n");

        await transport.SendBroadcastAsync(testData, 30303);
    }

    [UdpBroadcastFact(UdpBroadcastFactAttribute.BecauseBroadcastIsUnavailable)]
    public async Task SendBroadcastAsync_WithEndpoint_ShouldSendData()
    {
        using var transport = new UdpTransport(0);
        await transport.OpenAsync();
        var testData = Encoding.ASCII.GetBytes("DAQiFi?\r\n");
        var endPoint = new IPEndPoint(IPAddress.Broadcast, 30303);

        await transport.SendBroadcastAsync(testData, endPoint);
    }

    [Fact]
    public async Task SendUnicastAsync_ShouldSendData()
    {
        // Arrange
        using var transport = new UdpTransport(0);
        await transport.OpenAsync();
        var testData = Encoding.ASCII.GetBytes("Test");
        var endpoint = new IPEndPoint(IPAddress.Loopback, 12345);

        // Act & Assert (should not throw)
        await transport.SendUnicastAsync(testData, endpoint);
    }

    [Fact]
    public async Task ReceiveAsync_WithTimeout_ShouldReturnNullOnTimeout()
    {
        // Arrange
        using var transport = new UdpTransport(0);
        await transport.OpenAsync();

        // Act
        var result = await transport.ReceiveAsync(TimeSpan.FromMilliseconds(100));

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task SendAndReceive_Loopback_ShouldWork()
    {
        // Arrange - use dynamic port allocation to avoid conflicts in parallel test runs
        using var receiver = new UdpTransport(0);
        using var sender = new UdpTransport(0);

        await receiver.OpenAsync();
        await sender.OpenAsync();

        // Get the dynamically assigned port after opening
        var receiverPort = receiver.LocalPort;

        var testData = Encoding.ASCII.GetBytes("Hello UDP!");
        var endpoint = new IPEndPoint(IPAddress.Loopback, receiverPort);

        // Act
        await sender.SendUnicastAsync(testData, endpoint);
        var result = await receiver.ReceiveAsync(TimeSpan.FromSeconds(2));

        // Assert
        Assert.NotNull(result);
        Assert.Equal(testData, result.Value.Data);
        Assert.Equal(IPAddress.Loopback, result.Value.RemoteEndPoint.Address);
    }

    [Fact]
    public async Task OpenAsync_WhenAlreadyOpen_ShouldNotThrow()
    {
        // Arrange
        using var transport = new UdpTransport(0);
        await transport.OpenAsync();

        // Act & Assert
        await transport.OpenAsync(); // Should not throw
        Assert.True(transport.IsOpen);
    }

    [Fact]
    public async Task SendBroadcastAsync_WhenNotOpen_ShouldThrow()
    {
        // Arrange
        using var transport = new UdpTransport(0);
        var testData = Encoding.ASCII.GetBytes("Test");

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await transport.SendBroadcastAsync(testData, 30303));
    }

    [Fact]
    public async Task Dispose_ShouldCloseTransport()
    {
        // Arrange
        var transport = new UdpTransport(0);
        await transport.OpenAsync();

        // Act
        transport.Dispose();

        // Assert
        Assert.False(transport.IsOpen);
    }

    [Fact]
    public async Task ConnectionInfo_ShouldReflectStatus()
    {
        // Arrange - use dynamic port to avoid conflicts in parallel test runs
        using var transport = new UdpTransport(0);

        // Act & Assert
        Assert.Contains("Closed", transport.ConnectionInfo);

        await transport.OpenAsync();
        Assert.Contains("Open", transport.ConnectionInfo);
    }

    [Theory]
    [InlineData(nameof(SendBroadcastAsync_ShouldSendData))]
    [InlineData(nameof(SendBroadcastAsync_WithEndpoint_ShouldSendData))]
    public void BroadcastSendTests_CarryUdpBroadcastFact_SoTheyCannotSilentlyPassAgain(string testMethod)
    {
        // Pins the fix: these two used to catch every SocketException except AccessDenied
        // and fall off the end, reporting passed while asserting nothing.
        var method = typeof(UdpTransportTests).GetMethod(testMethod);
        Assert.NotNull(method);

        var gate = method.GetCustomAttribute<UdpBroadcastFactAttribute>();
        Assert.NotNull(gate);
    }
}

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
/// #663). xunit 2.9.3 has no dynamic skip, so the capability decision is made here at
/// discovery, the same way <c>PlatformFactAttribute</c> decides the platform.
/// </para>
/// <para>
/// <see cref="SocketError.AccessDenied"/> from <c>UdpTransport</c> itself still fails the
/// test: that is the <c>EnableBroadcast</c> bug the gated tests exist to catch. The probe
/// sets <c>EnableBroadcast</c> on its own socket, so AccessDenied from the probe is an
/// environment limitation and skips.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class UdpBroadcastFactAttribute : FactAttribute
{
    /// <summary>
    /// Why the two <c>SendBroadcastAsync</c> tests cannot be observed when the probe
    /// cannot send. Surfaced as the skip reason so a reader of the results learns that
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

public class UdpBroadcastFactAttributeTests
{
    [Fact]
    public void SkipReason_ProbeSucceeded_IsNullSoTheTestRuns()
    {
        Assert.Null(UdpBroadcastFactAttribute.SkipReason("because reasons", probeFailure: null));
    }

    [Fact]
    public void SkipReason_SocketException_AppendsTheErrorCode()
    {
        var failure = new SocketException((int)SocketError.NetworkDown);

        Assert.Equal(
            "because reasons (NetworkDown)",
            UdpBroadcastFactAttribute.SkipReason("because reasons", failure));
    }

    [Fact]
    public void SkipReason_NonSocketException_AppendsTheTypeAndMessage()
    {
        var failure = new InvalidOperationException("no datagrams");

        Assert.Equal(
            "because reasons (InvalidOperationException: no datagrams)",
            UdpBroadcastFactAttribute.SkipReason("because reasons", failure));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_ReasonIsMissing_Throws(string? because)
    {
        Assert.ThrowsAny<ArgumentException>(() => new UdpBroadcastFactAttribute(because!));
    }

    [Fact]
    public void Skip_OnThisHost_AgreesWithTheProbe()
    {
        var attribute = new UdpBroadcastFactAttribute("because reasons");

        Assert.Equal(
            UdpBroadcastFactAttribute.SkipReason("because reasons", UdpBroadcastFactAttribute.ProbeFailure),
            attribute.Skip);
    }
}
