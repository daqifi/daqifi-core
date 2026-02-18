using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Daqifi.Core.Tests.Device.SdCard;

/// <summary>
/// Helper class for building test JSON log files for unit tests.
/// </summary>
internal static class SdCardTestJsonFileBuilder
{
    /// <summary>
    /// Builds a JSON log file stream from sample data.
    /// </summary>
    /// <param name="lines">Array of tuples containing (timestamp, analog values, digital hex string).</param>
    /// <returns>A <see cref="MemoryStream"/> positioned at the beginning containing the JSON data.</returns>
    public static MemoryStream BuildJsonFile(params (uint ts, double[] analog, string digital)[] lines)
    {
        var ms = new MemoryStream();
        var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);

        foreach (var (ts, analog, digital) in lines)
        {
            var analogStr = string.Join(",", analog.Select(v => v.ToString("F6", CultureInfo.InvariantCulture)));
            var line = $"{{\"ts\":{ts.ToString(CultureInfo.InvariantCulture)},\"analog\":[{analogStr}],\"digital\":\"{digital}\"}}";
            writer.WriteLine(line);
        }

        writer.Flush();
        ms.Position = 0;
        return ms;
    }

    /// <summary>
    /// Builds a JSON log file with integer analog values instead of floats.
    /// </summary>
    public static MemoryStream BuildJsonFileWithIntegers(params (uint ts, int[] analog, string digital)[] lines)
    {
        var ms = new MemoryStream();
        var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);

        foreach (var (ts, analog, digital) in lines)
        {
            var analogStr = string.Join(",", analog.Select(v => v.ToString(CultureInfo.InvariantCulture)));
            var line = $"{{\"ts\":{ts.ToString(CultureInfo.InvariantCulture)},\"analog\":[{analogStr}],\"digital\":\"{digital}\"}}";
            writer.WriteLine(line);
        }

        writer.Flush();
        ms.Position = 0;
        return ms;
    }
}

/// <summary>
/// Helper class for building test CSV log files in the real DAQiFi firmware format.
/// <para>
/// The firmware CSV format is:
/// <list type="bullet">
///   <item><description># Device: {deviceName}</description></item>
///   <item><description># Serial Number: {serialNumber}</description></item>
///   <item><description># Timestamp Tick Rate: {freq} Hz</description></item>
///   <item><description>ch0_ts,ch0_val,ch1_ts,ch1_val,...</description></item>
///   <item><description>Data rows: interleaved per-channel (timestamp_ticks, adc_raw_value) pairs</description></item>
/// </list>
/// </para>
/// </summary>
internal static class SdCardTestCsvFileBuilder
{
    /// <summary>
    /// Builds a CSV log file stream from sample data in the real DAQiFi firmware format.
    /// </summary>
    /// <param name="channelCount">Number of analog channels.</param>
    /// <param name="deviceName">Device name for header (e.g. "Nyquist 1").</param>
    /// <param name="serialNumber">Serial number for header (e.g. "AABBCCDDEEFF0011").</param>
    /// <param name="timestampFreq">Timestamp tick rate in Hz for header.</param>
    /// <param name="rows">
    /// Array of rows, each containing per-channel (timestamp_ticks, adc_raw_value) pairs.
    /// e.g. new[] { (1000u, 15.0), (1000u, 22.0) } means ch0 ts=1000 val=15, ch1 ts=1000 val=22.
    /// </param>
    /// <returns>A <see cref="MemoryStream"/> positioned at the beginning containing the CSV data.</returns>
    public static MemoryStream BuildCsvFile(
        int channelCount,
        string deviceName,
        string serialNumber,
        uint timestampFreq,
        params (uint ts, double val)[][] rows)
    {
        var ms = new MemoryStream();
        var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);

        // Comment header lines
        writer.WriteLine($"# Device: {deviceName}");
        writer.WriteLine($"# Serial Number: {serialNumber}");
        writer.WriteLine($"# Timestamp Tick Rate: {timestampFreq} Hz");

        // Column header
        var headerCols = Enumerable.Range(0, channelCount)
            .SelectMany(ch => new[] { $"ch{ch}_ts", $"ch{ch}_val" });
        writer.WriteLine(string.Join(",", headerCols));

        // Data rows
        foreach (var row in rows)
        {
            var cols = row.SelectMany(pair => new[]
            {
                pair.ts.ToString(CultureInfo.InvariantCulture),
                pair.val.ToString("F6", CultureInfo.InvariantCulture)
            });
            writer.WriteLine(string.Join(",", cols));
        }

        writer.Flush();
        ms.Position = 0;
        return ms;
    }

    /// <summary>
    /// Builds a CSV log file with a simplified signature for single-timestamp-per-row data
    /// where all channels share the same timestamp (common in firmware output).
    /// </summary>
    /// <param name="deviceName">Device name.</param>
    /// <param name="serialNumber">Serial number.</param>
    /// <param name="timestampFreq">Tick rate in Hz.</param>
    /// <param name="rows">Array of (sharedTimestamp, analogValues[]) tuples.</param>
    public static MemoryStream BuildCsvFileSharedTimestamp(
        string deviceName,
        string serialNumber,
        uint timestampFreq,
        params (uint ts, double[] analog)[] rows)
    {
        var channelCount = rows.Length > 0 ? rows[0].analog.Length : 0;
        var perChannelRows = rows
            .Select(r => r.analog.Select((v, i) => (r.ts, v)).ToArray())
            .ToArray();
        return BuildCsvFile(channelCount, deviceName, serialNumber, timestampFreq, perChannelRows);
    }

    /// <summary>
    /// Builds a minimal CSV file (no comment headers) with raw data rows only,
    /// for testing format fallback / graceful degradation.
    /// </summary>
    public static MemoryStream BuildCsvFileNoHeaders(params (uint ts, double[] analog)[] rows)
    {
        var channelCount = rows.Length > 0 ? rows[0].analog.Length : 0;
        var ms = new MemoryStream();
        var writer = new StreamWriter(ms, new UTF8Encoding(false), leaveOpen: true);

        // Column header only (no # comment lines)
        var headerCols = Enumerable.Range(0, channelCount)
            .SelectMany(ch => new[] { $"ch{ch}_ts", $"ch{ch}_val" });
        writer.WriteLine(string.Join(",", headerCols));

        foreach (var (ts, analog) in rows)
        {
            var cols = analog.SelectMany((v, i) => new[]
            {
                ts.ToString(CultureInfo.InvariantCulture),
                v.ToString("F6", CultureInfo.InvariantCulture)
            });
            writer.WriteLine(string.Join(",", cols));
        }

        writer.Flush();
        ms.Position = 0;
        return ms;
    }
}
