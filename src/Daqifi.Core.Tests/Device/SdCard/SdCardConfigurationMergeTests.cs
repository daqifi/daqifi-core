using Daqifi.Core.Device.SdCard;

namespace Daqifi.Core.Tests.Device.SdCard;

/// <summary>
/// Direct tests for <see cref="SdCardConfigurationMerge"/>, the shared "file-derived values win,
/// the connected device's snapshot fills the gaps" merge behind both text SD card log parsers.
/// </summary>
/// <remarks>
/// The merge has ten fields and two different notions of "gap" — numeric fields treat any
/// non-positive value as absent, reference fields treat only <see langword="null"/> as absent.
/// Until now it was only exercised incidentally, through CSV and JSON parse fixtures that happen
/// to leave most fields null, so a field wired to the wrong source or the wrong gap test could
/// have shipped unnoticed.
/// </remarks>
public class SdCardConfigurationMergeTests
{
    private static SdCardDeviceConfiguration Empty() => new(
        AnalogPortCount: 0,
        DigitalPortCount: 0,
        TimestampFrequency: 0u,
        DeviceSerialNumber: null,
        DevicePartNumber: null,
        FirmwareRevision: null,
        CalibrationValues: null);

    [Fact]
    public void Merge_WithNullOverride_ReturnsTheParsedConfigurationItself()
    {
        // Arrange — a log parsed with no connected device to fall back on.
        var parsed = Empty() with { AnalogPortCount = 4, DeviceSerialNumber = "SN-FILE" };

        // Act
        var merged = SdCardConfigurationMerge.Merge(parsed, null);

        // Assert — no gap filling to do, and nothing is rebuilt or defaulted along the way.
        Assert.Same(parsed, merged);
    }

    [Fact]
    public void Merge_WhenFileStatesNumerics_OverrideDoesNotWin()
    {
        // Arrange — the file states every numeric itself; the device disagrees about all four.
        var parsed = Empty() with
        {
            AnalogPortCount = 4,
            DigitalPortCount = 2,
            TimestampFrequency = 42_000_000u,
            Resolution = 4095u
        };
        var overrideConfig = Empty() with
        {
            AnalogPortCount = 16,
            DigitalPortCount = 8,
            TimestampFrequency = 50_000_000u,
            Resolution = 65_535u
        };

        // Act
        var merged = SdCardConfigurationMerge.Merge(parsed, overrideConfig);

        // Assert — the log describes itself; a live device never overrides better information.
        Assert.Equal(4, merged.AnalogPortCount);
        Assert.Equal(2, merged.DigitalPortCount);
        Assert.Equal(42_000_000u, merged.TimestampFrequency);
        Assert.Equal(4095u, merged.Resolution);
    }

