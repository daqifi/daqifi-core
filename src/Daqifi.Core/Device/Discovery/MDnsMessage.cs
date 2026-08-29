using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// One parsed DNS resource record. Only the record types DNS-SD browsing needs
/// (PTR, SRV, TXT, A) carry decoded payloads; anything else is kept as a bare
/// name/type/TTL so the reader can walk past it without discarding the message.
/// </summary>
internal sealed class MDnsResourceRecord
{
    /// <summary>
    /// The owner name, as raw labels. Kept as labels rather than a joined string so a
    /// suffix match ("is this record under <c>_daqifi._tcp.local.</c>?") is exact even
    /// when an instance label legitimately contains a dot or a space (RFC 6763 §4.1.1).
    /// </summary>
    public IReadOnlyList<string> Name { get; init; } = Array.Empty<string>();

    /// <summary>The record type (see the Type* constants on <see cref="MDnsMessage"/>).</summary>
    public ushort RecordType { get; init; }

    /// <summary>The record TTL in seconds. Zero is a goodbye packet (RFC 6762 §10.1).</summary>
    public uint Ttl { get; init; }

    /// <summary>The PTR or SRV target name, as labels; null for other record types.</summary>
    public IReadOnlyList<string>? Target { get; init; }

    /// <summary>The SRV port; zero for other record types.</summary>
    public ushort Port { get; init; }

    /// <summary>The raw TXT strings; null for other record types.</summary>
    public IReadOnlyList<string>? TxtStrings { get; init; }

    /// <summary>The A record address; null for other record types.</summary>
    public IPAddress? Address { get; init; }
}

/// <summary>
/// Minimal DNS wire-format reader and query writer for the mDNS-SD <b>client</b> half
/// (browse + resolve). Deliberately in-tree rather than a NuGet dependency: a browsing
/// client needs one query message and four record types, which is a few hundred lines of
/// bounds-checked parsing, whereas every managed mDNS package on offer also carries a
/// responder, a service registry and a background announcement engine that a library
/// consumer would then be shipping and patching for no benefit. See issue #183.
/// </summary>
/// <remarks>
/// The reader is strict: any structural violation (a truncated record, an out-of-range
/// compression pointer, a reserved label-length prefix) fails the whole message rather
/// than returning a half-parsed record set, because a datagram off the wire is untrusted
/// input and a partial parse is the shape that turns into a wrong device entry.
/// </remarks>
internal static class MDnsMessage
{
    /// <summary>The mDNS well-known UDP port (RFC 6762 §2).</summary>
    internal const int MulticastPort = 5353;

    /// <summary>Host address record.</summary>
    internal const ushort TypeA = 1;

    /// <summary>Service pointer record (service type to instance).</summary>
    internal const ushort TypePtr = 12;

    /// <summary>Service metadata record.</summary>
    internal const ushort TypeTxt = 16;

    /// <summary>Service location record (host + port).</summary>
    internal const ushort TypeSrv = 33;

    private const int HeaderLength = 12;
    private const int MaxLabelLength = 63;
    private const int MaxLabelCount = 128;
    private const int MaxPointerJumps = 64;
    private const ushort ResponseFlag = 0x8000;
    private const ushort ClassIn = 1;
    private const byte PointerMask = 0xC0;

    /// <summary>
    /// Splits a DNS-SD service type into labels, appending the implicit <c>local</c> domain.
    /// Accepts <c>_daqifi._tcp</c>, <c>_daqifi._tcp.local</c> and <c>_daqifi._tcp.local.</c>.
    /// </summary>
    /// <param name="serviceType">The service type to split.</param>
    /// <returns>The label sequence, e.g. <c>["_daqifi", "_tcp", "local"]</c>.</returns>
    /// <exception cref="ArgumentException">Thrown when the service type is empty or malformed.</exception>
    internal static IReadOnlyList<string> ParseServiceLabels(string serviceType)
    {
        if (string.IsNullOrWhiteSpace(serviceType))
        {
            throw new ArgumentException("Service type must not be empty.", nameof(serviceType));
        }

        var trimmed = serviceType.Trim().TrimEnd('.');
        var labels = new List<string>(trimmed.Split('.'));

        foreach (var label in labels)
        {
            if (label.Length == 0)
            {
                throw new ArgumentException($"Service type '{serviceType}' contains an empty label.", nameof(serviceType));
            }

            if (Encoding.UTF8.GetByteCount(label) > MaxLabelLength)
            {
                throw new ArgumentException($"Service type '{serviceType}' contains a label longer than {MaxLabelLength} bytes.", nameof(serviceType));
            }
        }

        if (labels.Count < 2)
        {
            throw new ArgumentException($"Service type '{serviceType}' must be of the form '_name._tcp'.", nameof(serviceType));
        }

        if (!labels[labels.Count - 1].Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            labels.Add("local");
        }

        return labels;
    }

