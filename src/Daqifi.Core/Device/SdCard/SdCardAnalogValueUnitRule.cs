using System;
using System.Collections.Generic;
using System.IO;

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Decides what unit a text log's analog values are in, and converts them to volts. Shared by
/// the CSV and JSON paths so the two can never disagree about the same reading.
/// </summary>
/// <remarks>
/// <para>
/// The rule reads the unit from the file rather than assuming every value is a raw ADC code.
/// Firmware writes a reading in one of three forms and the file does not name the one it used
/// (daqifi/daqifi-nyquist-firmware#1179), but the lexical form of the value separates the volts
/// case from the other two: <c>csv_encoder.c</c> and <c>JSON_Encoder.c</c> write volts with a
/// decimal point, and write both raw ADC codes and millivolts as whole numbers.
/// </para>
/// <para>
/// So: a value with a decimal point or an exponent is volts. A whole number is a raw ADC code
/// or millivolts, and <see cref="SdCardParseOptions.IntegerValueUnits"/> says which. A row that
/// mixes the two forms states no single unit at all, and that is an error rather than a guess —
/// a reading that is wrong by a factor of 1000 is worse than a refused file.
/// </para>
/// </remarks>
internal static class SdCardAnalogValueUnitRule
{
    /// <summary>
    /// Reports whether a value token is written in the volts form: with a decimal point or an
    /// exponent. The test is on the text, not on the parsed number, because <c>245</c> and
    /// <c>245.0</c> parse to the same <c>double</c> while meaning different units.
    /// </summary>
    /// <param name="token">One value token, as the file wrote it.</param>
    /// <returns><c>true</c> for the volts form; <c>false</c> for a whole number.</returns>
    internal static bool IsDecimalForm(string token) =>
        token.AsSpan().IndexOfAny('.', 'e', 'E') >= 0;

    /// <summary>
    /// Decides one row's unit from how many of its values are written in the volts form.
    /// </summary>
    /// <param name="valueCount">How many analog values the row holds.</param>
    /// <param name="decimalFormCount">How many of them are written in the volts form.</param>
    /// <param name="integerValueUnits">
    /// What the caller says a whole number means. <c>null</c> means the caller did not say, and
    /// the row is then read as raw ADC codes, which is what every release before this one did.
    /// </param>
    /// <param name="line">The row, quoted in the exception message for a mixed row.</param>
    /// <returns>The unit to apply to the row.</returns>
    /// <exception cref="InvalidDataException">
    /// The row mixes the volts form and the whole-number form, so no single unit covers it.
    /// </exception>
    internal static SdCardAnalogValueUnit ResolveRowUnit(
        int valueCount,
        int decimalFormCount,
        SdCardIntegerValueUnits? integerValueUnits,
        string line)
    {
        if (decimalFormCount == valueCount && valueCount > 0)
        {
            return SdCardAnalogValueUnit.Volts;
        }

        if (decimalFormCount > 0)
        {
            throw new InvalidDataException(
                $"SD card log row '{line}' mixes values written with a decimal point with " +
                "whole numbers, so no single unit covers the row. Firmware writes one form for " +
                "every channel in a recording, so this file is not one it produced. Reading " +
                "the row either way would report a voltage wrong by a factor of 1000 or more.");
        }

        return integerValueUnits == SdCardIntegerValueUnits.Millivolts
            ? SdCardAnalogValueUnit.Millivolts
            : SdCardAnalogValueUnit.RawAdcCodes;
    }

    /// <summary>
    /// Converts whole-number millivolts to volts. The device has already applied its
    /// calibration to these values, so this divides by 1000 and does nothing else.
    /// </summary>
    /// <param name="millivolts">The row's values, in millivolts.</param>
    /// <returns>The same readings, in volts.</returns>
    internal static IReadOnlyList<double> MillivoltsToVolts(IReadOnlyList<double> millivolts)
    {
        var result = new double[millivolts.Count];
        for (var i = 0; i < millivolts.Count; i++)
        {
            result[i] = millivolts[i] / 1000.0;
        }

        return result;
    }
}
