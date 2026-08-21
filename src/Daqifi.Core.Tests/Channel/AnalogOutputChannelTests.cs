using System;
using System.Collections.Generic;
using Daqifi.Core.Channel;
using Xunit;

namespace Daqifi.Core.Tests.Channel
{
    /// <summary>
    /// Covers the analog-output (DAC) channel model on its own: what it accepts, what it refuses,
    /// and the stage-then-latch bookkeeping that keeps <see cref="IAnalogOutputChannel.OutputVoltage"/>
    /// reporting only voltages the hardware is actually driving.
    /// </summary>
    public class AnalogOutputChannelTests
    {
        [Fact]
        public void Constructor_Defaults_DescribeAnNq3Dac()
        {
            var channel = new AnalogOutputChannel(0);

            Assert.Equal(ChannelType.AnalogOutput, channel.Type);
            Assert.Equal(ChannelDirection.Output, channel.Direction);
            Assert.Equal(0, channel.ChannelNumber);
            Assert.Equal(AnalogOutputChannel.DefaultResolutionBits, channel.ResolutionBits);
            Assert.Equal(AnalogOutputChannel.DefaultMinimumVoltage, channel.MinimumVoltage);
            Assert.Equal(AnalogOutputChannel.DefaultMaximumVoltage, channel.MaximumVoltage);
            Assert.Equal("Analog Output 0", channel.Name);
            Assert.Equal("Analog Output 0", channel.ToString());
            Assert.Null(channel.OutputVoltage);
            Assert.Null(channel.PendingVoltage);
            Assert.Null(channel.ActiveSample);
        }

