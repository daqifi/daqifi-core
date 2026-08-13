using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Capabilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace Daqifi.Core.Tests.Device.Capabilities;

/// <summary>
/// Tests for the per-configuration sample-rate cap — the ceiling for the channel set a device has
/// enabled right now, as opposed to the board's absolute ceiling. The arithmetic and the
/// enforcement rule moved here from the MCP server, which was the only consumer that had them
/// (#481); the cases below that read like MCP scenarios are the ported ones.
/// </summary>
public class SampleRateCapTests
{
    #region Compute — source precedence and bounds

    [Fact]
    public void Compute_NothingReported_FallsBackToTheHardwareMaximum()
    {
        Assert.Equal(22000, SampleRateCap.Compute(22000, deviceReportedCapHz: null, modelCapHz: null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Compute_NonPositiveHardwareMaximum_IsFlooredToOne(int hardwareMaximumRateHz)
    {
        Assert.Equal(1, SampleRateCap.Compute(hardwareMaximumRateHz, deviceReportedCapHz: null, modelCapHz: null));
    }

    [Fact]
    public void Compute_DeviceReportedCapBelowTheHardwareMaximum_Wins()
    {
        // #447 bench figures: 1 analog channel -> 7746 Hz cap; 16 channels -> 3518 Hz cap.
        Assert.Equal(7746, SampleRateCap.Compute(22000, deviceReportedCapHz: 7746, modelCapHz: null));
        Assert.Equal(3518, SampleRateCap.Compute(22000, deviceReportedCapHz: 3518, modelCapHz: null));
    }

    [Fact]
    public void Compute_DeviceReportedCapAboveTheHardwareMaximum_IsBounded()
    {
        // A self-inconsistent document (or a channel-set read racing a board-table update) must
        // never report a cap above the absolute ISR ceiling.
        Assert.Equal(22000, SampleRateCap.Compute(22000, deviceReportedCapHz: 50000, modelCapHz: null));
    }

    [Fact]
    public void Compute_ZeroDeviceReportedCap_IsARealAnswerNotFlooredToOne()
    {
        // Zero enabled channels genuinely caps the rate at 0 — unlike the hardware maximum, this is
        // not treated as an uninitialized value.
        Assert.Equal(0, SampleRateCap.Compute(22000, deviceReportedCapHz: 0, modelCapHz: null));
    }

    [Fact]
    public void Compute_NegativeDeviceReportedCap_IsTreatedAsAbsent()
    {
        // A malformed capability document could report a negative current_max_rate_hz (the JSON
        // parser accepts any int32). Left unguarded that produces a negative effective cap, which
        // both misreads as "nothing enabled" and disables every `cap > 0` guard downstream.
        Assert.Equal(22000, SampleRateCap.Compute(22000, deviceReportedCapHz: -1, modelCapHz: null));
        Assert.Equal(5000, SampleRateCap.Compute(22000, deviceReportedCapHz: -1, modelCapHz: 5000));
    }

    [Fact]
    public void Compute_ModelIsUsedOnlyWhenTheDeviceStatedNothing()
    {
        // The model accounts for channel count and type only, so it can sit above the cap the
        // device would actually enforce. The device's own answer therefore outranks it — including
        // when the model is the more generous of the two, which is the case that would otherwise
        // let a client command a rate the firmware rejects outright.
        Assert.Equal(3518, SampleRateCap.Compute(22000, deviceReportedCapHz: 3518, modelCapHz: 11000));
        Assert.Equal(11000, SampleRateCap.Compute(22000, deviceReportedCapHz: null, modelCapHz: 11000));
    }

    [Fact]
    public void Compute_ModelCapAboveTheHardwareMaximum_IsBounded()
    {
        Assert.Equal(22000, SampleRateCap.Compute(22000, deviceReportedCapHz: null, modelCapHz: 50000));
    }

    #endregion

    #region Enforce — the rule for an already-live rate

    [Fact]
    public void Enforce_RateAtOrBelowTheCap_IsNotAdjusted()
    {
        Assert.Equal((3518, (int?)null), SampleRateCap.Enforce(3518, capHz: 3518));
        Assert.Equal((1000, (int?)null), SampleRateCap.Enforce(1000, capHz: 3518));
    }

    [Fact]
    public void Enforce_RateAboveTheCap_IsLoweredAndReportsThePreviousRate()
    {
        // The exact reorder trap from #447: one channel enabled -> cap 7746; rate set to 7746;
        // sixteen channels enabled -> the cap drops to 3518 while 7746 is still live.
        var (newRateHz, adjustedFromHz) = SampleRateCap.Enforce(7746, capHz: 3518);

        Assert.Equal(3518, newRateHz);
        Assert.Equal(7746, adjustedFromHz);
    }

    [Fact]
    public void Enforce_ZeroCap_LeavesTheRateAlone()
    {
        // A cap of 0 (nothing enabled) must not drive the live rate to 0. The rate is stale until
        // the channel set changes again, not invalid.
        var (newRateHz, adjustedFromHz) = SampleRateCap.Enforce(7746, capHz: 0);

        Assert.Equal(7746, newRateHz);
        Assert.Null(adjustedFromHz);
    }

    #endregion

    #region ComputeForDevice — reading the sources off a real device

    [Fact]
    public void ComputeForDevice_WithNoCapabilityDocument_IsTheBoardCeiling()
    {
        var device = CreateDevice();

        Assert.Equal(22000, device.MaximumStreamingFrequencyHz);
    }

    [Fact]
    public void ComputeForDevice_WithTheDevicesOwnCap_ReportsIt()
    {
        var device = CreateDevice();
        device.Metadata.ApplyCapabilityDocument(BenchDocument(currentMaximumRateHz: 3518));

        Assert.Equal(3518, device.MaximumStreamingFrequencyHz);
    }

    [Fact]
    public void ComputeForDevice_WithTheDevicesOwnCapOfZero_ReportsNoCapacity()
    {
        // What the bench NQ1 actually answers when the document is read with nothing enabled.
        var device = CreateDevice();
        device.Metadata.ApplyCapabilityDocument(BenchDocument(currentMaximumRateHz: 0));

        Assert.Equal(0, device.MaximumStreamingFrequencyHz);
    }

    [Fact]
    public void ComputeForDevice_WithoutADeviceCap_EvaluatesTheModelOverTheEnabledChannels()
    {
        // The formula the bench NQ1 publishes, for four muxed channels:
        // min(22000, -, 110000/(6+4)) = 11000 Hz.
        var device = CreateDevice();
        device.Metadata.ApplyCapabilityDocument(BenchDocument(currentMaximumRateHz: null));
        EnableAnalog(device, 0, 1, 2, 3);

        Assert.Equal(11000, device.MaximumStreamingFrequencyHz);
    }

    [Fact]
    public void ComputeForDevice_WithoutADeviceCap_EvaluatesTheModelOverAMixedSelection()
    {
        // Channels 4 and 8 are the document's dedicated-converter ("simultaneous") channels, so
        // this selection is 2 of 3: min(22000, 55000/2, 110000/(6+3)) = 12222 Hz.
        var device = CreateDevice();
        device.Metadata.ApplyCapabilityDocument(BenchDocument(currentMaximumRateHz: null));
        EnableAnalog(device, 4, 8, 1);

        Assert.Equal(12222, device.MaximumStreamingFrequencyHz);
    }

    [Fact]
    public void ComputeForDevice_ModelCounts_SeparateDedicatedConverterChannelsFromMuxedOnes()
    {
        // On the bench board's own constants the two terms happen to coincide for every selection,
        // so miscounting the split there is invisible. This uses a hand-built document where the
        // dedicated-converter term is the only one that can bind: with both selected channels
        // counted as dedicated, min(-, 40000/2, 500000/(0+2)) = 20000 Hz; counted as muxed, the
        // Type-1 term drops out and 500000/2 is bounded to the board's 22000 Hz ceiling instead.
        //
        // The digital pins are listed first, and with the same ids: the document numbers analog
        // inputs and digital pins from 0 independently, so a lookup by id alone would find the
        // digital entry — which is never "simultaneous" — and silently report the muxed answer.
        var device = CreateDevice();
        device.Metadata.ApplyCapabilityDocument(new CapabilityDocument
        {
            SchemaVersion = 2,
            Channels = new[]
            {
                new CapabilityChannel { Id = 0, Kind = CapabilityChannelKind.DigitalIo },
                new CapabilityChannel { Id = 1, Kind = CapabilityChannelKind.DigitalIo },
                AnalogInput(0, isSimultaneous: true),
                AnalogInput(1, isSimultaneous: true),
                AnalogInput(2, isSimultaneous: false)
            },
            Streaming = new CapabilityStreaming
            {
                RateModel = new CapabilityRateModel
                {
                    Type1AggregateMaximumHz = 40000,
                    PerTickBudgetHz = 500000,
                    PerTickOverhead = 0
                }
            }
        });
        EnableAnalog(device, 0, 1);

        Assert.Equal(20000, device.MaximumStreamingFrequencyHz);
    }

    [Fact]
    public void ComputeForDevice_ModelCounts_IgnoreDisabledAndDigitalChannels()
    {
        // Two analog channels enabled out of sixteen, with every digital channel enabled too:
        // min(22000, -, 110000/(6+2)) = 13750 Hz. Digital cost is amortized into the model's
        // per-tick overhead, so counting the digital channels here would report 110000/24 = 4583.
        var device = CreateDevice();
        device.Metadata.ApplyCapabilityDocument(BenchDocument(currentMaximumRateHz: null));
        EnableAnalog(device, 0, 1);
        foreach (var digital in device.Channels.Where(c => c.Type == ChannelType.Digital))
        {
            digital.IsEnabled = true;
        }

        Assert.Equal(13750, device.MaximumStreamingFrequencyHz);
    }

    [Fact]
    public void ComputeForDevice_ModelCounts_MoveWithTheEnabledSet()
    {
        // The point of the model fallback: unlike the device's own figure, it is re-evaluated
        // against the set that is enabled right now rather than the set that was enabled when the
        // document was read.
        var device = CreateDevice();
        device.Metadata.ApplyCapabilityDocument(BenchDocument(currentMaximumRateHz: null));

        EnableAnalog(device, 0);
        Assert.Equal(15714, device.MaximumStreamingFrequencyHz);

        EnableAnalog(device, 1, 2, 3);
        Assert.Equal(11000, device.MaximumStreamingFrequencyHz);
    }

    [Fact]
    public void ComputeForDevice_WithADocumentThatStatesNoRates_IsTheBoardCeiling()
    {
        var device = CreateDevice();
        device.Metadata.ApplyCapabilityDocument(new CapabilityDocument { SchemaVersion = 2 });

        Assert.Equal(22000, device.MaximumStreamingFrequencyHz);
    }

    [Fact]
    public void ComputeForDevice_NullDevice_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SampleRateCap.ComputeForDevice(null!));
    }

    #endregion

    #region EnforceStreamingFrequencyCap — the device-level enforcement

    [Fact]
    public void EnforceStreamingFrequencyCap_RateAboveTheCap_LowersItAndReportsThePreviousRate()
    {
        var device = CreateDevice();
        device.Metadata.ApplyCapabilityDocument(BenchDocument(currentMaximumRateHz: 7746));
        device.StreamingFrequency = 7746;

        // The channel set grew, so the device's next document read reports a lower cap.
        device.Metadata.ApplyCapabilityDocument(BenchDocument(currentMaximumRateHz: 3518));

        Assert.Equal(7746, device.EnforceStreamingFrequencyCap());
        Assert.Equal(3518, device.StreamingFrequency);
    }

    [Fact]
    public void EnforceStreamingFrequencyCap_RateUnderTheCap_ChangesNothing()
    {
        var device = CreateDevice();
        device.Metadata.ApplyCapabilityDocument(BenchDocument(currentMaximumRateHz: 3518));
        device.StreamingFrequency = 1000;

        Assert.Null(device.EnforceStreamingFrequencyCap());
        Assert.Equal(1000, device.StreamingFrequency);
    }

    [Fact]
    public void EnforceStreamingFrequencyCap_WithNothingEnabled_LeavesTheRateAlone()
    {
        var device = CreateDevice();
        device.Metadata.ApplyCapabilityDocument(BenchDocument(currentMaximumRateHz: 0));
        device.StreamingFrequency = 7746;

        Assert.Null(device.EnforceStreamingFrequencyCap());
        Assert.Equal(7746, device.StreamingFrequency);
    }

    [Fact]
    public void EnforceStreamingFrequencyCap_IsReachableThroughTheStreamingInterface()
    {
        // The members are on IStreamingDevice, so a consumer holding the interface — which is what
        // the MCP server and the desktop application hold — gets them without a downcast.
        IStreamingDevice device = CreateDevice();
        device.Metadata.ApplyCapabilityDocument(BenchDocument(currentMaximumRateHz: 3518));
        device.StreamingFrequency = 7746;

        Assert.Equal(3518, device.MaximumStreamingFrequencyHz);
        Assert.Equal(7746, device.EnforceStreamingFrequencyCap());
        Assert.Equal(3518, device.StreamingFrequency);
    }

    [Fact]
    public void EnforceOn_NullDevice_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SampleRateCap.EnforceOn(null!));
    }

    #endregion

    #region Helpers

    /// <summary>
    /// A connected NQ1 with the board's sixteen analog and sixteen digital channels populated and
    /// nothing enabled.
    /// </summary>
    private static TestableStreamingDevice CreateDevice()
    {
        var device = new TestableStreamingDevice("BenchNq1");
        device.Metadata.DeviceType = DeviceType.Nyquist1;
        device.Metadata.Capabilities = DeviceCapabilities.FromDeviceType(DeviceType.Nyquist1);
        device.Metadata.Capabilities.MaxSamplingRate = 22000;
        device.PopulateChannelsFromStatus(new DaqifiOutMessage
        {
            AnalogInPortNum = 16,
            AnalogInRes = 65535,
            DigitalPortNum = 16
        });
        device.Connect();
        return device;
    }

    private static void EnableAnalog(DaqifiStreamingDevice device, params int[] channelNumbers)
    {
        foreach (var channelNumber in channelNumbers)
        {
            device.EnableChannel(device.Channels.First(
                c => c.Type == ChannelType.Analog && c.ChannelNumber == channelNumber));
        }
    }

    /// <summary>
    /// The bench NQ1's real capability document, re-projected so the test can choose whether the
    /// device stated a current cap. Channels 4, 8, 10, 12 and 14 are its dedicated-converter
    /// channels, and the rate-model constants are the ones firmware 3.7.2 publishes.
    /// </summary>
    private static CapabilityDocument BenchDocument(int? currentMaximumRateHz)
    {
        var simultaneous = new HashSet<int> { 4, 8, 10, 12, 14 };
        var channels = new List<CapabilityChannel>();
        for (var id = 0; id < 16; id++)
        {
            channels.Add(AnalogInput(id, simultaneous.Contains(id)));
        }

        for (var id = 0; id < 16; id++)
        {
            channels.Add(new CapabilityChannel { Id = id, Kind = CapabilityChannelKind.DigitalIo });
        }

        return new CapabilityDocument
        {
            SchemaVersion = 2,
            Channels = channels,
            Streaming = new CapabilityStreaming
            {
                MinimumSampleRateHz = 1,
                MaximumSampleRateHz = 22000,
                ConservativeEnvelopeHz = 500,
                CurrentMaximumRateHz = currentMaximumRateHz,
                RateValidation = "error",
                RateModel = new CapabilityRateModel
                {
                    AbsoluteMaximumHz = 22000,
                    Type1AggregateMaximumHz = 55000,
                    PerTickBudgetHz = 110000,
                    PerTickOverhead = 6
                }
            }
        };
    }

    private static CapabilityChannel AnalogInput(int id, bool isSimultaneous) => new()
    {
        Id = id,
        Kind = CapabilityChannelKind.AnalogInput,
        IsSimultaneous = isSimultaneous
    };

    /// <summary>
    /// A <see cref="DaqifiStreamingDevice"/> that swallows outbound messages, so the channel
    /// enable/disable calls these tests make run without a transport.
    /// </summary>
    private sealed class TestableStreamingDevice : DaqifiStreamingDevice
    {
        public TestableStreamingDevice(string name, IPAddress? ipAddress = null) : base(name, ipAddress)
        {
        }

        public override void Send<T>(IOutboundMessage<T> message)
        {
        }
    }

    #endregion
}
