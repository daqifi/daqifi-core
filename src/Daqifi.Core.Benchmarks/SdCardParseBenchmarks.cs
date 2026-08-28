using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device.SdCard;
using Google.Protobuf;

namespace Daqifi.Core.Benchmarks;

/// <summary>
/// The three SD-card log parsers — protobuf <c>.bin</c>, firmware <c>.csv</c>, and line-delimited
/// <c>.json</c> — over the same logical recording in each format.
/// </summary>
/// <remarks>
/// <para>
/// Two numbers per format, because issue #489 changed one of them and not the other.
/// <c>DrainAll</c> is throughput: how long a full export of the file takes.
/// <c>TimeToFirstSample</c> is latency: how long a caller waits before the first
/// <see cref="SdCardLogEntry"/> comes back. Before #489 every parser materialized the whole file
/// first, so the two numbers were the same; they should now be far apart, and a regression that
/// reintroduces up-front materialization shows up as the latency number climbing towards the
/// throughput one rather than as anything getting slower.
/// </para>
/// <para>
/// The files are synthetic but real-shaped: the same sample count, channel count and tick rate in
/// all three formats, built once in <see cref="Setup"/> and re-read from memory per iteration so
/// the disk is not part of the measurement.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class SdCardParseBenchmarks
{
    /// <summary>
    /// Rows in the synthetic log. About ten seconds of a 1 kHz recording — big enough that the
    /// per-row work dominates the fixed parse setup, small enough to keep an iteration in the
    /// milliseconds.
    /// </summary>
    private const int RowCount = 10_000;

    private const int AnalogChannelCount = 8;

    private const uint TimestampFrequency = 50_000_000;

    private const uint TicksPerRow = 50_000;

    private byte[] _protobufLog = null!;
    private byte[] _csvLog = null!;
    private byte[] _jsonLog = null!;

    [GlobalSetup]
    public void Setup()
    {
        _protobufLog = BuildProtobufLog();
        _csvLog = BuildCsvLog();
        _jsonLog = BuildJsonLog();
    }

    [Benchmark(Baseline = true)]
    public async Task<int> ProtobufDrainAll() => await DrainAllAsync(_protobufLog, "log.bin");

    [Benchmark]
    public async Task<bool> ProtobufTimeToFirstSample() => await FirstSampleAsync(_protobufLog, "log.bin");

    [Benchmark]
    public async Task<int> CsvDrainAll() => await DrainAllAsync(_csvLog, "log.csv");

    [Benchmark]
    public async Task<bool> CsvTimeToFirstSample() => await FirstSampleAsync(_csvLog, "log.csv");

    [Benchmark]
    public async Task<int> JsonDrainAll() => await DrainAllAsync(_jsonLog, "log.json");

    [Benchmark]
    public async Task<bool> JsonTimeToFirstSample() => await FirstSampleAsync(_jsonLog, "log.json");

    private static async Task<int> DrainAllAsync(byte[] log, string fileName)
    {
        using var stream = new MemoryStream(log, writable: false);
        var session = await SdCardFileParserFactory.ParseAsync(stream, fileName);

        var count = 0;
        await foreach (var _ in session.Samples)
        {
            count++;
        }

        return count;
    }

    private static async Task<bool> FirstSampleAsync(byte[] log, string fileName)
    {
        using var stream = new MemoryStream(log, writable: false);
        var session = await SdCardFileParserFactory.ParseAsync(stream, fileName);

        await foreach (var _ in session.Samples)
        {
            return true;
        }

        return false;
    }

    private static byte[] BuildProtobufLog()
    {
        using var buffer = new MemoryStream();

        Append(buffer, new DaqifiOutMessage
        {
            AnalogInPortNum = AnalogChannelCount,
            DigitalPortNum = 0,
            AnalogInRes = 65535,
            TimestampFreq = TimestampFrequency,
            DevicePn = "Nyquist1",
            DeviceFwRev = "3.7.2",
            DeviceSn = 9090539562006014104,
        });

        for (var row = 0; row < RowCount; row++)
        {
            var message = new DaqifiOutMessage { MsgTimeStamp = (uint)(row + 1) * TicksPerRow };
            for (var channel = 0; channel < AnalogChannelCount; channel++)
            {
                message.AnalogInData.Add(1_000 + channel + row % 97);
            }

            Append(buffer, message);
        }

        return buffer.ToArray();

        static void Append(Stream destination, DaqifiOutMessage message)
        {
            var payload = message.ToByteArray();
            var coded = new CodedOutputStream(destination, leaveOpen: true);
            coded.WriteLength(payload.Length);
            coded.Flush();
            destination.Write(payload, 0, payload.Length);
        }
    }

    private static byte[] BuildCsvLog()
    {
        // The firmware's own CSV shape: three comment lines, then interleaved per-channel
        // (timestamp, raw ADC value) column pairs.
        var text = new StringBuilder();
        text.Append("# Device: Nyquist 1\n");
        text.Append("# Serial Number: AABBCCDDEEFF0011\n");
        text.Append(CultureInfo.InvariantCulture, $"# Timestamp Tick Rate: {TimestampFrequency} Hz\n");

        for (var channel = 0; channel < AnalogChannelCount; channel++)
        {
            text.Append(CultureInfo.InvariantCulture, $"ch{channel}_ts,ch{channel}_val");
            text.Append(channel == AnalogChannelCount - 1 ? '\n' : ',');
        }

        for (var row = 0; row < RowCount; row++)
        {
            var timestamp = (uint)(row + 1) * TicksPerRow;
            for (var channel = 0; channel < AnalogChannelCount; channel++)
            {
                var value = 1_000 + channel + row % 97;
                text.Append(CultureInfo.InvariantCulture, $"{timestamp},{value}.000000");
                text.Append(channel == AnalogChannelCount - 1 ? '\n' : ',');
            }
        }

        return new UTF8Encoding(false).GetBytes(text.ToString());
    }

    private static byte[] BuildJsonLog()
    {
        // One JSON object per line, which is what the firmware writes.
        var text = new StringBuilder();

        for (var row = 0; row < RowCount; row++)
        {
            var timestamp = (uint)(row + 1) * TicksPerRow;
            text.Append(CultureInfo.InvariantCulture, $"{{\"ts\":{timestamp},\"analog\":[");

            for (var channel = 0; channel < AnalogChannelCount; channel++)
            {
                var value = 1_000 + channel + row % 97;
                text.Append(CultureInfo.InvariantCulture, $"{value}.000000");
                if (channel != AnalogChannelCount - 1)
                {
                    text.Append(',');
                }
            }

            text.Append("],\"digital\":\"00\"}\n");
        }

        return new UTF8Encoding(false).GetBytes(text.ToString());
    }
}
