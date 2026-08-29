using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace Daqifi.Core.Tests.Device.Discovery;

/// <summary>
/// Builds mDNS response datagrams on the wire so the discovery path can be exercised with no
/// network at all. The default shape mirrors what the device firmware actually transmits
/// (daqifi-nyquist-firmware#345: PTR in the answer section, SRV + TXT + A in additional,
/// names uncompressed), so a test packet is a recorded advertisement rather than an invention.
/// </summary>
internal sealed class MDnsResponseBuilder
{
    internal const string ServiceType = "_daqifi._tcp.local";
    internal const string DefaultInstanceLabel = "DAQiFi-95A7";
    internal const string DefaultHost = "daqifi-95a7.local";
    internal const string DefaultDeviceIp = "192.168.1.39";
    internal const ushort DefaultPort = 9760;

    private const ushort TypeA = 1;
    private const ushort TypePtr = 12;
    private const ushort TypeTxt = 16;
    private const ushort TypeSrv = 33;
    private const ushort ClassIn = 1;
    private const ushort ClassInCacheFlush = 0x8001;

    private readonly List<byte> _buffer = [];
    private readonly Dictionary<string, int> _nameOffsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _compressNames;

    private int _answerCount;
    private int _additionalCount;

    internal MDnsResponseBuilder(bool compressNames = false)
    {
        _compressNames = compressNames;
        _buffer.AddRange(new byte[12]); // header, patched in Build()
    }

    /// <summary>
    /// The complete advertisement a DAQiFi device sends in response to a browse query.
    /// </summary>
    internal static byte[] DeviceAdvertisement(
        string instanceLabel = DefaultInstanceLabel,
        string host = DefaultHost,
        string? deviceIp = DefaultDeviceIp,
        ushort port = DefaultPort,
        IEnumerable<string>? txtStrings = null,
        uint ptrTtl = 4500,
        bool compressNames = false)
    {
        var instance = instanceLabel + "." + ServiceType;
        var builder = new MDnsResponseBuilder(compressNames)
            .AddPtr(ServiceType, instance, ptrTtl)
            .AddSrv(instance, host, port)
            .AddTxt(instance, txtStrings ?? DefaultTxt());

        if (deviceIp is not null)
        {
            builder.AddA(host, IPAddress.Parse(deviceIp));
        }

        return builder.Build();
    }

    /// <summary>
    /// The TXT record set the firmware publishes.
    /// </summary>
    internal static string[] DefaultTxt() =>
    [
        "sn=9090539562006014104",
        "pn=Nq1",
        "fw=3.7.3",
        "hw=2.0.0",
        "friendly=Bench Nyquist"
    ];

    internal MDnsResponseBuilder AddPtr(string owner, string target, uint ttl = 4500, bool inAnswerSection = true)
    {
        WriteName(owner);
        var rdLengthAt = WriteRecordHeader(TypePtr, ClassIn, ttl);
        var start = _buffer.Count;
        WriteName(target);
        PatchUInt16(rdLengthAt, (ushort)(_buffer.Count - start));
        CountRecord(inAnswerSection);
        return this;
    }

    internal MDnsResponseBuilder AddSrv(string owner, string target, ushort port, uint ttl = 120, bool inAnswerSection = false)
    {
        WriteName(owner);
        var rdLengthAt = WriteRecordHeader(TypeSrv, ClassInCacheFlush, ttl);
        var start = _buffer.Count;
        WriteUInt16(0); // priority
        WriteUInt16(0); // weight
        WriteUInt16(port);
        WriteName(target);
        PatchUInt16(rdLengthAt, (ushort)(_buffer.Count - start));
        CountRecord(inAnswerSection);
        return this;
    }

    internal MDnsResponseBuilder AddTxt(string owner, IEnumerable<string> strings, uint ttl = 4500, bool inAnswerSection = false)
    {
        WriteName(owner);
        var rdLengthAt = WriteRecordHeader(TypeTxt, ClassInCacheFlush, ttl);
        var start = _buffer.Count;
        foreach (var entry in strings)
        {
            var bytes = Encoding.UTF8.GetBytes(entry);
            _buffer.Add((byte)bytes.Length);
            _buffer.AddRange(bytes);
        }

        PatchUInt16(rdLengthAt, (ushort)(_buffer.Count - start));
        CountRecord(inAnswerSection);
        return this;
    }

    internal MDnsResponseBuilder AddA(string owner, IPAddress address, uint ttl = 120, bool inAnswerSection = false)
    {
        WriteName(owner);
        var rdLengthAt = WriteRecordHeader(TypeA, ClassInCacheFlush, ttl);
        var start = _buffer.Count;
        _buffer.AddRange(address.GetAddressBytes());
        PatchUInt16(rdLengthAt, (ushort)(_buffer.Count - start));
        CountRecord(inAnswerSection);
        return this;
    }

    /// <summary>
    /// Adds a record of a type the client does not decode, to prove an unrecognized record is
    /// skipped by RDLENGTH rather than derailing the rest of the message.
    /// </summary>
    internal MDnsResponseBuilder AddOpaque(string owner, ushort recordType, byte[] rdata, uint ttl = 120, bool inAnswerSection = false)
    {
        WriteName(owner);
        var rdLengthAt = WriteRecordHeader(recordType, ClassIn, ttl);
        _buffer.AddRange(rdata);
        PatchUInt16(rdLengthAt, (ushort)rdata.Length);
        CountRecord(inAnswerSection);
        return this;
    }

    internal byte[] Build()
    {
        PatchUInt16(0, 0);           // ID
        PatchUInt16(2, 0x8400);      // QR = 1, AA = 1
        PatchUInt16(4, 0);           // QDCOUNT
        PatchUInt16(6, (ushort)_answerCount);
        PatchUInt16(8, 0);           // NSCOUNT
        PatchUInt16(10, (ushort)_additionalCount);
        return _buffer.ToArray();
    }

    private void CountRecord(bool inAnswerSection)
    {
        if (inAnswerSection)
        {
            _answerCount++;
        }
        else
        {
            _additionalCount++;
        }
    }

    private int WriteRecordHeader(ushort recordType, ushort recordClass, uint ttl)
    {
        WriteUInt16(recordType);
        WriteUInt16(recordClass);
        WriteUInt16((ushort)(ttl >> 16));
        WriteUInt16((ushort)(ttl & 0xFFFF));
        var rdLengthAt = _buffer.Count;
        WriteUInt16(0); // patched by the caller
        return rdLengthAt;
    }

    private void WriteName(string name)
    {
        var labels = name.TrimEnd('.').Split('.');

        for (var i = 0; i < labels.Length; i++)
        {
            var suffix = string.Join(".", labels, i, labels.Length - i);

            if (_compressNames && _nameOffsets.TryGetValue(suffix, out var offset) && offset < 0x4000)
            {
                _buffer.Add((byte)(0xC0 | (offset >> 8)));
                _buffer.Add((byte)(offset & 0xFF));
                return;
            }

            _nameOffsets.TryAdd(suffix, _buffer.Count);

            var bytes = Encoding.UTF8.GetBytes(labels[i]);
            _buffer.Add((byte)bytes.Length);
            _buffer.AddRange(bytes);
        }

        _buffer.Add(0); // root label
    }

    private void WriteUInt16(ushort value)
    {
        _buffer.Add((byte)(value >> 8));
        _buffer.Add((byte)value);
    }

    private void PatchUInt16(int offset, ushort value)
    {
        _buffer[offset] = (byte)(value >> 8);
        _buffer[offset + 1] = (byte)value;
    }
}
