using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Parses SD card log <c>.bin</c> files containing varint32-prefixed
/// <see cref="DaqifiOutMessage"/> protobuf payloads.
/// </summary>
public sealed class SdCardFileParser
{
    /// <summary>
    /// Marker bytes written at the end of a USB-transferred file.
    /// </summary>
    private static readonly byte[] EndOfFileMarker =
        Encoding.ASCII.GetBytes("__END_OF_FILE__");

    /// <summary>
    /// Parses an SD card log file from a <see cref="Stream"/>.
    /// </summary>
    /// <param name="fileStream">A readable stream containing the binary log data.</param>
    /// <param name="fileName">The file name (used for metadata and date extraction).</param>
    /// <param name="options">Optional parse options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="SdCardLogSession"/> providing lazy access to sample data.</returns>
    public async Task<SdCardLogSession> ParseAsync(
        Stream fileStream,
        string fileName,
        SdCardParseOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileStream);
        ArgumentNullException.ThrowIfNull(fileName);

        options ??= new SdCardParseOptions();

        if (options.BufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "BufferSize must be greater than zero.");
        }

        var fileCreatedDate = options.SessionStartTime
                              ?? SdCardFileListParser.TryParseDateFromLogFileName(fileName);

        // Read all messages from the stream.
        var allMessages = await ReadAllMessagesAsync(fileStream, options, ct).ConfigureAwait(false);

        SdCardDeviceConfiguration? config = null;
        var streamStartIndex = 0;

        // First, check if the first message is a dedicated status message.
        // Some firmware versions embed config fields (e.g., AnalogInPortNum/TimestampFreq)
        // inside stream messages, so we only treat the first message as status when it
        // has no stream payload.
        if (allMessages.Count > 0 && !HasStreamPayload(allMessages[0]))
        {
            config = ExtractDeviceConfiguration(allMessages[0]);
            streamStartIndex = 1;
        }

        // If no dedicated status message was found, or the status message had no TimestampFreq,
        // scan all messages for config fields. Device firmware often embeds config fields
        // (TimestampFreq, DeviceSn, etc.) in streaming data messages rather than writing
        // a separate status header.
        if (config == null || config.TimestampFrequency == 0)
        {
            var scannedConfig = ScanMessagesForConfiguration(allMessages);
            if (scannedConfig != null)
            {
                config = MergeConfigurations(config, scannedConfig);
            }
        }

        var timestampFrequency = config?.TimestampFrequency ?? 0u;
        if (timestampFrequency == 0 && options.FallbackTimestampFrequency > 0)
        {
            timestampFrequency = options.FallbackTimestampFrequency;
        }

        var tickPeriod = timestampFrequency > 0
            ? 1.0 / timestampFrequency
            : 0.0;

        var samples = ProduceSamples(
            allMessages,
            streamStartIndex,
            fileCreatedDate,
            tickPeriod,
            ct);

        return new SdCardLogSession(fileName, fileCreatedDate, config, samples);
    }

    /// <summary>
    /// Convenience overload that opens a file by path.
    /// </summary>
    /// <param name="filePath">Absolute or relative path to the <c>.bin</c> file.</param>
    /// <param name="options">Optional parse options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="SdCardLogSession"/> providing lazy access to sample data.</returns>
    public async Task<SdCardLogSession> ParseFileAsync(
        string filePath,
        SdCardParseOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        options ??= new SdCardParseOptions();

        if (options.BufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "BufferSize must be greater than zero.");
        }

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            options.BufferSize,
            useAsync: true);

        return await ParseAsync(stream, Path.GetFileName(filePath), options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads and deserializes all protobuf messages from the stream.
    /// </summary>
    private static async Task<List<DaqifiOutMessage>> ReadAllMessagesAsync(
        Stream stream,
        SdCardParseOptions options,
        CancellationToken ct)
    {
        var parser = new ProtobufMessageParser();
        var messages = new List<DaqifiOutMessage>();
        var buffer = new byte[options.BufferSize];
        var carry = Array.Empty<byte>();
        long totalBytesRead = 0;
        var totalBytes = stream.CanSeek ? stream.Length : -1L;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            totalBytesRead += bytesRead;

            // Combine carry-over bytes from previous chunk with new data
            var chunk = CombineBuffers(carry, buffer, bytesRead);

            // Strip end-of-file marker if present at the tail
            chunk = StripEndOfFileMarker(chunk);

            var parsed = parser.ParseMessages(chunk, out var consumed);

            foreach (var msg in parsed)
            {
                messages.Add(msg.Data);
            }

            // Carry unconsumed bytes forward
            if (consumed < chunk.Length)
            {
                carry = new byte[chunk.Length - consumed];
                Array.Copy(chunk, consumed, carry, 0, carry.Length);
            }
            else
            {
                carry = Array.Empty<byte>();
            }

            options.Progress?.Report(new SdCardParseProgress(totalBytesRead, totalBytes, messages.Count));
        }

        // Try to parse any remaining carry bytes (may contain a partial final message)
        if (carry.Length > 0)
        {
            var parsed = parser.ParseMessages(carry, out _);
            foreach (var msg in parsed)
            {
                messages.Add(msg.Data);
            }

            options.Progress?.Report(new SdCardParseProgress(totalBytesRead, totalBytes, messages.Count));
        }

        return messages;
    }

    /// <summary>
    /// Produces <see cref="SdCardLogEntry"/> samples from the parsed messages.
    /// </summary>
    private static async IAsyncEnumerable<SdCardLogEntry> ProduceSamples(
        List<DaqifiOutMessage> messages,
        int startIndex,
        DateTime? anchorTime,
        double tickPeriod,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var baseTime = anchorTime ?? DateTime.UtcNow;
        uint? previousTimestamp = null;
        var elapsedSeconds = 0.0;

        for (var i = startIndex; i < messages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var msg = messages[i];

            // Skip non-stream messages
            if (msg.AnalogInData.Count == 0 &&
                msg.AnalogInDataFloat.Count == 0 &&
                msg.DigitalData.Length == 0)
            {
                continue;
            }

            // Reconstruct timestamp
            var timestamp = baseTime;
            if (msg.MsgTimeStamp != 0 && tickPeriod > 0)
            {
                if (previousTimestamp == null)
                {
                    // First stream message — anchor to base time
                    previousTimestamp = msg.MsgTimeStamp;
                }
                else
                {
                    var delta = ComputeTickDelta(previousTimestamp.Value, msg.MsgTimeStamp);
                    elapsedSeconds += delta * tickPeriod;
                    previousTimestamp = msg.MsgTimeStamp;
                }

                timestamp = baseTime.AddSeconds(elapsedSeconds);
            }

            // Extract analog values (prefer float, fall back to raw int)
            var analogValues = msg.AnalogInDataFloat.Count > 0
                ? msg.AnalogInDataFloat.Select(v => (double)v).ToArray()
                : msg.AnalogInData.Select(v => (double)v).ToArray();

            // Extract digital data
            var digitalData = 0u;
            if (msg.DigitalData.Length > 0)
            {
                var bytes = msg.DigitalData.ToByteArray();
                for (var b = 0; b < bytes.Length && b < 4; b++)
                {
                    digitalData |= (uint)bytes[b] << (b * 8);
                }
            }

            // Per-channel timestamps
            IReadOnlyList<uint>? analogTimestamps = msg.AnalogInDataTs.Count > 0
                ? msg.AnalogInDataTs.ToArray()
                : null;

            yield return new SdCardLogEntry(timestamp, analogValues, digitalData, analogTimestamps);
        }

        await Task.CompletedTask; // keep the method async-compatible
    }

    /// <summary>
    /// Computes the tick delta between two uint32 timestamps, handling rollover.
    /// </summary>
    private static long ComputeTickDelta(uint previous, uint current)
    {
        if (current >= previous)
        {
            return current - previous;
        }

        // Rollover: ticks remaining to max + current
        return (long)(uint.MaxValue - previous) + current + 1;
    }

    /// <summary>
    /// Determines whether a message contains stream sample payload fields.
    /// </summary>
    private static bool HasStreamPayload(DaqifiOutMessage message)
    {
        return message.AnalogInData.Count > 0 ||
               message.AnalogInDataFloat.Count > 0 ||
               message.DigitalData.Length > 0;
    }

    /// <summary>
    /// Scans all messages for device configuration fields that may be embedded in streaming
    /// data messages. Returns a merged configuration from the first non-zero value found
    /// for each field, or null if no config fields are found in any message.
    /// </summary>
    private static SdCardDeviceConfiguration? ScanMessagesForConfiguration(List<DaqifiOutMessage> messages)
    {
        uint timestampFreq = 0;
        uint analogPortNum = 0;
        uint digitalPortNum = 0;
        ulong deviceSn = 0;
        string? devicePn = null;
        string? fwRev = null;
        IReadOnlyList<(double Slope, double Intercept)>? calibration = null;

        foreach (var msg in messages)
        {
            if (timestampFreq == 0 && msg.TimestampFreq != 0)
            {
                timestampFreq = msg.TimestampFreq;
            }

            if (analogPortNum == 0 && msg.AnalogInPortNum != 0)
            {
                analogPortNum = msg.AnalogInPortNum;
            }

            if (digitalPortNum == 0 && msg.DigitalPortNum != 0)
            {
                digitalPortNum = msg.DigitalPortNum;
            }

            if (deviceSn == 0 && msg.DeviceSn != 0)
            {
                deviceSn = msg.DeviceSn;
            }

            if (devicePn == null && !string.IsNullOrEmpty(msg.DevicePn))
            {
                devicePn = msg.DevicePn;
            }

            if (fwRev == null && !string.IsNullOrEmpty(msg.DeviceFwRev))
            {
                fwRev = msg.DeviceFwRev;
            }

            if (calibration == null && msg.AnalogInCalM.Count > 0 && msg.AnalogInCalB.Count > 0)
            {
                var count = Math.Min(msg.AnalogInCalM.Count, msg.AnalogInCalB.Count);
                var cal = new (double, double)[count];
                for (var i = 0; i < count; i++)
                {
                    cal[i] = (msg.AnalogInCalM[i], msg.AnalogInCalB[i]);
                }

                calibration = cal;
            }

            // If we've found all fields, stop scanning
            if (timestampFreq != 0 && analogPortNum != 0 && digitalPortNum != 0 &&
                deviceSn != 0 && devicePn != null && fwRev != null && calibration != null)
            {
                break;
            }
        }

        // Only return a config if we found at least one meaningful field
        if (timestampFreq == 0 && analogPortNum == 0 && digitalPortNum == 0 &&
            deviceSn == 0 && devicePn == null && fwRev == null && calibration == null)
        {
            return null;
        }

        return new SdCardDeviceConfiguration(
            AnalogPortCount: (int)analogPortNum,
            DigitalPortCount: (int)digitalPortNum,
            TimestampFrequency: timestampFreq,
            DeviceSerialNumber: deviceSn != 0 ? deviceSn.ToString() : null,
            DevicePartNumber: devicePn,
            FirmwareRevision: fwRev,
            CalibrationValues: calibration);
    }

    /// <summary>
    /// Merges two configurations, preferring non-zero/non-null values from the primary config,
    /// falling back to values from the scanned config.
    /// </summary>
    private static SdCardDeviceConfiguration MergeConfigurations(
        SdCardDeviceConfiguration? primary,
        SdCardDeviceConfiguration scanned)
    {
        if (primary == null)
        {
            return scanned;
        }

        return new SdCardDeviceConfiguration(
            AnalogPortCount: primary.AnalogPortCount != 0 ? primary.AnalogPortCount : scanned.AnalogPortCount,
            DigitalPortCount: primary.DigitalPortCount != 0 ? primary.DigitalPortCount : scanned.DigitalPortCount,
            TimestampFrequency: primary.TimestampFrequency != 0 ? primary.TimestampFrequency : scanned.TimestampFrequency,
            DeviceSerialNumber: primary.DeviceSerialNumber ?? scanned.DeviceSerialNumber,
            DevicePartNumber: !string.IsNullOrEmpty(primary.DevicePartNumber)
                ? primary.DevicePartNumber
                : scanned.DevicePartNumber,
            FirmwareRevision: !string.IsNullOrEmpty(primary.FirmwareRevision)
                ? primary.FirmwareRevision
                : scanned.FirmwareRevision,
            CalibrationValues: primary.CalibrationValues ?? scanned.CalibrationValues);
    }

    /// <summary>
    /// Extracts device configuration from a status message.
    /// </summary>
    private static SdCardDeviceConfiguration ExtractDeviceConfiguration(DaqifiOutMessage statusMessage)
    {
        // Extract calibration values if present
        IReadOnlyList<(double Slope, double Intercept)>? calibration = null;
        if (statusMessage.AnalogInCalM.Count > 0 && statusMessage.AnalogInCalB.Count > 0)
        {
            var count = Math.Min(statusMessage.AnalogInCalM.Count, statusMessage.AnalogInCalB.Count);
            var cal = new (double, double)[count];
            for (var i = 0; i < count; i++)
            {
                cal[i] = (statusMessage.AnalogInCalM[i], statusMessage.AnalogInCalB[i]);
            }

            calibration = cal;
        }

        return new SdCardDeviceConfiguration(
            AnalogPortCount: (int)statusMessage.AnalogInPortNum,
            DigitalPortCount: (int)statusMessage.DigitalPortNum,
            TimestampFrequency: statusMessage.TimestampFreq,
            DeviceSerialNumber: statusMessage.DeviceSn != 0 ? statusMessage.DeviceSn.ToString() : null,
            DevicePartNumber: statusMessage.DevicePn,
            FirmwareRevision: statusMessage.DeviceFwRev,
            CalibrationValues: calibration);
    }

    /// <summary>
    /// Combines carry-over bytes with newly read data into a single array.
    /// </summary>
    private static byte[] CombineBuffers(byte[] carry, byte[] buffer, int bytesRead)
    {
        if (carry.Length == 0)
        {
            var result = new byte[bytesRead];
            Array.Copy(buffer, 0, result, 0, bytesRead);
            return result;
        }

        var combined = new byte[carry.Length + bytesRead];
        Array.Copy(carry, 0, combined, 0, carry.Length);
        Array.Copy(buffer, 0, combined, carry.Length, bytesRead);
        return combined;
    }

    /// <summary>
    /// Strips the <c>__END_OF_FILE__</c> marker if it appears at the tail of the data.
    /// </summary>
    private static byte[] StripEndOfFileMarker(byte[] data)
    {
        if (data.Length < EndOfFileMarker.Length)
        {
            return data;
        }

        var tailStart = data.Length - EndOfFileMarker.Length;
        for (var i = 0; i < EndOfFileMarker.Length; i++)
        {
            if (data[tailStart + i] != EndOfFileMarker[i])
            {
                return data;
            }
        }

        var trimmed = new byte[tailStart];
        Array.Copy(data, 0, trimmed, 0, tailStart);
        return trimmed;
    }
}
