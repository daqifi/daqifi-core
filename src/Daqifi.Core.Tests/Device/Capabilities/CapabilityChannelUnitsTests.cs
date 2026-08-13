using System;
using System.Collections.Generic;
using Daqifi.Core.Channel;
using Daqifi.Core.Device.Capabilities;

namespace Daqifi.Core.Tests.Device.Capabilities;

/// <summary>
/// Unit tests for <see cref="CapabilityChannelUnits"/> — the consumer that finally reads the
/// capability document's per-channel <c>unit</c> and puts it on the channel (#501).
/// </summary>
/// <remarks>
/// Applying it through the device is covered by <c>DaqifiDeviceCapabilityDocumentTests</c>; what
/// these add is the matching rules, which are only visible here: the kind+id join, and the refusal
/// to overwrite a conversion the caller configured.
/// </remarks>
public class CapabilityChannelUnitsTests
{
    [Fact]
    public void AppliesTheDocumentsUnitAsAnIdentityScaling()
    {
        var ai0 = new AnalogChannel(0);

        var applied = CapabilityChannelUnits.Apply(
            new IChannel[] { ai0 },
            Document(AnalogInput(0, "V")));

        Assert.Equal(1, applied);
        Assert.Equal("V", ai0.Scaling!.Unit);

        // An identity scaling: a label, not a transform. Readings must be untouched.
        Assert.True(ai0.Scaling.IsIdentity);
        Assert.Equal(2.5, ai0.Scaling.Apply(2.5));
    }

    [Fact]
    public void MatchesOnKindAsWellAsId()
    {
        // The document numbers analog inputs and digital pins from 0 independently, so an id alone
        // is ambiguous. A digital-io entry must never hand its unit to the analog channel that
        // happens to share its number.
        var ai0 = new AnalogChannel(0);

        var applied = CapabilityChannelUnits.Apply(
            new IChannel[] { ai0 },
            Document(new CapabilityChannel { Id = 0, Kind = CapabilityChannelKind.DigitalIo, Unit = "bogus" }));

        Assert.Equal(0, applied);
        Assert.Null(ai0.Scaling);
    }

    [Fact]
    public void MatchesEachChannelToItsOwnEntry()
    {
        var ai0 = new AnalogChannel(0);
        var ai1 = new AnalogChannel(1);

        CapabilityChannelUnits.Apply(
            new IChannel[] { ai1, ai0 }, // deliberately out of order
            Document(AnalogInput(0, "V"), AnalogInput(1, "Cel")));

        Assert.Equal("V", ai0.Scaling!.Unit);
        Assert.Equal("Cel", ai1.Scaling!.Unit);
    }

    [Fact]
    public void NeverOverwritesAScalingTheCallerConfigured()
    {
        // This runs again on every capability refresh — the MCP layer re-reads the document after
        // each channel-configuration call — so clobbering here would quietly undo a transducer
        // conversion minutes after it was set.
        var configured = new ChannelScaling(gain: 20.0, unit: "PSI");
        var ai0 = new AnalogChannel(0) { Scaling = configured };

        var applied = CapabilityChannelUnits.Apply(
            new IChannel[] { ai0 },
            Document(AnalogInput(0, "V")));

        Assert.Equal(0, applied);
        Assert.Same(configured, ai0.Scaling);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ADocumentStatingNoUnit_LeavesTheChannelAlone(string? unit)
    {
        // "Not stated" must not become a scaling with a null unit — that would then block the next
        // refresh, which reads a null Scaling as "nothing has claimed this channel yet".
        var ai0 = new AnalogChannel(0);

        var applied = CapabilityChannelUnits.Apply(new IChannel[] { ai0 }, Document(AnalogInput(0, unit)));

        Assert.Equal(0, applied);
        Assert.Null(ai0.Scaling);
    }

    [Fact]
    public void AChannelTheDocumentDoesNotDescribe_IsLeftAlone()
    {
        var ai3 = new AnalogChannel(3);

        Assert.Equal(0, CapabilityChannelUnits.Apply(new IChannel[] { ai3 }, Document(AnalogInput(0, "V"))));
        Assert.Null(ai3.Scaling);
    }

    [Fact]
    public void DigitalChannelsAreSkipped()
    {
        var dio0 = new DigitalChannel(0);

        Assert.Equal(0, CapabilityChannelUnits.Apply(new IChannel[] { dio0 }, Document(AnalogInput(0, "V"))));
    }

    [Fact]
    public void NullOrEmptyInputs_AreNoOps()
    {
        Assert.Equal(0, CapabilityChannelUnits.Apply(null, Document(AnalogInput(0, "V"))));
        Assert.Equal(0, CapabilityChannelUnits.Apply(new IChannel[] { new AnalogChannel(0) }, null));
        Assert.Equal(0, CapabilityChannelUnits.Apply(Array.Empty<IChannel>(), Document(AnalogInput(0, "V"))));
    }

    private static CapabilityDocument Document(params CapabilityChannel[] channels)
        => new() { SchemaVersion = 2, Channels = new List<CapabilityChannel>(channels) };

    private static CapabilityChannel AnalogInput(int id, string? unit)
        => new() { Id = id, Kind = CapabilityChannelKind.AnalogInput, Unit = unit };
}
