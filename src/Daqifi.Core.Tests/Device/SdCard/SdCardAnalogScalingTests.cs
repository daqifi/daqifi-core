using Daqifi.Core.Device.SdCard;

namespace Daqifi.Core.Tests.Device.SdCard;

/// <summary>
/// Direct tests for <see cref="SdCardAnalogScaling"/>, the one raw-count-to-volts conversion
/// shared by all three SD card log parsers (protobuf, CSV, JSON).
/// </summary>
/// <remarks>
/// <para>
/// Until now this helper had no tests of its own. It was reached only through the three parsers'
/// fixtures, and every one of those fixtures supplies a calibration, port-range, and internal-scale
/// list at least as long as the channel count, with the <em>same</em> value in every slot. That
/// leaves two whole families of behaviour unpinned:
/// </para>
/// <list type="bullet">
///   <item>
///     the per-channel indexing — with uniform lists, a helper that read slot 0 for every channel
///     would produce byte-identical output, so a transposed or pinned index could not be seen;
///   </item>
///   <item>
///     the short-list fallbacks (<c>channel &lt; list.Count</c>) that supply unity gain, no offset,
///     a one-volt range, and a unity internal scale for channels the log did not describe — the
///     branch that stands between a ragged status message and an <see cref="IndexOutOfRangeException"/>
///     mid-download.
///   </item>
/// </list>
/// <para>
/// The pass-through contracts are pinned here too: with no configuration the
/// <see cref="IReadOnlyList{Double}"/> overload hands back the caller's own list rather than a copy,
/// while the <see cref="IReadOnlyList{Int32}"/> overload must still widen each count to
/// <see cref="double"/>.
/// </para>
/// </remarks>
public class SdCardAnalogScalingTests
{
    /// <summary>
    /// A configuration whose every per-channel list holds a <em>different</em> value in each slot,
    /// so a result can only come out right if each channel was read from its own index.
    /// </summary>
    private static SdCardDeviceConfiguration PerChannelConfig(
        IReadOnlyList<(double Slope, double Intercept)>? calibration = null,
        IReadOnlyList<double>? portRange = null,
        IReadOnlyList<double>? internalScale = null) => new(
        AnalogPortCount: 3,
        DigitalPortCount: 0,
        TimestampFrequency: 0u,
        DeviceSerialNumber: null,
        DevicePartNumber: null,
        FirmwareRevision: null,
        CalibrationValues: calibration ?? new[] { (2.0, 0.5), (3.0, -0.25), (4.0, 0.125) },
        Resolution: 1000u,
        PortRange: portRange ?? new[] { 10.0, 20.0, 40.0 },
        InternalScaleM: internalScale ?? new[] { 1.0, 2.0, 0.5 });

    // Raw counts chosen so that raw/Resolution*PortRange is never exactly 1.0 on any channel:
    // at 1.0 the gain and the offset become interchangeable and a slope/intercept swap would
    // still land on the expected volts.
    private static readonly double[] RawCounts = [150.0, 200.0, 400.0];
    private static readonly int[] RawIntCounts = [150, 200, 400];

    // ------------------------------------------------------------------
    // Pass-through, IReadOnlyList<double> overload
    // ------------------------------------------------------------------

    [Fact]
    public void ScaleRawAnalogValues_Double_WithNullConfig_HandsBackTheCallersOwnList()
    {
        // Arrange — a log with no status message and no connected device to fall back on.
        IReadOnlyList<double> raw = RawCounts;

        // Act
        var result = SdCardAnalogScaling.ScaleRawAnalogValues(raw, null);

        // Assert — documented as "returned unchanged": not merely equal, the same instance.
        // Nothing is copied or reallocated per sample on the unscaled path.
        Assert.Same(raw, result);
    }

    [Fact]
    public void ScaleRawAnalogValues_Double_WithZeroResolution_HandsBackTheCallersOwnList()
    {
        // Arrange — a status message that named calibration, range, and scale but no resolution.
        // Without the zero-resolution guard the division below would run and yield infinities.
        var config = PerChannelConfig() with { Resolution = 0u };
        IReadOnlyList<double> raw = RawCounts;

        // Act
        var result = SdCardAnalogScaling.ScaleRawAnalogValues(raw, config);

        // Assert
        Assert.Same(raw, result);
    }

    // ------------------------------------------------------------------
    // Per-channel indexing, IReadOnlyList<double> overload
    // ------------------------------------------------------------------

    [Fact]
    public void ScaleRawAnalogValues_Double_ReadsEachChannelsOwnCalibrationRangeAndScale()
    {
        // Arrange — every slot of every list differs, so each expected value below is reachable
        // only from that channel's own index.
        var config = PerChannelConfig();

        // Act
        var result = SdCardAnalogScaling.ScaleRawAnalogValues(RawCounts, config);

        // Assert — raw/Resolution * PortRange * Slope * InternalScaleM + Intercept, per channel.
        Assert.Equal(3, result.Count);
        Assert.Equal(150.0 / 1000.0 * 10.0 * 2.0 * 1.0 + 0.5, result[0], 10);
        Assert.Equal(200.0 / 1000.0 * 20.0 * 3.0 * 2.0 - 0.25, result[1], 10);
        Assert.Equal(400.0 / 1000.0 * 40.0 * 4.0 * 0.5 + 0.125, result[2], 10);
    }

