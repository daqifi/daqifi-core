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
    /// <remarks>
    /// The session reads <paramref name="fileStream"/> lazily: keep the stream open and do not
    /// read from it yourself until you have finished enumerating
    /// <see cref="SdCardLogSession.Samples"/>. A seekable stream is re-read from its starting
    /// position on each enumeration, and only one enumeration may be in flight at a time; a
    /// forward-only stream cannot be re-read, so its contents are decoded up front instead.
    /// </remarks>
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

        var source = SdCardParseSource.TryCreate(fileStream);
        if (source != null)
        {
            return await BuildSessionAsync(
                (progress, token) => ReadMessagesAsync(source, options, progress, token),
                fileName,
                options,
                ct).ConfigureAwait(false);
        }

        // A forward-only stream can only be read once, so it has to be decoded up front.
        // Callers that want the streaming parse hand the parser a seekable stream or a path.
        var buffered = new List<DaqifiOutMessage>();
        await foreach (var message in ReadMessagesAsync(fileStream, options, options.Progress, -1L, ct)
                           .WithCancellation(ct).ConfigureAwait(false))
        {
            buffered.Add(message);
        }

        return await BuildSessionAsync(
            (_, _) => SdCardTextLineReader.ToAsyncEnumerable(buffered),
            fileName,
            options,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Convenience overload that opens a file by path.
    /// </summary>
    /// <param name="filePath">Absolute or relative path to the <c>.bin</c> file.</param>
    /// <param name="options">Optional parse options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="SdCardLogSession"/> providing lazy access to sample data.</returns>
    /// <remarks>
    /// The returned session opens its own read of the file each time
    /// <see cref="SdCardLogSession.Samples"/> is enumerated, so the file must still exist then.
    /// </remarks>
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

        var source = SdCardParseSource.ForFile(filePath, options.BufferSize);

        return await BuildSessionAsync(
            (progress, token) => ReadMessagesAsync(source, options, progress, token),
            Path.GetFileName(filePath),
            options,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the session's configuration from a short prefix of the log, then hands the
    /// session a sample iterator that re-reads the log lazily.
    /// </summary>
    private static async Task<SdCardLogSession> BuildSessionAsync(
        Func<IProgress<SdCardParseProgress>?, CancellationToken, IAsyncEnumerable<DaqifiOutMessage>> openMessages,
        string fileName,
        SdCardParseOptions options,
        CancellationToken ct)
    {
        var fileCreatedDate = options.SessionStartTime
                              ?? SdCardFileListParser.TryParseDateFromLogFileName(fileName);

        // Only the leading messages are examined, never the whole file: firmware states the
        // configuration in the status message or in the first handful of stream messages.
        // No progress is reported for this pass — it is not the caller's read of the file.
        //
        // The scan reads only as far as it has to. It used to read the whole
        // ConfigurationScanMessageLimit window every time and decide afterwards, so the common
        // case — a status message that states everything, which is then used on its own —
        // decoded 512 messages and discarded 511 of them before the caller saw a sample
        // (issue #697). Each break below is a point at which no later message can change the
        // configuration that comes out of this pass.
        SdCardDeviceConfiguration? statusConfig = null;
        var scanner = new ConfigurationScanner();
        var messagesScanned = 0;
        var limit = options.ConfigurationScanMessageLimit;

        await foreach (var message in openMessages(null, ct).WithCancellation(ct).ConfigureAwait(false))
        {
            // Some firmware versions embed config fields (e.g., AnalogInPortNum/TimestampFreq)
            // inside stream messages, so we only treat the first message as a dedicated status
            // message when it has no stream payload.
            if (messagesScanned == 0 && !HasStreamPayload(message))
            {
                statusConfig = ExtractDeviceConfiguration(message);
            }

            messagesScanned++;
            scanner.Add(message);

            // A status message that stated the timestamp clock settles it: the embedded-field
            // scan below is skipped entirely in that case, so what the rest of the file says is
            // never consulted and there is nothing left to read for.
            if (statusConfig is { TimestampFrequency: not 0 })
            {
                break;
            }

            // Every field the scan can find has been found.
            if (scanner.IsComplete)
            {
                break;
            }

            if (limit > 0 && messagesScanned >= limit)
            {
                break;
            }
        }

        var config = statusConfig;

        // If no dedicated status message was found, or the status message had no TimestampFreq,
        // use the config fields scanned out of the leading messages. Device firmware often embeds
        // config fields (TimestampFreq, DeviceSn, etc.) in streaming data messages rather than
        // writing a separate status header.
        if (config == null || config.TimestampFrequency == 0)
        {
            var scannedConfig = scanner.ToConfiguration();
            if (scannedConfig != null)
            {
                config = MergeConfigurations(config, scannedConfig);
            }
        }

        // Whatever the file itself stated, captured before the override is merged in so the
        // two sources stay distinguishable when reporting which one was used.
        var fileTimestampFrequency = config?.TimestampFrequency ?? 0u;

        // Apply ConfigurationOverride as a fallback for any fields not found in the file.
        // This is useful when the device is connected during download — the device's live
        // status provides calibration, resolution, port range, and timestamp clock values
        // that may not be embedded in the SD card log file.
        if (options.ConfigurationOverride != null)
        {
            config = MergeConfigurations(config, options.ConfigurationOverride);
        }

        var (timestampFrequency, timestampSource) = SdCardTimestampFrequencyResolver.Resolve(
            fileTimestampFrequency,
            options.ConfigurationOverride?.TimestampFrequency ?? 0u,
            options.FallbackTimestampFrequency);

        var samples = ProduceSamples(
            token => openMessages(options.Progress, token),
            // Anchored once, here, rather than inside the iterator: a session whose samples can
            // be enumerated more than once must not shift its timestamps between reads when the
            // file name carries no date.
            fileCreatedDate ?? DateTime.UtcNow,
            timestampFrequency,
            config,
            ct);

        return new SdCardLogSession(fileName, fileCreatedDate, config, samples)
        {
            TimestampFrequency = timestampFrequency,
            TimestampFrequencySource = timestampSource
        };
    }

    /// <summary>
    /// Streams the protobuf messages of the source, re-opening it from the start each time.
    /// </summary>
    private static async IAsyncEnumerable<DaqifiOutMessage> ReadMessagesAsync(
        SdCardParseSource source,
        SdCardParseOptions options,
        IProgress<SdCardParseProgress>? progress,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var lease = source.Open();
        await using (lease.ConfigureAwait(false))
        {
            await foreach (var message in ReadMessagesAsync(lease.Stream, options, progress, source.TotalBytes, ct)
                               .WithCancellation(ct).ConfigureAwait(false))
            {
                yield return message;
            }
        }
    }

    /// <summary>
    /// Deserializes protobuf messages from the stream, holding at most one read buffer plus
    /// the bytes of a straddling frame.
    /// </summary>
    /// <remarks>
    /// Each frame is handed on as it is decoded rather than after its whole chunk has been
    /// parsed. Parsing the chunk first made the caller's first sample cost every frame in the
    /// leading <see cref="SdCardParseOptions.BufferSize"/> bytes — about 1,300 of them at the
    /// 64 KB default — which is what made this parser two to three orders of magnitude slower
    /// to its first sample than the CSV and JSON parsers (issue #697).
    /// </remarks>
    private static async IAsyncEnumerable<DaqifiOutMessage> ReadMessagesAsync(
        Stream stream,
        SdCardParseOptions options,
        IProgress<SdCardParseProgress>? progress,
        long totalBytes,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var parser = new ProtobufMessageParser();
        var buffer = new byte[options.BufferSize];
        var carry = Array.Empty<byte>();
        long totalBytesRead = 0;
        var messagesRead = 0;

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

            var consumed = 0;
            var framesFromChunk = 0;

            while (true)
            {
                // The parser only applies its leading-noise recovery before the first frame of
                // a buffer, so framesFromChunk has to be per-chunk — exactly what the count of
                // a whole-chunk parse used to say.
                var step = parser.TryReadFrame(chunk, consumed, framesFromChunk > 0, out var frame, out var nextOffset);
                if (step == ProtobufFrameStep.EndOfBuffer)
                {
                    break;
                }

                consumed = nextOffset;

                if (step != ProtobufFrameStep.Frame)
                {
                    continue;
                }

                framesFromChunk++;
                messagesRead++;
                yield return frame!;
            }

            // Carry the bytes the parser did not consume — a frame straddling this chunk and
            // the next. The inner loop has fully drained the chunk by now, so the reader's
            // state is consistent again before the next read.
            if (consumed < chunk.Length)
            {
                carry = new byte[chunk.Length - consumed];
                Array.Copy(chunk, consumed, carry, 0, carry.Length);
            }
            else
            {
                carry = Array.Empty<byte>();
            }

            progress?.Report(new SdCardParseProgress(totalBytesRead, totalBytes, messagesRead));
        }

        // Try to parse any remaining carry bytes (may contain a partial final message)
        if (carry.Length > 0)
        {
            var offset = 0;
            var framesFromCarry = 0;

            while (true)
            {
                var step = parser.TryReadFrame(carry, offset, framesFromCarry > 0, out var frame, out var nextOffset);
                if (step == ProtobufFrameStep.EndOfBuffer)
                {
                    break;
                }

                offset = nextOffset;

                if (step != ProtobufFrameStep.Frame)
                {
                    continue;
                }

                framesFromCarry++;
                messagesRead++;
                yield return frame!;
            }

            progress?.Report(new SdCardParseProgress(totalBytesRead, totalBytes, messagesRead));
        }
    }

    /// <summary>
    /// Produces <see cref="SdCardLogEntry"/> samples, decoding the log as they are consumed.
    /// </summary>
    private static async IAsyncEnumerable<SdCardLogEntry> ProduceSamples(
        Func<CancellationToken, IAsyncEnumerable<DaqifiOutMessage>> openMessages,
        DateTime baseTime,
        uint timestampFrequency,
        SdCardDeviceConfiguration? config,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var timestampReconstructor =
            SdCardTimestampReconstructor.ForTimestampFrequency(timestampFrequency);

        var enumerator = openMessages(ct).GetAsyncEnumerator(ct);
        await using (enumerator.ConfigureAwait(false))
        {
            var current = await enumerator.MoveNextAsync().ConfigureAwait(false)
                ? enumerator.Current
                : null;

            while (current != null)
            {
                ct.ThrowIfCancellationRequested();

                var msg = current;
                current = await enumerator.MoveNextAsync().ConfigureAwait(false)
                    ? enumerator.Current
                    : null;

                // Skip non-stream messages (no analog and no digital data)
                if (!HasStreamPayload(msg))
                {
                    continue;
                }

                // Merge consecutive messages at the same timestamp into a single entry.
                // Device firmware often sends separate analog and digital messages per sample
                // period with the same MsgTimeStamp. A message without stream payload ends the
                // merge without being consumed, so the loop above skips it on the next turn.
                while (current != null &&
                       current.MsgTimeStamp == msg.MsgTimeStamp &&
                       HasStreamPayload(current))
                {
                    var next = current;
                    current = await enumerator.MoveNextAsync().ConfigureAwait(false)
                        ? enumerator.Current
                        : null;

                    // Take analog data from whichever message has it
                    if (msg.AnalogInDataFloat.Count == 0 && next.AnalogInDataFloat.Count > 0)
                    {
                        msg.AnalogInDataFloat.AddRange(next.AnalogInDataFloat);
                    }
                    else if (msg.AnalogInData.Count == 0 && next.AnalogInData.Count > 0)
                    {
                        msg.AnalogInData.AddRange(next.AnalogInData);
                    }

                    // Take digital data from whichever message has it
                    if (msg.DigitalData.Length == 0 && next.DigitalData.Length > 0)
                    {
                        msg.DigitalData = next.DigitalData;
                    }

                    // Take per-channel timestamps from whichever message has them
                    if (msg.AnalogInDataTs.Count == 0 && next.AnalogInDataTs.Count > 0)
                    {
                        msg.AnalogInDataTs.AddRange(next.AnalogInDataTs);
                    }
                }

                // Reconstruct timestamp
                var hasDeviceTimestamp = msg.MsgTimeStamp != 0 && timestampReconstructor.HasDeviceClock;
                var timestamp = hasDeviceTimestamp
                    ? timestampReconstructor.Advance(msg.MsgTimeStamp, baseTime)
                    : baseTime;

                // Extract analog values (prefer float, fall back to scaled raw int)
                IReadOnlyList<double> analogValues;
                if (msg.AnalogInDataFloat.Count > 0)
                {
                    // Hand-rolled rather than LINQ: this runs once per sample in the file.
                    var floats = msg.AnalogInDataFloat;
                    var converted = new double[floats.Count];
                    for (var v = 0; v < floats.Count; v++)
                    {
                        converted[v] = floats[v];
                    }

                    analogValues = converted;
                }
                else if (msg.AnalogInData.Count > 0)
                {
                    analogValues = SdCardAnalogScaling.ScaleRawAnalogValues(msg.AnalogInData, config);
                }
                else
                {
                    analogValues = Array.Empty<double>();
                }

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

                yield return new SdCardLogEntry(timestamp, analogValues, digitalData, analogTimestamps)
                {
                    HasDeviceTimestamp = hasDeviceTimestamp
                };
            }
        }
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
    /// Collects the device configuration fields that firmware may embed in streaming data
    /// messages, keeping the first non-zero value seen for each, and reporting when there is
    /// nothing left to look for.
    /// </summary>
    /// <remarks>
    /// Accumulating message by message rather than over a materialized list is what lets
    /// <see cref="BuildSessionAsync"/> stop reading the moment the answer is settled, instead of
    /// always reading <see cref="SdCardParseOptions.ConfigurationScanMessageLimit"/> messages
    /// first (issue #697).
    /// </remarks>
    private sealed class ConfigurationScanner
    {
        private uint _timestampFreq;
        private uint _analogPortNum;
        private uint _digitalPortNum;
        private ulong _deviceSn;
        private string? _devicePn;
        private string? _fwRev;
        private IReadOnlyList<(double Slope, double Intercept)>? _calibration;
        private uint _resolution;
        private IReadOnlyList<double>? _portRange;
        private IReadOnlyList<double>? _internalScaleM;

        /// <summary>
        /// Gets a value indicating whether every field has been found, so no further message
        /// can change the result.
        /// </summary>
        public bool IsComplete =>
            _timestampFreq != 0 && _analogPortNum != 0 && _digitalPortNum != 0 &&
            _deviceSn != 0 && _devicePn != null && _fwRev != null && _calibration != null &&
            _resolution != 0 && _portRange != null && _internalScaleM != null;

        /// <summary>
        /// Takes whichever configuration fields <paramref name="msg"/> states and that have not
        /// been seen already.
        /// </summary>
        public void Add(DaqifiOutMessage msg)
        {
            if (_timestampFreq == 0 && msg.TimestampFreq != 0)
            {
                _timestampFreq = msg.TimestampFreq;
            }

            if (_analogPortNum == 0 && msg.AnalogInPortNum != 0)
            {
                _analogPortNum = msg.AnalogInPortNum;
            }

            if (_digitalPortNum == 0 && msg.DigitalPortNum != 0)
            {
                _digitalPortNum = msg.DigitalPortNum;
            }

            if (_deviceSn == 0 && msg.DeviceSn != 0)
            {
                _deviceSn = msg.DeviceSn;
            }

            if (_devicePn == null && !string.IsNullOrEmpty(msg.DevicePn))
            {
                _devicePn = msg.DevicePn;
            }

            if (_fwRev == null && !string.IsNullOrEmpty(msg.DeviceFwRev))
            {
                _fwRev = msg.DeviceFwRev;
            }

            if (_calibration == null && msg.AnalogInCalM.Count > 0 && msg.AnalogInCalB.Count > 0)
            {
                var count = Math.Min(msg.AnalogInCalM.Count, msg.AnalogInCalB.Count);
                var cal = new (double, double)[count];
                for (var i = 0; i < count; i++)
                {
                    cal[i] = (msg.AnalogInCalM[i], msg.AnalogInCalB[i]);
                }

                _calibration = cal;
            }

            if (_resolution == 0 && msg.AnalogInRes != 0)
            {
                _resolution = msg.AnalogInRes;
            }

            if (_portRange == null && msg.AnalogInPortRange.Count > 0)
            {
                _portRange = msg.AnalogInPortRange.Select(v => (double)v).ToArray();
            }

            if (_internalScaleM == null && msg.AnalogInIntScaleM.Count > 0)
            {
                _internalScaleM = msg.AnalogInIntScaleM.Select(v => (double)v).ToArray();
            }
        }

        /// <summary>
        /// Returns what was found, or <c>null</c> when no message stated a single field.
        /// </summary>
        public SdCardDeviceConfiguration? ToConfiguration()
        {
            // Only return a config if we found at least one meaningful field
            if (_timestampFreq == 0 && _analogPortNum == 0 && _digitalPortNum == 0 &&
                _deviceSn == 0 && _devicePn == null && _fwRev == null && _calibration == null &&
                _resolution == 0 && _portRange == null && _internalScaleM == null)
            {
                return null;
            }

            return new SdCardDeviceConfiguration(
                AnalogPortCount: (int)_analogPortNum,
                DigitalPortCount: (int)_digitalPortNum,
                TimestampFrequency: _timestampFreq,
                DeviceSerialNumber: _deviceSn != 0 ? _deviceSn.ToString() : null,
                DevicePartNumber: _devicePn,
                FirmwareRevision: _fwRev,
                CalibrationValues: _calibration,
                Resolution: _resolution,
                PortRange: _portRange,
                InternalScaleM: _internalScaleM);
        }
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
            CalibrationValues: primary.CalibrationValues ?? scanned.CalibrationValues,
            Resolution: primary.Resolution != 0 ? primary.Resolution : scanned.Resolution,
            PortRange: primary.PortRange ?? scanned.PortRange,
            InternalScaleM: primary.InternalScaleM ?? scanned.InternalScaleM);
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

        var portRange = statusMessage.AnalogInPortRange.Count > 0
            ? statusMessage.AnalogInPortRange.Select(v => (double)v).ToArray()
            : null;

        var internalScaleM = statusMessage.AnalogInIntScaleM.Count > 0
            ? statusMessage.AnalogInIntScaleM.Select(v => (double)v).ToArray()
            : null;

        return new SdCardDeviceConfiguration(
            AnalogPortCount: (int)statusMessage.AnalogInPortNum,
            DigitalPortCount: (int)statusMessage.DigitalPortNum,
            TimestampFrequency: statusMessage.TimestampFreq,
            DeviceSerialNumber: statusMessage.DeviceSn != 0 ? statusMessage.DeviceSn.ToString() : null,
            DevicePartNumber: statusMessage.DevicePn,
            FirmwareRevision: statusMessage.DeviceFwRev,
            CalibrationValues: calibration,
            Resolution: statusMessage.AnalogInRes,
            PortRange: portRange,
            InternalScaleM: internalScaleM);
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
