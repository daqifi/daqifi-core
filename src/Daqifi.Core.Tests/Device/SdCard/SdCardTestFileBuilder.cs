using System.IO;
using Daqifi.Core.Communication.Messages;
using Google.Protobuf;

namespace Daqifi.Core.Tests.Device.SdCard;

/// <summary>
/// Helper for constructing synthetic SD card <c>.bin</c> files in tests.
/// Writes varint-prefixed <see cref="DaqifiOutMessage"/> payloads to a <see cref="MemoryStream"/>.
/// </summary>
internal sealed class SdCardTestFileBuilder
{
    private readonly MemoryStream _stream = new();

    /// <summary>
    /// Writes a <see cref="DaqifiOutMessage"/> to the stream with a varint32 length prefix.
    /// </summary>
    public SdCardTestFileBuilder AddMessage(DaqifiOutMessage message)
    {
        var payload = message.ToByteArray();
        var coded = new CodedOutputStream(_stream, leaveOpen: true);
        coded.WriteLength(payload.Length);
        coded.Flush();
        _stream.Write(payload, 0, payload.Length);
        return this;
    }

    /// <summary>
    /// Appends raw bytes to the stream (useful for markers or corruption testing).
    /// </summary>
    public SdCardTestFileBuilder AddRawBytes(byte[] data)
    {
        _stream.Write(data, 0, data.Length);
        return this;
    }

    /// <summary>
    /// Creates a status message with common device configuration fields.
    /// </summary>
    public static DaqifiOutMessage CreateStatusMessage(
        uint analogPortNum = 8,
        uint digitalPortNum = 4,
        uint timestampFreq = 50_000_000,
        string? firmwareRevision = "1.0.0",
        string? partNumber = "Nyquist1",
        ulong serialNumber = 12345)
    {
        var msg = new DaqifiOutMessage
        {
            AnalogInPortNum = analogPortNum,
            DigitalPortNum = digitalPortNum,
            TimestampFreq = timestampFreq,
        };

        if (firmwareRevision != null)
        {
            msg.DeviceFwRev = firmwareRevision;
        }

        if (partNumber != null)
        {
            msg.DevicePn = partNumber;
        }

        if (serialNumber != 0)
        {
            msg.DeviceSn = serialNumber;
        }

        return msg;
    }

    /// <summary>
    /// Creates a stream message with analog float data and a timestamp.
    /// </summary>
    public static DaqifiOutMessage CreateStreamMessage(
        uint timestamp,
        float[]? analogFloatValues = null,
        int[]? analogIntValues = null,
        byte[]? digitalData = null,
        uint[]? analogTimestamps = null)
    {
        var msg = new DaqifiOutMessage
        {
            MsgTimeStamp = timestamp
        };

        if (analogFloatValues != null)
        {
            msg.AnalogInDataFloat.AddRange(analogFloatValues);
        }

        if (analogIntValues != null)
        {
            msg.AnalogInData.AddRange(analogIntValues);
        }

        if (digitalData != null)
        {
            msg.DigitalData = ByteString.CopyFrom(digitalData);
        }

        if (analogTimestamps != null)
        {
            msg.AnalogInDataTs.AddRange(analogTimestamps);
        }

        return msg;
    }

    /// <summary>
    /// Returns a <see cref="MemoryStream"/> positioned at the beginning, ready for reading.
    /// </summary>
    public MemoryStream Build()
    {
        var result = new MemoryStream(_stream.ToArray());
        return result;
    }
}
