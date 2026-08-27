using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device.Internal;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Daqifi.Core.Tests.Device.Internal;

/// <summary>
/// Unit tests for <see cref="StatusChannelPopulator"/>, the status-frame-to-channel mapping
/// extracted from <c>DaqifiDevice</c>.
/// </summary>
/// <remarks>
/// The mapping's behaviour through the device is already covered by
/// <c>ChannelPopulationTests</c>; these exercise the collaborator directly, which is what the
/// extraction newly makes possible — no device, no connection, no channels lock — and pin the
/// contract of the seam the device now depends on.
/// </remarks>
public class StatusChannelPopulatorTests
{
    private static StatusChannelPopulator Create(ILogger? logger = null, string name = "Nq1")
        => new(logger ?? NullLogger.Instance, () => name);

    [Fact]
    public void Constructor_WithNullLogger_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new StatusChannelPopulator(null!, () => "Nq1"));
    }

    [Fact]
    public void Constructor_WithNullDeviceName_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new StatusChannelPopulator(NullLogger.Instance, null!));
    }

    [Fact]
    public void Populate_AppendsAnalogThenDigital_AndReportsBothCounts()
    {
        var destination = new List<IChannel>();

        var (analogCount, digitalCount) = Create().Populate(
            new DaqifiOutMessage { AnalogInPortNum = 3, DigitalPortNum = 2 },
            Array.Empty<IChannel>(),
            destination);

        Assert.Equal(3, analogCount);
        Assert.Equal(2, digitalCount);
        Assert.Equal(
            new[] { ChannelType.Analog, ChannelType.Analog, ChannelType.Analog, ChannelType.Digital, ChannelType.Digital },
            destination.Select(c => c.Type));
        Assert.Equal(new[] { "AI0", "AI1", "AI2", "DIO0", "DIO1" }, destination.Select(c => c.Name));
    }

    [Fact]
    public void Populate_WithNoPortsReported_AppendsNothingAndReportsZero()
    {
        var destination = new List<IChannel>();

        var (analogCount, digitalCount) = Create().Populate(
            new DaqifiOutMessage(), Array.Empty<IChannel>(), destination);

        Assert.Equal(0, analogCount);
        Assert.Equal(0, digitalCount);
        Assert.Empty(destination);
    }

    [Fact]
    public void Populate_ReusesExistingChannelInstances_WhenIdentityIsUnchanged()
    {
        var existingAnalog = new AnalogChannel(0);
        var existingDigital = new DigitalChannel(0, isPwmCapable: true) { Direction = ChannelDirection.Output };
        var destination = new List<IChannel>();

        Create().Populate(
            new DaqifiOutMessage { AnalogInPortNum = 1, DigitalPortNum = 1 },
            new IChannel[] { existingAnalog, existingDigital },
            destination);

        // Same references back, so consumer-held channels and the configuration on them survive.
        Assert.Same(existingAnalog, destination[0]);
        Assert.Same(existingDigital, destination[1]);
        Assert.Equal(ChannelDirection.Output, destination[1].Direction);
    }

    [Fact]
    public void Populate_ResyncsAnalogIsEnabled_FromReportedMask()
    {
        // Channel 0 was enabled in Core's view; the device reports only channel 1 enabled.
        var existing = new AnalogChannel(0) { IsEnabled = true };
        var message = new DaqifiOutMessage { AnalogInPortNum = 2 };
        message.AnalogInPortEnabled = Google.Protobuf.ByteString.CopyFrom(new byte[] { 0b0000_0010 });
        var destination = new List<IChannel>();

        Create().Populate(message, new IChannel[] { existing }, destination);

        Assert.False(destination[0].IsEnabled);
        Assert.True(destination[1].IsEnabled);
    }

    [Fact]
    public void Populate_LeavesIsEnabledAlone_WhenNoMaskIsReported()
    {
        // An empty mask is ambiguous between "nothing enabled" and "not reported" on older
        // firmware, so it must not be treated as the source of truth.
        var existing = new AnalogChannel(0) { IsEnabled = true };
        var destination = new List<IChannel>();

        Create().Populate(new DaqifiOutMessage { AnalogInPortNum = 1 }, new IChannel[] { existing }, destination);

        Assert.True(destination[0].IsEnabled);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    [InlineData(7, true)]
    [InlineData(8, false)]
    public void Populate_MarksOnlyTheHardwarePwmChannelsAsPwmCapable(int channelNumber, bool expected)
    {
        var destination = new List<IChannel>();

        Create().Populate(new DaqifiOutMessage { DigitalPortNum = 9 }, Array.Empty<IChannel>(), destination);

        var channel = Assert.IsType<DigitalChannel>(destination[channelNumber]);
        Assert.Equal(expected, channel.IsPwmCapable);
    }

    [Theory]
    [InlineData(0b1111_1111, ChannelDirection.Input)]
    [InlineData(0b1111_1011, ChannelDirection.Output)]
    public void Populate_SetsDigitalDirection_FromTheReportedDirectionMask(byte lowByte, ChannelDirection expected)
    {
        // digital_port_dir is TRIS-encoded: bit set = input, bit clear = output — the inverse of
        // the argument Core sends outbound. Bit 2 is the one under test here (#685).
        var message = new DaqifiOutMessage { DigitalPortNum = 16 };
        message.DigitalPortDir = ByteString.CopyFrom(new byte[] { lowByte, 0xFF });
        var destination = new List<IChannel>();

        Create().Populate(message, Array.Empty<IChannel>(), destination);

        Assert.Equal(expected, destination[2].Direction);
        // The channels the device reported as inputs are unaffected either way.
        Assert.Equal(ChannelDirection.Input, destination[4].Direction);
        Assert.Equal(ChannelDirection.Input, destination[15].Direction);
    }

    [Fact]
    public void Populate_ResyncsDigitalDirection_OnAnExistingChannel()
    {
        // The device's own view outranks Core's, the same way analog IsEnabled does (#409):
        // an output pin left driven by a previous session must not come back as an input.
        var existing = new DigitalChannel(2, isPwmCapable: false) { Direction = ChannelDirection.Input };
        var message = new DaqifiOutMessage { DigitalPortNum = 3 };
        message.DigitalPortDir = ByteString.CopyFrom(new byte[] { 0b1111_1011 });
        var destination = new List<IChannel>();

        Create().Populate(message, new IChannel[] { existing }, destination);

        Assert.Same(existing, destination[2]);
        Assert.Equal(ChannelDirection.Output, existing.Direction);
    }

    [Fact]
    public void Populate_LeavesDigitalDirectionAlone_WhenNoDirectionMaskIsReported()
    {
        // Firmware that never populates the field, and the SD-card/replay paths that synthesize
        // status messages without one, must not stomp a locally-commanded direction.
        var existing = new DigitalChannel(0, isPwmCapable: true) { Direction = ChannelDirection.Output };
        var destination = new List<IChannel>();

        Create().Populate(
            new DaqifiOutMessage { DigitalPortNum = 1 }, new IChannel[] { existing }, destination);

        Assert.Equal(ChannelDirection.Output, destination[0].Direction);
    }

    [Fact]
    public void Populate_DefaultsDigitalDirectionToInput_ForChannelsBeyondTheReportedMask()
    {
        // A one-byte mask describes channels 0-7 only; channel 8 is "not reported", not "output".
        var existingBeyondMask = new DigitalChannel(8, isPwmCapable: false) { Direction = ChannelDirection.Output };
        var message = new DaqifiOutMessage { DigitalPortNum = 9 };
        message.DigitalPortDir = ByteString.CopyFrom(new byte[] { 0b1111_1011 });
        var destination = new List<IChannel>();

        Create().Populate(message, new IChannel[] { existingBeyondMask }, destination);

        Assert.Equal(ChannelDirection.Output, destination[2].Direction);   // described, and an output
        Assert.Equal(ChannelDirection.Input, destination[7].Direction);    // described, and an input
        Assert.Equal(ChannelDirection.Output, destination[8].Direction);   // undescribed: left as it was
    }

    [Fact]
    public void Populate_DefaultsNewDigitalChannelsToInput_WhenTheMaskIsTooShort()
    {
        var message = new DaqifiOutMessage { DigitalPortNum = 10 };
        message.DigitalPortDir = ByteString.CopyFrom(new byte[] { 0b0000_0000 });
        var destination = new List<IChannel>();

        Create().Populate(message, Array.Empty<IChannel>(), destination);

        Assert.Equal(ChannelDirection.Output, destination[0].Direction);   // described
        Assert.Equal(ChannelDirection.Input, destination[9].Direction);    // undescribed: Input default
    }

    [Theory]
    [InlineData(0u)]                     // not reported
    [InlineData(AnalogChannel.MinResolution - 1)]
    [InlineData(AnalogChannel.MaxResolution + 1)]
    public void Populate_SubstitutesAssumedResolution_WhenReportedValueIsUnusable(uint reported)
    {
        var destination = new List<IChannel>();

        Create().Populate(
            new DaqifiOutMessage { AnalogInPortNum = 1, AnalogInRes = reported },
            Array.Empty<IChannel>(),
            destination);

        var channel = Assert.IsType<AnalogChannel>(destination[0]);
        Assert.Equal(65535u, channel.Resolution);
        Assert.True(channel.ResolutionIsAssumed);
    }

    [Fact]
    public void Populate_SubstitutesSafeDefaults_ForNonFiniteScalingValues()
    {
        // A corrupt status frame must not throw out of AnalogChannel's validating setters and
        // abort population; the value is replaced and the population completes.
        var message = new DaqifiOutMessage { AnalogInPortNum = 1, AnalogInRes = 65535 };
        message.AnalogInCalM.Add(float.NaN);
        message.AnalogInCalB.Add(float.PositiveInfinity);
        message.AnalogInIntScaleM.Add(0f);            // zero is rejected for a multiplier
        message.AnalogInPortRange.Add(-1f);
        var destination = new List<IChannel>();

        Create().Populate(message, Array.Empty<IChannel>(), destination);

        var channel = Assert.IsType<AnalogChannel>(destination[0]);
        Assert.Equal(1.0, channel.CalibrationM);
        Assert.Equal(0.0, channel.CalibrationB);
        Assert.Equal(1.0, channel.InternalScaleM);
        Assert.Equal(1.0, channel.PortRange);
    }

    [Fact]
    public void Populate_UsesDefaults_WhenCalibrationArraysAreShorterThanTheChannelCount()
    {
        var message = new DaqifiOutMessage { AnalogInPortNum = 2, AnalogInRes = 65535 };
        message.AnalogInCalM.Add(2.5f);   // only channel 0 described
        var destination = new List<IChannel>();

        Create().Populate(message, Array.Empty<IChannel>(), destination);

        Assert.Equal(2.5, ((AnalogChannel)destination[0]).CalibrationM, 5);
        Assert.Equal(1.0, ((AnalogChannel)destination[1]).CalibrationM, 5);
    }

    [Fact]
    public void Populate_ReadsTheDeviceNameAtPopulateTime_NotAtConstruction()
    {
        // The name is supplied as a delegate precisely because it can change during the device's
        // lifetime (a friendly-name write); a warning naming the old device would be misleading.
        var logger = new CapturingLogger();
        var name = "Before";
        var populator = new StatusChannelPopulator(logger, () => name);
        name = "After";

        populator.Populate(
            new DaqifiOutMessage { AnalogInPortNum = 1, AnalogInRes = 0 },
            Array.Empty<IChannel>(),
            new List<IChannel>());

        Assert.Contains("After", Assert.Single(logger.Warnings));
    }

    [Fact]
    public void Populate_WithThrowingLogger_StillPopulates()
    {
        // The warnings are emitted exactly when the device reported something implausible, so a
        // faulting consumer logger must not turn a recoverable bad frame into a failed population.
        var destination = new List<IChannel>();

        var ex = Record.Exception(() => Create(new ThrowingLogger()).Populate(
            new DaqifiOutMessage { AnalogInPortNum = 2, AnalogInRes = 0 },
            Array.Empty<IChannel>(),
            destination));

        Assert.Null(ex);
        Assert.Equal(2, destination.Count);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => throw new InvalidOperationException("Consumer logger blew up.");
    }
}