    /// <summary>
    /// Builds a standard multicast PTR query ("who offers this service type?").
    /// </summary>
    /// <param name="serviceLabels">The service type labels from <see cref="ParseServiceLabels"/>.</param>
    /// <returns>The query datagram.</returns>
    /// <remarks>
    /// The QU (unicast-response) bit of RFC 6762 §5.4 is deliberately <b>not</b> set. The
    /// querying socket is bound to 5353, which makes this a normal continuous-querier
    /// question that responders answer by multicast — and a multicast answer is delivered
    /// to every socket joined to the group, whereas a unicast answer to 5353 on a host
    /// running its own mDNS daemon can be load-balanced to that daemon instead of to us.
    /// </remarks>
    internal static byte[] BuildPtrQuery(IReadOnlyList<string> serviceLabels)
    {
        if (serviceLabels is null)
        {
            throw new ArgumentNullException(nameof(serviceLabels));
        }

        var nameLength = 1; // the root label
        foreach (var label in serviceLabels)
        {
            nameLength += 1 + Encoding.UTF8.GetByteCount(label);
        }

        var buffer = new byte[HeaderLength + nameLength + 4];
        var offset = 0;

        offset = WriteUInt16(buffer, offset, 0);            // ID: 0 for mDNS
        offset = WriteUInt16(buffer, offset, 0);            // flags: standard query
        offset = WriteUInt16(buffer, offset, 1);            // QDCOUNT
        offset = WriteUInt16(buffer, offset, 0);            // ANCOUNT
        offset = WriteUInt16(buffer, offset, 0);            // NSCOUNT
        offset = WriteUInt16(buffer, offset, 0);            // ARCOUNT

        foreach (var label in serviceLabels)
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            buffer[offset++] = (byte)bytes.Length;
            Buffer.BlockCopy(bytes, 0, buffer, offset, bytes.Length);
            offset += bytes.Length;
        }

        buffer[offset++] = 0;                               // root label

        offset = WriteUInt16(buffer, offset, TypePtr);      // QTYPE
        WriteUInt16(buffer, offset, ClassIn);               // QCLASS