    [Fact]
    public void ScaleRawAnalogValues_Double_LeavesTheOffsetOutOfTheInternalScale()
    {
        // Arrange — channel 1 carries both a non-unity internal scale (2.0) and a non-zero
        // intercept (-0.25). Scaling the intercept along with the gain would land on 23.5.
        var config = PerChannelConfig();

        // Act
        var result = SdCardAnalogScaling.ScaleRawAnalogValues(RawCounts, config);

        // Assert — the intercept is volts, added after the internal scale, never through it.
        Assert.Equal(24.0 - 0.25, result[1], 10);
    }

    // ------------------------------------------------------------------
    // Short per-channel lists, IReadOnlyList<double> overload
    // ------------------------------------------------------------------

    [Fact]
    public void ScaleRawAnalogValues_Double_WhenCalibrationRunsOut_TheRestGetUnityGainAndNoOffset()
    {
        // Arrange — three channels of data but calibration for only the first two, the shape a
        // status message takes when its calibration arrays lag its port count.
        var config = PerChannelConfig(calibration: new[] { (2.0, 0.5), (3.0, -0.25) });

        // Act
        var result = SdCardAnalogScaling.ScaleRawAnalogValues(RawCounts, config);

        // Assert — the covered channels are unaffected...
        Assert.Equal(150.0 / 1000.0 * 10.0 * 2.0 * 1.0 + 0.5, result[0], 10);
        Assert.Equal(200.0 / 1000.0 * 20.0 * 3.0 * 2.0 - 0.25, result[1], 10);

        // ...and channel 2 falls back to slope 1 / intercept 0 rather than reusing the last
        // entry (which would give 23.75) or reading past the end of the list.
        Assert.Equal(400.0 / 1000.0 * 40.0 * 1.0 * 0.5 + 0.0, result[2], 10);
    }

    [Fact]
    public void ScaleRawAnalogValues_Double_WhenPortRangeRunsOut_TheRestGetAOneVoltRange()
    {
        // Arrange — only channel 0 has a stated range.
        var config = PerChannelConfig(portRange: new[] { 10.0 });

        // Act
        var result = SdCardAnalogScaling.ScaleRawAnalogValues(RawCounts, config);

        // Assert
        Assert.Equal(150.0 / 1000.0 * 10.0 * 2.0 * 1.0 + 0.5, result[0], 10);
        Assert.Equal(200.0 / 1000.0 * 1.0 * 3.0 * 2.0 - 0.25, result[1], 10);
        Assert.Equal(400.0 / 1000.0 * 1.0 * 4.0 * 0.5 + 0.125, result[2], 10);
    }

    [Fact]
    public void ScaleRawAnalogValues_Double_WhenInternalScaleRunsOut_TheRestGetUnityScale()
    {
        // Arrange — internal scale for the first two channels only.
        var config = PerChannelConfig(internalScale: new[] { 1.0, 2.0 });

        // Act
        var result = SdCardAnalogScaling.ScaleRawAnalogValues(RawCounts, config);

        // Assert — channel 2 keeps its own slope and intercept, only the scale defaults.
        Assert.Equal(200.0 / 1000.0 * 20.0 * 3.0 * 2.0 - 0.25, result[1], 10);
        Assert.Equal(400.0 / 1000.0 * 40.0 * 4.0 * 1.0 + 0.125, result[2], 10);
    }

    [Fact]
    public void ScaleRawAnalogValues_Double_WithNoCalibrationAtAll_StillAppliesRangeAndScale()
    {
        // Arrange — an uncalibrated log that still states its ranges and front-end scales.
        // Every parser fixture today supplies either all four lists or none, so this mixed
        // shape has never been exercised.
        var config = PerChannelConfig(internalScale: new[] { 1.0, 2.0, 0.25 })
            with
        { CalibrationValues = null };

        // Act
        var result = SdCardAnalogScaling.ScaleRawAnalogValues(RawCounts, config);

        // Assert — unity gain and no offset, but the range and scale still count.
        Assert.Equal(150.0 / 1000.0 * 10.0 * 1.0 * 1.0, result[0], 10);
        Assert.Equal(200.0 / 1000.0 * 20.0 * 1.0 * 2.0, result[1], 10);
        Assert.Equal(400.0 / 1000.0 * 40.0 * 1.0 * 0.25, result[2], 10);
    }

