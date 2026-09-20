using System.Net.Sockets;
using System.Reflection;
using Daqifi.Core.Tests.Communication.Transport;

namespace Daqifi.Core.Tests.TestSupport;

/// <summary>
/// Covers the UDP-broadcast gate that replaced swallowing every
/// <see cref="SocketException"/> except <see cref="SocketError.AccessDenied"/>
/// (PR #749, re-boarded). The skip-message decision is pure, so every arm of it
/// is exercised on every CI leg.
/// </summary>
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

    [Fact]
    public void IsEnvironmental_AccessDenied_IsTheEnableBroadcastBug()
    {
        Assert.False(UdpBroadcastFactAttribute.IsEnvironmental(
            new SocketException((int)SocketError.AccessDenied)));
    }

    [Theory]
    [InlineData(SocketError.NetworkDown)]
    [InlineData(SocketError.NoBufferSpaceAvailable)]
    [InlineData(SocketError.NetworkUnreachable)]
    [InlineData(SocketError.HostUnreachable)]
    public void IsEnvironmental_OtherSocketErrors_AreHostLimitations(SocketError error)
    {
        Assert.True(UdpBroadcastFactAttribute.IsEnvironmental(new SocketException((int)error)));
    }

    [Fact]
    public async Task AwaitSendAsync_Succeeds_Completes()
    {
        var ran = false;
        await UdpBroadcastFactAttribute.AwaitSendAsync(() =>
        {
            ran = true;
            return Task.CompletedTask;
        });
        Assert.True(ran);
    }

    [Fact]
    public async Task AwaitSendAsync_AccessDenied_PropagatesSoEnableBroadcastBugsStillFail()
    {
        var denied = new SocketException((int)SocketError.AccessDenied);

        var thrown = await Assert.ThrowsAsync<SocketException>(
            () => UdpBroadcastFactAttribute.AwaitSendAsync(() => Task.FromException(denied)));

        Assert.Same(denied, thrown);
        Assert.Equal(SocketError.AccessDenied, thrown.SocketErrorCode);
    }

    [Fact]
    public async Task AwaitSendAsync_NetworkDown_ThrowsUnavailableInsteadOfSwallowing()
    {
        var down = new SocketException((int)SocketError.NetworkDown);

        var thrown = await Assert.ThrowsAsync<UdpBroadcastUnavailableException>(
            () => UdpBroadcastFactAttribute.AwaitSendAsync(() => Task.FromException(down)));

        Assert.Same(down, thrown.InnerException);
        Assert.Equal(SocketError.NetworkDown, thrown.SocketErrorCode);
    }

    [Fact]
    public void UnavailableException_CarriesTheSkipReasonAndErrorCode()
    {
        var inner = new SocketException((int)SocketError.NetworkDown);
        var unavailable = new UdpBroadcastUnavailableException(inner);

        Assert.Equal(SocketError.NetworkDown, unavailable.SocketErrorCode);
        Assert.Equal(
            UdpBroadcastFactAttribute.SkipReason(
                UdpBroadcastFactAttribute.BecauseBroadcastIsUnavailable,
                inner),
            unavailable.Message);
        Assert.Same(inner, unavailable.InnerException);
    }

    [Theory]
    [InlineData(nameof(UdpTransportTests.SendBroadcastAsync_ShouldSendData))]
    [InlineData(nameof(UdpTransportTests.SendBroadcastAsync_WithEndpoint_ShouldSendData))]
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