    [Fact]
    public void Merge_WhenFileStatesNoNumerics_OverrideFillsEachSlot()
    {
        // Arrange — the JSON parser's real shape: it infers almost nothing, so the numerics
        // arrive as zero and every one of them must come from the device snapshot.
        var parsed = Empty();
        var overrideConfig = Empty() with
        {
            AnalogPortCount = 16,
            DigitalPortCount = 8,
            TimestampFrequency = 50_000_000u,
            Resolution = 65_535u
        };

        // Act
        var merged = SdCardConfigurationMerge.Merge(parsed, overrideConfig);

        // Assert — each zero is filled from its own matching field, not a neighbouring one.
        Assert.Equal(16, merged.AnalogPortCount);
        Assert.Equal(8, merged.DigitalPortCount);
        Assert.Equal(50_000_000u, merged.TimestampFrequency);
        Assert.Equal(65_535u, merged.Resolution);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Merge_WhenFilePortCountsAreNegative_OverrideFillsThem(int negativeCount)
    {
        // Arrange — the port counts are signed, so a garbled header can produce a negative count.
        // The guard is "> 0", not "!= 0", so a negative count counts as absent rather than being
        // carried through into channel indexing.
        var parsed = Empty() with { AnalogPortCount = negativeCount, DigitalPortCount = negativeCount };
        var overrideConfig = Empty() with { AnalogPortCount = 16, DigitalPortCount = 8 };

        // Act
        var merged = SdCardConfigurationMerge.Merge(parsed, overrideConfig);

        // Assert
        Assert.Equal(16, merged.AnalogPortCount);
        Assert.Equal(8, merged.DigitalPortCount);
    }

    [Fact]
    public void Merge_WhenFileStatesNoReferenceFields_EachOverrideValueLandsInItsOwnSlot()
    {
        // Arrange — deliberately distinct values per slot so a transposed argument in the merge
        // (serial into part number, port range into internal scale) cannot pass unnoticed.
        var calibration = new[] { (Slope: 1.5, Intercept: 0.25) };
        var portRange = new[] { 10.0 };
        var internalScale = new[] { 2.0 };
        var overrideConfig = Empty() with
        {
            DeviceSerialNumber = "SN-DEVICE",
            DevicePartNumber = "PN-DEVICE",
            FirmwareRevision = "1.2.3",
            CalibrationValues = calibration,
            PortRange = portRange,
            InternalScaleM = internalScale
        };

        // Act
        var merged = SdCardConfigurationMerge.Merge(Empty(), overrideConfig);

        // Assert
        Assert.Equal("SN-DEVICE", merged.DeviceSerialNumber);
        Assert.Equal("PN-DEVICE", merged.DevicePartNumber);
        Assert.Equal("1.2.3", merged.FirmwareRevision);
        Assert.Same(calibration, merged.CalibrationValues);
        Assert.Same(portRange, merged.PortRange);
        Assert.Same(internalScale, merged.InternalScaleM);
    }

    [Fact]
    public void Merge_WhenFileStatesReferenceFields_OverrideDoesNotReplaceThem()
    {
        // Arrange — both sides supply every reference field, with different values.
        var fileCalibration = new[] { (Slope: 1.0, Intercept: 0.0) };
        var filePortRange = new[] { 5.0 };
        var fileInternalScale = new[] { 1.0 };
        var parsed = Empty() with
        {
            DeviceSerialNumber = "SN-FILE",
            DevicePartNumber = "PN-FILE",
            FirmwareRevision = "0.9.0",
            CalibrationValues = fileCalibration,
            PortRange = filePortRange,
            InternalScaleM = fileInternalScale
        };
        var overrideConfig = Empty() with
        {
            DeviceSerialNumber = "SN-DEVICE",
            DevicePartNumber = "PN-DEVICE",
            FirmwareRevision = "1.2.3",
            CalibrationValues = new[] { (Slope: 9.0, Intercept: 9.0) },
            PortRange = new[] { 99.0 },
            InternalScaleM = new[] { 99.0 }
        };

        // Act
        var merged = SdCardConfigurationMerge.Merge(parsed, overrideConfig);

        // Assert — scaling a log with the wrong board's calibration is exactly the failure this
        // precedence rule exists to prevent.
        Assert.Equal("SN-FILE", merged.DeviceSerialNumber);
        Assert.Equal("PN-FILE", merged.DevicePartNumber);
        Assert.Equal("0.9.0", merged.FirmwareRevision);
        Assert.Same(fileCalibration, merged.CalibrationValues);
        Assert.Same(filePortRange, merged.PortRange);
        Assert.Same(fileInternalScale, merged.InternalScaleM);
    }

    [Fact]
    public void Merge_WithGapsOnBothSides_ComposesTheResultFieldByField()
    {
        // Arrange — the CSV parser's real shape: the header names the device and its channel
        // layout, the connected device supplies the calibration and clock the file never wrote.
        var calibration = new[] { (Slope: 1.5, Intercept: 0.25) };
        var parsed = Empty() with
        {
            AnalogPortCount = 4,
            DigitalPortCount = 1,
            DeviceSerialNumber = "7E2815916200E898",
            DevicePartNumber = "Nyquist 1"
        };
        var overrideConfig = Empty() with
        {
            AnalogPortCount = 16,
            TimestampFrequency = 42_000_000u,
            Resolution = 4095u,
            FirmwareRevision = "3.7.2",
            CalibrationValues = calibration
        };

        // Act
        var merged = SdCardConfigurationMerge.Merge(parsed, overrideConfig);

        // Assert — neither input alone describes the session; the merge is what makes it parseable.
        Assert.Equal(4, merged.AnalogPortCount);
        Assert.Equal(1, merged.DigitalPortCount);
        Assert.Equal("7E2815916200E898", merged.DeviceSerialNumber);
        Assert.Equal("Nyquist 1", merged.DevicePartNumber);
        Assert.Equal(42_000_000u, merged.TimestampFrequency);
        Assert.Equal(4095u, merged.Resolution);
        Assert.Equal("3.7.2", merged.FirmwareRevision);
        Assert.Same(calibration, merged.CalibrationValues);
        Assert.Null(merged.PortRange);
        Assert.Null(merged.InternalScaleM);
    }

    [Fact]
    public void Merge_WithAnEmptyOverride_AddsNothing()
    {
        // Arrange — SdCardDeviceConfiguration.FromDevice can hand back a snapshot whose optional
        // fields are all empty; filling a gap with another gap must not invent a value.
        var parsed = Empty() with { AnalogPortCount = 4 };

        // Act
        var merged = SdCardConfigurationMerge.Merge(parsed, Empty());

        // Assert
        Assert.Equal(4, merged.AnalogPortCount);
        Assert.Equal(0, merged.DigitalPortCount);
        Assert.Equal(0u, merged.TimestampFrequency);
        Assert.Equal(0u, merged.Resolution);
        Assert.Null(merged.DeviceSerialNumber);
        Assert.Null(merged.DevicePartNumber);
        Assert.Null(merged.FirmwareRevision);
        Assert.Null(merged.CalibrationValues);
        Assert.Null(merged.PortRange);
        Assert.Null(merged.InternalScaleM);
    }
}