    [Fact]
    public void ScaleRawAnalogValues_Double_WithNoSamplesInTheRow_ReturnsNothing()
    {
        // Arrange — a data row that carried no analog columns at all.
        var config = PerChannelConfig();

        // Act
        var result = SdCardAnalogScaling.ScaleRawAnalogValues(Array.Empty<double>(), config);

        // Assert — driven by the row width, not by how many channels the config describes.
        Assert.Empty(result);
    }

    // ------------------------------------------------------------------
    // IReadOnlyList<int> overload
    // ------------------------------------------------------------------

    [Fact]
    public void ScaleRawAnalogValues_Int_WithNullConfig_WidensEachCountUnscaled()
    {
        // Arrange — the protobuf path's raw ADC counts, including a negative and a full-scale
        // 16-bit reading, with no configuration to scale them by.
        int[] raw = [-250, 0, 65535];

        // Act
        var result = SdCardAnalogScaling.ScaleRawAnalogValues(raw, null);

        // Assert — each count arrives as its own double, in order and unaltered. Unlike the
        // double overload this one cannot hand the input back, so it must copy rather than
        // return an empty or default-filled array.
        Assert.Equal([-250.0, 0.0, 65535.0], result);
    }

    [Fact]
    public void ScaleRawAnalogValues_Int_WithZeroResolution_WidensEachCountUnscaled()
    {
        // Arrange — configuration present, resolution absent.
        var config = PerChannelConfig() with { Resolution = 0u };

        // Act
        var result = SdCardAnalogScaling.ScaleRawAnalogValues(RawIntCounts, config);

        // Assert
        Assert.Equal([150.0, 200.0, 400.0], result);
    }

    [Fact]
    public void ScaleRawAnalogValues_Int_ReadsEachChannelsOwnCalibrationRangeAndScale()
    {
        // Arrange
        var config = PerChannelConfig();

        // Act
        var result = SdCardAnalogScaling.ScaleRawAnalogValues(RawIntCounts, config);

        // Assert
        Assert.Equal(3, result.Count);
        Assert.Equal(150.0 / 1000.0 * 10.0 * 2.0 * 1.0 + 0.5, result[0], 10);
        Assert.Equal(200.0 / 1000.0 * 20.0 * 3.0 * 2.0 - 0.25, result[1], 10);
        Assert.Equal(400.0 / 1000.0 * 40.0 * 4.0 * 0.5 + 0.125, result[2], 10);
    }

    [Fact]
    public void ScaleRawAnalogValues_Int_WhenCalibrationRunsOut_TheRestGetUnityGainAndNoOffset()
    {
        // Arrange — the short-list fallback has to hold on this overload too; the two bodies
        // are separate code and only a test on each keeps them from drifting.
        var config = PerChannelConfig(calibration: new[] { (2.0, 0.5), (3.0, -0.25) });

        // Act
        var result = SdCardAnalogScaling.ScaleRawAnalogValues(RawIntCounts, config);

        // Assert
        Assert.Equal(200.0 / 1000.0 * 20.0 * 3.0 * 2.0 - 0.25, result[1], 10);
        Assert.Equal(400.0 / 1000.0 * 40.0 * 1.0 * 0.5 + 0.0, result[2], 10);
    }

    [Fact]
    public void ScaleRawAnalogValues_Int_WithACountBelowZero_ScalesToANegativeVoltage()
    {
        // Arrange — a bipolar input reading below its zero point. Nothing may clamp or take a
        // magnitude on the way through.
        var config = PerChannelConfig() with
        {
            CalibrationValues = new[] { (1.0, 0.0) },
            PortRange = new[] { 10.0 },
            InternalScaleM = new[] { 1.0 }
        };

        // Act
        var result = SdCardAnalogScaling.ScaleRawAnalogValues(new[] { -250 }, config);

        // Assert
        Assert.Equal(-250.0 / 1000.0 * 10.0, result[0], 10);
    }

    [Fact]
    public void ScaleRawAnalogValues_Int_WithNoSamplesInTheRow_ReturnsNothing()
    {
        // Arrange
        var config = PerChannelConfig();

        // Act
        var result = SdCardAnalogScaling.ScaleRawAnalogValues(Array.Empty<int>(), config);

        // Assert
        Assert.Empty(result);
    }

    // ------------------------------------------------------------------
    // The two overloads are one conversion
    // ------------------------------------------------------------------

    [Fact]
    public void ScaleRawAnalogValues_BothOverloads_AgreeOnTheSameCounts()
    {
        // Arrange — the whole reason this helper exists is that the protobuf path and the two
        // text paths must never disagree about the same sample, and they enter through
        // different overloads.
        var config = PerChannelConfig(calibration: new[] { (2.0, 0.5), (3.0, -0.25) });

        // Act
        var fromDoubles = SdCardAnalogScaling.ScaleRawAnalogValues(RawCounts, config);
        var fromInts = SdCardAnalogScaling.ScaleRawAnalogValues(RawIntCounts, config);

        // Assert — bit-for-bit, not merely close: the two bodies must compute the identical
        // expression, short-list fallbacks included.
        Assert.Equal(fromDoubles, fromInts);
    }
}