        [Fact]
        public void Constructor_NegativeChannelNumber_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new AnalogOutputChannel(-1));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-4)]
        [InlineData(33)]
        public void Constructor_ImplausibleResolution_Throws(int resolutionBits)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new AnalogOutputChannel(0, resolutionBits));
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(60.0)]
        [InlineData(-60.0)]
        public void Constructor_ImplausibleRangeEndpoint_Throws(double minimum)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new AnalogOutputChannel(0, 12, minimum, 100.0));
        }

        [Theory]
        [InlineData(5.0, 5.0)]
        [InlineData(5.0, -5.0)]
        public void Constructor_EmptyOrInvertedRange_Throws(double minimum, double maximum)
        {
            Assert.Throws<ArgumentException>(
                () => new AnalogOutputChannel(0, 12, minimum, maximum));
        }

        [Fact]
        public void Direction_SetToOutput_IsAccepted()
        {
            var channel = new AnalogOutputChannel(0) { Direction = ChannelDirection.Output };

            Assert.Equal(ChannelDirection.Output, channel.Direction);
        }

        [Theory]
        [InlineData(ChannelDirection.Input)]
        [InlineData(ChannelDirection.Unknown)]
        public void Direction_SetToAnythingElse_Throws(ChannelDirection direction)
        {
            var channel = new AnalogOutputChannel(0);

            Assert.Throws<ArgumentException>(() => channel.Direction = direction);
        }

        [Theory]
        [InlineData(0.0, true)]     // the low endpoint is in range
        [InlineData(10.0, true)]    // so is the high one
        [InlineData(5.0, true)]
        [InlineData(-0.1, false)]
        [InlineData(10.1, false)]
        [InlineData(double.NaN, false)]
        [InlineData(double.PositiveInfinity, false)]
        public void IsInRange_TreatsTheStatedRangeAsInclusive(double voltage, bool expected)
        {
            var channel = new AnalogOutputChannel(0, 12, 0.0, 10.0);

            Assert.Equal(expected, channel.IsInRange(voltage));
        }

        [Fact]
        public void Stage_RecordsPendingWithoutChangingTheDrivenValue()
        {
            var channel = new AnalogOutputChannel(0);
            var samples = Subscribe(channel);

            channel.Stage(3.25);

            Assert.Equal(3.25, channel.PendingVoltage);
            Assert.Null(channel.OutputVoltage);
            Assert.Empty(samples);
        }

        [Fact]
        public void Latch_PromotesThePendingValueAndAnnouncesIt()
        {
            var channel = new AnalogOutputChannel(0);
            var samples = Subscribe(channel);
            var at = new DateTime(2026, 8, 13, 12, 0, 0, DateTimeKind.Local);

            channel.Stage(3.25);
            var latched = channel.Latch(at);

            Assert.True(latched);
            Assert.Equal(3.25, channel.OutputVoltage);
            Assert.Null(channel.PendingVoltage);
            Assert.Equal(new[] { 3.25 }, samples);
            Assert.Equal(at, channel.ActiveSample!.Timestamp);
        }

        [Fact]
        public void Latch_WithNothingStaged_ChangesNothing()
        {
            var channel = new AnalogOutputChannel(0);
            channel.Stage(1.0);
            channel.Latch(DateTime.Now);

            var samples = Subscribe(channel);
            var latched = channel.Latch(DateTime.Now);

            Assert.False(latched);
            Assert.Equal(1.0, channel.OutputVoltage);
            Assert.Empty(samples);
        }

        [Fact]
        public void Stage_Twice_LatchesOnlyTheLatestValue()
        {
            var channel = new AnalogOutputChannel(0);
            var samples = Subscribe(channel);

            channel.Stage(1.0);
            channel.Stage(2.0);
            channel.Latch(DateTime.Now);

            Assert.Equal(new[] { 2.0 }, samples);
            Assert.Equal(2.0, channel.OutputVoltage);
        }

        [Fact]
        public void ClearPending_DropsTheStagedValueWithoutDrivingIt()
        {
            var channel = new AnalogOutputChannel(0);
            var samples = Subscribe(channel);

            channel.Stage(4.0);
            channel.ClearPending();

            Assert.Null(channel.PendingVoltage);
            Assert.Null(channel.OutputVoltage);
            Assert.False(channel.Latch(DateTime.Now));
            Assert.Empty(samples);
        }

        [Fact]
        public void SetOutputVoltage_RecordsTheValueWithoutStaging()
        {
            var channel = new AnalogOutputChannel(0);
            var samples = Subscribe(channel);

            channel.SetOutputVoltage(7.5, DateTime.Now);

            Assert.Equal(7.5, channel.OutputVoltage);
            Assert.Null(channel.PendingVoltage);
            Assert.Equal(new[] { 7.5 }, samples);
        }

        [Fact]
        public void UpdateFromCapabilities_ReplacesTheResolutionAndRangeInPlace()
        {
            var channel = new AnalogOutputChannel(0, 12, 0.0, 10.0);

            channel.UpdateFromCapabilities(16, -5.0, 5.0, rangeIsAssumed: true);

            Assert.Equal(16, channel.ResolutionBits);
            Assert.Equal(-5.0, channel.MinimumVoltage);
            Assert.Equal(5.0, channel.MaximumVoltage);
            Assert.True(channel.RangeIsAssumed);
            Assert.True(channel.IsInRange(-5.0));
            Assert.False(channel.IsInRange(7.5));
        }

        [Fact]
        public void SetActiveSample_NullSample_Throws()
        {
            var channel = new AnalogOutputChannel(0);

            Assert.Throws<ArgumentNullException>(() => channel.SetActiveSample(null!));
        }

        /// <summary>
        /// Collects the values announced through <see cref="IChannel.SampleReceived"/>, which is how
        /// a consumer (a UI binding, the live sample stream) learns an output changed.
        /// </summary>
        private static List<double> Subscribe(AnalogOutputChannel channel)
        {
            var values = new List<double>();
            channel.SampleReceived += (_, e) =>
            {
                Assert.Same(channel, e.Channel);
                values.Add(e.Sample.Value);
            };
            return values;
        }
    }
}
