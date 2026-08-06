using System;
using System.Collections.Generic;
using System.Linq;
using Daqifi.Core.Channel;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Internal;
using Xunit;

namespace Daqifi.Core.Tests.Device.Internal;

/// <summary>
/// Unit tests for <see cref="StreamingSessionSnapshot"/>, which records what a streaming session
/// looked like at a drop and decides what putting it back implies (issue #379). These pin the
/// decision itself; <c>DeviceReconnectTests</c> pins the effects the device applies from it.
/// </summary>
public class StreamingSessionSnapshotTests
{
    private static AnalogChannel Analog(int number, bool enabled)
    {
        var channel = new AnalogChannel(number);
        channel.IsEnabled = enabled;
        return channel;
    }

    private static DigitalChannel Digital(int number, bool enabled)
    {
        var channel = new DigitalChannel(number);
        channel.IsEnabled = enabled;
        return channel;
    }

    private static ReconnectOptions Policy(bool resumeStreaming = true) =>
        new() { Enabled = true, ResumeStreaming = resumeStreaming };

    [Fact]
    public void Capture_RecordsOnlyTheEnabledChannels()
    {
        var channels = new IChannel[]
        {
            Analog(0, enabled: true),
            Analog(1, enabled: false),
            Digital(2, enabled: true),
        };

        var snapshot = StreamingSessionSnapshot.Capture(channels, isStreaming: true);

        Assert.Equal(2, snapshot.EnabledChannelCount);
        Assert.True(snapshot.WasStreaming);
    }

    [Fact]
    public void Capture_TakesACopy_SoLaterChannelMutationCannotRewriteTheSession()
    {
        // This is the whole reason a snapshot exists: re-initialization after a reconnect goes on
        // mutating these same channel objects (analog IsEnabled is resynced from the device's own
        // reported mask on every status frame, #409), so a snapshot that held them by reference
        // would describe the reconnected device rather than the session that was lost.
        var enabled = Analog(0, enabled: true);
        var disabled = Analog(1, enabled: false);

        var snapshot = StreamingSessionSnapshot.Capture(new IChannel[] { enabled, disabled }, isStreaming: true);

        enabled.IsEnabled = false;
        disabled.IsEnabled = true;

        var plan = snapshot.PlanRestore(new IChannel[] { enabled, disabled }, Policy());

        Assert.Equal(new[] { 0 }, plan.ChannelsToEnable.Select(c => c.ChannelNumber));
    }

    [Fact]
    public void Capture_WithNoChannelsEnabled_PlansNothingToEnable()
    {
        var snapshot = StreamingSessionSnapshot.Capture(
            new IChannel[] { Analog(0, enabled: false) }, isStreaming: false);

        var plan = snapshot.PlanRestore(new IChannel[] { Analog(0, enabled: false) }, Policy());

        Assert.Equal(0, snapshot.EnabledChannelCount);
        Assert.Empty(plan.ChannelsToEnable);
        Assert.False(plan.ResumeStreaming);
    }

    [Fact]
    public void PlanRestore_ReturnsTheReconnectedDevicesOwnChannelObjects_NotTheCapturedOnes()
    {
        // A reconnect can replace the channel objects wholesale, so matching has to be by identity
        // (type + number) and the plan has to hand back the objects the device has now — enabling
        // the old ones would set a flag on garbage.
        var beforeDrop = Analog(3, enabled: true);
        var snapshot = StreamingSessionSnapshot.Capture(new IChannel[] { beforeDrop }, isStreaming: true);

        var afterReconnect = Analog(3, enabled: false);
        var plan = snapshot.PlanRestore(new IChannel[] { afterReconnect }, Policy());

        Assert.Same(afterReconnect, Assert.Single(plan.ChannelsToEnable));
    }

    [Fact]
    public void PlanRestore_MatchesOnChannelTypeAsWellAsNumber()
    {
        // Analog 0 and digital 0 share a number and are different channels; a number-only match
        // would enable the wrong one.
        var snapshot = StreamingSessionSnapshot.Capture(
            new IChannel[] { Analog(0, enabled: true), Digital(0, enabled: false) }, isStreaming: true);

        var plan = snapshot.PlanRestore(
            new IChannel[] { Analog(0, enabled: false), Digital(0, enabled: false) }, Policy());

        var restored = Assert.Single(plan.ChannelsToEnable);
        Assert.Equal(ChannelType.Analog, restored.Type);
    }

    [Fact]
    public void PlanRestore_WhenTheDeviceComesBackSmaller_RestoresTheIntersection()
    {
        var snapshot = StreamingSessionSnapshot.Capture(
            new IChannel[] { Analog(0, enabled: true), Analog(1, enabled: true) }, isStreaming: true);

        var plan = snapshot.PlanRestore(new IChannel[] { Analog(0, enabled: false) }, Policy());

        Assert.Equal(new[] { 0 }, plan.ChannelsToEnable.Select(c => c.ChannelNumber));
    }

    [Fact]
    public void PlanRestore_IgnoresChannelsTheSessionNeverHad()
    {
        var snapshot = StreamingSessionSnapshot.Capture(
            new IChannel[] { Analog(0, enabled: true) }, isStreaming: false);

        var plan = snapshot.PlanRestore(
            new IChannel[] { Analog(0, enabled: false), Analog(7, enabled: true) }, Policy());

        Assert.Equal(new[] { 0 }, plan.ChannelsToEnable.Select(c => c.ChannelNumber));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void PlanRestore_ResumesOnlyWhenTheSessionWasStreamingAndThePolicyAllowsIt(
        bool wasStreaming, bool resumeStreaming, bool expected)
    {
        var snapshot = StreamingSessionSnapshot.Capture(
            new IChannel[] { Analog(0, enabled: true) }, wasStreaming);

        var plan = snapshot.PlanRestore(
            new IChannel[] { Analog(0, enabled: false) }, Policy(resumeStreaming));

        Assert.Equal(expected, plan.ResumeStreaming);
    }

    [Fact]
    public void PlanRestore_CanBeCalledMoreThanOnce_WithoutConsumingTheSnapshot()
    {
        // The device keeps one snapshot field across reconnect attempts, so a failed attempt must
        // not leave a snapshot that has already given up its contents.
        var snapshot = StreamingSessionSnapshot.Capture(
            new IChannel[] { Analog(0, enabled: true) }, isStreaming: true);

        var first = snapshot.PlanRestore(new IChannel[] { Analog(0, enabled: false) }, Policy());
        var second = snapshot.PlanRestore(new IChannel[] { Analog(0, enabled: false) }, Policy());

        Assert.Single(first.ChannelsToEnable);
        Assert.Single(second.ChannelsToEnable);
        Assert.True(second.ResumeStreaming);
    }

    [Fact]
    public void Capture_WithNullChannels_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => StreamingSessionSnapshot.Capture(null!, isStreaming: false));
    }

    [Fact]
    public void PlanRestore_WithNullArguments_Throws()
    {
        var snapshot = StreamingSessionSnapshot.Capture(Array.Empty<IChannel>(), isStreaming: false);

        Assert.Throws<ArgumentNullException>(() => snapshot.PlanRestore(null!, Policy()));
        Assert.Throws<ArgumentNullException>(
            () => snapshot.PlanRestore(new List<IChannel>(), null!));
    }
}
