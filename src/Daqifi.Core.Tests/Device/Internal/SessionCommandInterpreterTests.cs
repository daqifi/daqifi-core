using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device.Internal;
using Xunit;

namespace Daqifi.Core.Tests.Device.Internal;

/// <summary>
/// Unit tests for <see cref="SessionCommandInterpreter"/>, which reads a command that has already
/// been sent and decides what it means for the streaming session (issue #379). These pin the
/// decision itself; <c>DeviceReconnectTests</c> pins the effects the device applies from it.
/// </summary>
public class SessionCommandInterpreterTests
{
    private const int MaxSamplingRate = 1000;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankCommand_MeansNothing(string? command)
    {
        var effect = SessionCommandInterpreter.Interpret(command, MaxSamplingRate);

        Assert.Equal(SessionCommandEffectKind.None, effect.Kind);
    }

    [Theory]
    [InlineData("SYSTem:REboot")]
    [InlineData("DIO:PORt:ENAble 1")]
    [InlineData("SYSTem:SYSInfoPB?")]
    public void ACommandThatSaysNothingAboutTheSession_PassesThrough(string command)
    {
        // The global DIO enable is deliberately in this group: it is one switch for the whole port
        // rather than a per-channel mask, so it carries no information about which digital channels
        // were wanted and none is inferred.
        var effect = SessionCommandInterpreter.Interpret(command, MaxSamplingRate);

        Assert.Equal(SessionCommandEffectKind.None, effect.Kind);
    }

    [Fact]
    public void AStopCommand_EndsTheSession()
    {
        var effect = SessionCommandInterpreter.Interpret("SYSTem:StopStreamData", MaxSamplingRate);

        Assert.Equal(SessionCommandEffectKind.StopStreaming, effect.Kind);
    }

    [Fact]
    public void TheProducersOwnCommands_AreTheOnesRecognized()
    {
        // The constants are duplicated from the producer's output rather than derived from it, so
        // this is the guard that keeps the two from drifting apart silently.
        var start = SessionCommandInterpreter.Interpret(
            ScpiMessageProducer.StartStreaming(250).Data, MaxSamplingRate);
        var stop = SessionCommandInterpreter.Interpret(
            ScpiMessageProducer.StopStreaming.Data, MaxSamplingRate);
        var enable = SessionCommandInterpreter.Interpret(
            ScpiMessageProducer.EnableAdcChannels("10").Data, MaxSamplingRate);

        Assert.Equal(SessionCommandEffectKind.StartStreaming, start.Kind);
        Assert.Equal(250, start.StreamingFrequency);
        Assert.Equal(SessionCommandEffectKind.StopStreaming, stop.Kind);
        Assert.Equal(SessionCommandEffectKind.SetAdcEnableMask, enable.Kind);
        Assert.Equal(10u, enable.AdcEnableMask);
    }

    [Theory]
    [InlineData("systEM:startstreamdata 100")]
    [InlineData("SYSTEM:STARTSTREAMDATA 100")]
    public void CommandMatchingIsCaseInsensitive(string command)
    {
        // SCPI's short/long forms differ only in case, so a caller writing the long form must be
        // recognized exactly like the producer's mixed-case output.
        var effect = SessionCommandInterpreter.Interpret(command, MaxSamplingRate);

        Assert.Equal(SessionCommandEffectKind.StartStreaming, effect.Kind);
        Assert.Equal(100, effect.StreamingFrequency);
    }

    [Fact]
    public void SurroundingWhitespace_DoesNotHideACommand()
    {
        var effect = SessionCommandInterpreter.Interpret("  SYSTem:StartStreamData 100  ", MaxSamplingRate);

        Assert.Equal(SessionCommandEffectKind.StartStreaming, effect.Kind);
        Assert.Equal(100, effect.StreamingFrequency);
    }