        return buffer;
    }

    /// <summary>
    /// Parses a datagram as an mDNS <b>response</b>, returning every resource record in the
    /// answer, authority and additional sections. Queries (including our own, looped back)
    /// and structurally invalid datagrams are rejected.
    /// </summary>
    /// <param name="buffer">The received datagram.</param>
    /// <param name="length">The number of valid bytes in <paramref name="buffer"/>.</param>
    /// <param name="records">The parsed records on success; empty otherwise.</param>
    /// <returns><c>true</c> if the datagram was a well-formed response.</returns>
    internal static bool TryParseResponse(byte[] buffer, int length, out IReadOnlyList<MDnsResourceRecord> records)
    {
        records = Array.Empty<MDnsResourceRecord>();

        if (buffer is null || length < HeaderLength || length > buffer.Length)
        {
            return false;
        }

        var flags = ReadUInt16(buffer, 2);
        if ((flags & ResponseFlag) == 0)
        {
            return false; // a query, not a response
        }

        var questionCount = ReadUInt16(buffer, 4);
        var recordCount = ReadUInt16(buffer, 6) + ReadUInt16(buffer, 8) + ReadUInt16(buffer, 10);

        var offset = HeaderLength;

        for (var i = 0; i < questionCount; i++)
        {
            if (!TryReadName(buffer, length, length, ref offset, out _) || offset + 4 > length)
            {
                return false;
            }

            offset += 4; // QTYPE + QCLASS
        }

        // Size the list from the datagram, not from the counts: a hostile header can claim
        // 3 x 65535 records, and the smallest possible record is 11 bytes, so anything beyond
        // length/11 is a lie the loop below is about to reject anyway.
        var parsed = new List<MDnsResourceRecord>(Math.Min(recordCount, length / 11));
        for (var i = 0; i < recordCount; i++)
        {
            if (!TryReadRecord(buffer, length, ref offset, out var record))
            {
                return false;
            }

            parsed.Add(record);
        }

        records = parsed;
        return true;
    }

    /// <summary>
    /// Compares two label sequences case-insensitively, as DNS name comparison requires.
    /// </summary>
    /// <param name="left">The first label sequence.</param>
    /// <param name="right">The second label sequence.</param>
    /// <returns><c>true</c> if the two names are equal.</returns>
    internal static bool NameEquals(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!left[i].Equals(right[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns true if <paramref name="name"/> is exactly one label deeper than
    /// <paramref name="suffix"/> and ends with it — i.e. it is a service <i>instance</i>
    /// name under that service type.
    /// </summary>
    /// <param name="name">The candidate instance name.</param>
    /// <param name="suffix">The service type labels.</param>
    /// <returns><c>true</c> if the name is an instance of the service type.</returns>
    internal static bool IsInstanceOf(IReadOnlyList<string>? name, IReadOnlyList<string> suffix)
    {
        if (name is null || name.Count != suffix.Count + 1)
        {
            return false;
        }

        for (var i = 0; i < suffix.Count; i++)
        {
            if (!name[i + 1].Equals(suffix[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadRecord(byte[] buffer, int length, ref int offset, out MDnsResourceRecord record)
    {
        record = null!;

        if (!TryReadName(buffer, length, length, ref offset, out var name))
        {
            return false;
        }

        if (offset + 10 > length)
        {
            return false;
        }

        var recordType = ReadUInt16(buffer, offset);
        offset += 2;
        offset += 2; // CLASS — the mDNS cache-flush bit lives here and is not needed for browsing
        var ttl = ReadUInt32(buffer, offset);
        offset += 4;
        var rdLength = ReadUInt16(buffer, offset);
        offset += 2;

        if (offset + rdLength > length)
        {
            return false;
        }

        var rdataStart = offset;
        var rdataEnd = rdataStart + rdLength;

        IReadOnlyList<string>? target = null;
        IReadOnlyList<string>? txtStrings = null;
        IPAddress? address = null;
        ushort port = 0;

        switch (recordType)
        {
            case TypePtr:
            {
                var cursor = rdataStart;

                // The name's own bytes must fit inside RDATA (a compression pointer inside it may
                // still resolve elsewhere in the message). Without the rdataEnd bound, a record
                // that understates RDLENGTH would have its target silently assembled from the
                // *next* record's bytes and still parse as valid.
                if (!TryReadName(buffer, length, rdataEnd, ref cursor, out var ptrTarget))
                {
                    return false;
                }

                target = ptrTarget;
                break;
            }

            case TypeSrv:
            {
                if (rdLength < 7)
                {
                    return false;
                }

                port = ReadUInt16(buffer, rdataStart + 4);
                var cursor = rdataStart + 6;
                if (!TryReadName(buffer, length, rdataEnd, ref cursor, out var srvTarget))
                {
                    return false;
                }

                target = srvTarget;
                break;
            }

            case TypeTxt:
            {
                if (!TryReadTxtStrings(buffer, rdataStart, rdataEnd, out var strings))
                {
                    return false;
                }

                txtStrings = strings;
                break;
            }

            case TypeA:
            {
                if (rdLength != 4)
                {
                    return false;
                }

                var addressBytes = new byte[4];
                Buffer.BlockCopy(buffer, rdataStart, addressBytes, 0, 4);
                address = new IPAddress(addressBytes);
                break;
            }
        }

        // Always resynchronize on RDLENGTH rather than on where the payload parse stopped:
        // a compressed name inside RDATA can end before RDLENGTH does, and an unknown record
        // type is skipped wholesale.
        offset = rdataEnd;

        record = new MDnsResourceRecord
        {
            Name = name,
            RecordType = recordType,
            Ttl = ttl,
            Target = target,
            Port = port,
            TxtStrings = txtStrings,
            Address = address
        };

        return true;
    }

    private static bool TryReadTxtStrings(byte[] buffer, int start, int end, out IReadOnlyList<string> strings)
    {
        var result = new List<string>();
        var cursor = start;

        while (cursor < end)
        {
            var stringLength = buffer[cursor++];
            if (cursor + stringLength > end)
            {
                strings = Array.Empty<string>();
                return false;
            }

            result.Add(Encoding.UTF8.GetString(buffer, cursor, stringLength));
            cursor += stringLength;
        }

        strings = result;
        return true;
    }

    /// <summary>
    /// Reads a (possibly compression-pointer-bearing) DNS name into its raw labels, advancing
    /// <paramref name="offset"/> past the name as it appears at the current position.
    /// </summary>
    /// <param name="buffer">The datagram.</param>
    /// <param name="length">The datagram length — the bound for bytes reached through a pointer.</param>
    /// <param name="wireLimit">
    /// The bound for the name's own bytes at <paramref name="offset"/>. For a name inside RDATA
    /// this is the record's RDATA end, so a record understating RDLENGTH cannot have its name
    /// completed from the bytes of the record that follows; for an owner name it is
    /// <paramref name="length"/>. Bytes reached through a compression pointer are bounded by
    /// <paramref name="length"/> instead, because a pointer legitimately targets any earlier name
    /// in the message.
    /// </param>
    /// <param name="offset">On entry, where the name starts; on success, the byte after it.</param>
    /// <param name="labels">The decoded labels.</param>
    private static bool TryReadName(byte[] buffer, int length, int wireLimit, ref int offset, out IReadOnlyList<string> labels)
    {
        var result = new List<string>();
        labels = result;

        var cursor = offset;
        var jumps = 0;
        int? resumeAt = null;

        while (true)
        {
            // Before the first pointer we are still reading the name's own on-wire bytes; after
            // one, we are reading a name that lives elsewhere in the message.
            var bound = resumeAt.HasValue ? length : wireLimit;

            if (cursor < 0 || cursor >= bound)
            {
                return false;
            }

            var labelLength = buffer[cursor];

            if ((labelLength & PointerMask) == PointerMask)
            {
                if (cursor + 1 >= bound)
                {
                    return false;
                }

                var pointer = ((labelLength & 0x3F) << 8) | buffer[cursor + 1];

                // The first pointer is where this name ends on the wire; later jumps do not
                // move the caller's cursor.
                resumeAt ??= cursor + 2;

                if (++jumps > MaxPointerJumps || pointer >= length)
                {
                    return false; // malformed, or a pointer loop
                }

                cursor = pointer;
                continue;
            }

            if ((labelLength & PointerMask) != 0)
            {
                return false; // reserved label-length prefix
            }

            cursor++;

            if (labelLength == 0)
            {
                break; // root label — end of name
            }

            if (cursor + labelLength > bound || result.Count >= MaxLabelCount)
            {
                return false;
            }

            result.Add(Encoding.UTF8.GetString(buffer, cursor, labelLength));
            cursor += labelLength;
        }

        offset = resumeAt ?? cursor;
        return true;
    }

    private static int WriteUInt16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value >> 8);
        buffer[offset + 1] = (byte)value;
        return offset + 2;
    }

    private static ushort ReadUInt16(byte[] buffer, int offset)
        => (ushort)((buffer[offset] << 8) | buffer[offset + 1]);

    private static uint ReadUInt32(byte[] buffer, int offset)
        => ((uint)buffer[offset] << 24) | ((uint)buffer[offset + 1] << 16) |
           ((uint)buffer[offset + 2] << 8) | buffer[offset + 3];
}