    [Theory]
    [InlineData("SYSTem:StartStreamData")]           // no argument at all
    [InlineData("SYSTem:StartStreamData ")]          // argument present but empty
    [InlineData("SYSTem:StartStreamData abc")]       // not a number
    [InlineData("SYSTem:StartStreamData 100 extra")] // trailing junk
    [InlineData("SYSTem:StartStreamData 0")]         // below the usable range
    [InlineData("SYSTem:StartStreamData -5")]        // negative
    [InlineData("SYSTem:StartStreamData 99999999")]  // beyond the device's sampling rate
    public void AStartCommandWithAnUnusableRate_IsNotAStart(string command)
    {
        // Reported as its own kind rather than as None so the caller can trace the rejection, but
        // carrying no rate: marking a session started here would leave the streaming frequency
        // holding a rate from an earlier session, which a reconnect would then faithfully restore.
        var effect = SessionCommandInterpreter.Interpret(command, MaxSamplingRate);

        Assert.Equal(SessionCommandEffectKind.UnusableStreamingStart, effect.Kind);
        Assert.Equal(0, effect.StreamingFrequency);
    }

    [Fact]
    public void ARejectedRate_IsReportedAsItWasWritten()
    {
        var effect = SessionCommandInterpreter.Interpret("SYSTem:StartStreamData  not-a-rate ", MaxSamplingRate);

        Assert.Equal("not-a-rate", effect.RejectedRate);
    }

    [Fact]
    public void TheCeilingIsInclusive()
    {
        var atCeiling = SessionCommandInterpreter.Interpret("SYSTem:StartStreamData 1000", MaxSamplingRate);
        var pastCeiling = SessionCommandInterpreter.Interpret("SYSTem:StartStreamData 1001", MaxSamplingRate);

        Assert.Equal(SessionCommandEffectKind.StartStreaming, atCeiling.Kind);
        Assert.Equal(1000, atCeiling.StreamingFrequency);
        Assert.Equal(SessionCommandEffectKind.UnusableStreamingStart, pastCeiling.Kind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AnUnusableCeiling_FallsBackToOneRatherThanRejectingEverything(int maxSamplingRate)
    {
        // MaxSamplingRate is a mutable, unvalidated public property. An uninitialized value must not
        // produce an impossible range like "1..0" that turns every rate into a rejection.
        var atFloor = SessionCommandInterpreter.Interpret("SYSTem:StartStreamData 1", maxSamplingRate);
        var aboveFloor = SessionCommandInterpreter.Interpret("SYSTem:StartStreamData 2", maxSamplingRate);

        Assert.Equal(SessionCommandEffectKind.StartStreaming, atFloor.Kind);
        Assert.Equal(1, atFloor.StreamingFrequency);
        Assert.Equal(SessionCommandEffectKind.UnusableStreamingStart, aboveFloor.Kind);
    }

    [Fact]
    public void StopIsNotMistakenForStart()
    {
        // Both commands share the "SYSTem:St" prefix, so the order the two are tested in is load
        // bearing rather than incidental.
        var effect = SessionCommandInterpreter.Interpret("SYSTem:StopStreamData", MaxSamplingRate);

        Assert.Equal(SessionCommandEffectKind.StopStreaming, effect.Kind);
    }

    [Theory]
    [InlineData("ENAble:VOLTage:DC 0", 0u)]
    [InlineData("ENAble:VOLTage:DC 5", 5u)]
    [InlineData("ENAble:VOLTage:DC  65535 ", 65535u)]
    [InlineData("ENAble:VOLTage:DC 4294967295", uint.MaxValue)]
    public void AnAdcEnableCommand_CarriesItsMask(string command, uint expected)
    {
        var effect = SessionCommandInterpreter.Interpret(command, MaxSamplingRate);

        Assert.Equal(SessionCommandEffectKind.SetAdcEnableMask, effect.Kind);
        Assert.Equal(expected, effect.AdcEnableMask);
    }

    [Theory]
    [InlineData("ENAble:VOLTage:DC")]           // no argument
    [InlineData("ENAble:VOLTage:DC abc")]       // not a number
    [InlineData("ENAble:VOLTage:DC -1")]        // negative
    [InlineData("ENAble:VOLTage:DC 4294967296")] // past uint
    public void AnUnparseableMask_ChangesNothing(string command)
    {
        // A mask that cannot be read is not evidence that no channels are enabled — clearing the
        // set from it would be inventing a state the device never reported.
        var effect = SessionCommandInterpreter.Interpret(command, MaxSamplingRate);

        Assert.Equal(SessionCommandEffectKind.None, effect.Kind);
    }
}
