using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Daqifi.Core.Device.Discovery;

namespace Daqifi.Core.Tests.Device.Discovery;

/// <summary>
/// Wire-format tests for the in-tree mDNS reader. Every packet here is built byte by byte by
/// <see cref="MDnsResponseBuilder"/>, so the whole file runs with no network and no device.
/// </summary>
public class MDnsMessageTests
{
    private static readonly IReadOnlyList<string> DaqifiService = MDnsMessage.ParseServiceLabels("_daqifi._tcp");

    #region Service type parsing

    [Fact]
    public void ParseServiceLabels_AppendsImplicitLocalDomain()
    {
        var labels = MDnsMessage.ParseServiceLabels("_daqifi._tcp");

        Assert.Equal(["_daqifi", "_tcp", "local"], labels);
    }

    [Theory]
    [InlineData("_daqifi._tcp.local")]
    [InlineData("_daqifi._tcp.local.")]
    [InlineData("  _daqifi._tcp  ")]
    public void ParseServiceLabels_AcceptsQualifiedForms(string serviceType)
    {
        var labels = MDnsMessage.ParseServiceLabels(serviceType);

        Assert.Equal(["_daqifi", "_tcp", "local"], labels);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("_daqifi")]
    [InlineData("_daqifi.._tcp")]
    public void ParseServiceLabels_RejectsMalformedServiceType(string serviceType)
    {
        Assert.Throws<ArgumentException>(() => MDnsMessage.ParseServiceLabels(serviceType));
    }

    [Fact]
    public void ParseServiceLabels_RejectsOverlongLabel()
    {
        var serviceType = "_" + new string('a', 63) + "._tcp";

        Assert.Throws<ArgumentException>(() => MDnsMessage.ParseServiceLabels(serviceType));
    }

    #endregion

    #region Query encoding

    [Fact]
    public void BuildPtrQuery_EncodesASingleServiceTypeQuestion()
    {
        var query = MDnsMessage.BuildPtrQuery(DaqifiService);

        // Header: ID 0, flags 0 (standard query), QDCOUNT 1, everything else 0.
        Assert.Equal(0, query[0] << 8 | query[1]);
        Assert.Equal(0, query[2] << 8 | query[3]);
        Assert.Equal(1, query[4] << 8 | query[5]);
        Assert.Equal(0, query[6] << 8 | query[7]);

        // QNAME: _daqifi._tcp.local.
        var offset = 12;
        foreach (var label in DaqifiService)
        {
            Assert.Equal(label.Length, query[offset]);
            Assert.Equal(label, System.Text.Encoding.UTF8.GetString(query, offset + 1, label.Length));
            offset += 1 + label.Length;
        }

        Assert.Equal(0, query[offset]);
        offset++;

        Assert.Equal(MDnsMessage.TypePtr, query[offset] << 8 | query[offset + 1]);

        // QCLASS is IN with the top (QU / unicast-response) bit clear: the querier is bound to
        // 5353 and wants the multicast answer, which every joined socket receives.
        Assert.Equal(1, query[offset + 2] << 8 | query[offset + 3]);
        Assert.Equal(offset + 4, query.Length);
    }

    [Fact]
    public void TryParseResponse_RejectsOurOwnQueryLoopedBack()
    {
        var query = MDnsMessage.BuildPtrQuery(DaqifiService);

        Assert.False(MDnsMessage.TryParseResponse(query, query.Length, out var records));
        Assert.Empty(records);
    }

    #endregion

    #region Response parsing

    [Fact]
    public void TryParseResponse_ParsesTheFirmwareAdvertisement()
    {
        var packet = MDnsResponseBuilder.DeviceAdvertisement();

        Assert.True(MDnsMessage.TryParseResponse(packet, packet.Length, out var records));
        Assert.Equal(4, records.Count);

        var ptr = records.Single(r => r.RecordType == MDnsMessage.TypePtr);
        Assert.Equal(["_daqifi", "_tcp", "local"], ptr.Name);
        Assert.Equal(["DAQiFi-95A7", "_daqifi", "_tcp", "local"], ptr.Target);
        Assert.Equal(4500u, ptr.Ttl);

        var srv = records.Single(r => r.RecordType == MDnsMessage.TypeSrv);
        Assert.Equal(9760, srv.Port);
        Assert.Equal(["daqifi-95a7", "local"], srv.Target);

        var txt = records.Single(r => r.RecordType == MDnsMessage.TypeTxt);
        Assert.Equal(MDnsResponseBuilder.DefaultTxt(), txt.TxtStrings);

        var a = records.Single(r => r.RecordType == MDnsMessage.TypeA);
        Assert.Equal(IPAddress.Parse("192.168.1.39"), a.Address);
    }

    [Fact]
    public void TryParseResponse_ResolvesNameCompressionPointers()
    {
        var compressed = MDnsResponseBuilder.DeviceAdvertisement(compressNames: true);
        var uncompressed = MDnsResponseBuilder.DeviceAdvertisement();

        // Compression has to actually be exercised, or this test proves nothing.
        Assert.True(compressed.Length < uncompressed.Length);

        Assert.True(MDnsMessage.TryParseResponse(compressed, compressed.Length, out var records));

        var srv = records.Single(r => r.RecordType == MDnsMessage.TypeSrv);
        Assert.Equal(["DAQiFi-95A7", "_daqifi", "_tcp", "local"], srv.Name);
        Assert.Equal(9760, srv.Port);

        var a = records.Single(r => r.RecordType == MDnsMessage.TypeA);
        Assert.Equal(IPAddress.Parse("192.168.1.39"), a.Address);
    }

    [Fact]
    public void TryParseResponse_SkipsUnknownRecordTypesByLength()
    {
        var packet = new MDnsResponseBuilder()
            .AddOpaque(MDnsResponseBuilder.ServiceType, recordType: 47 /* NSEC */, rdata: [1, 2, 3, 4, 5])
            .AddPtr(MDnsResponseBuilder.ServiceType, "DAQiFi-95A7." + MDnsResponseBuilder.ServiceType)
            .Build();

        Assert.True(MDnsMessage.TryParseResponse(packet, packet.Length, out var records));
        Assert.Equal(2, records.Count);
        Assert.Contains(records, r => r.RecordType == MDnsMessage.TypePtr);
    }

    [Fact]
    public void TryParseResponse_ParsesTxtRecordWithNoStrings()
    {
        var packet = new MDnsResponseBuilder()
            .AddTxt("DAQiFi-95A7." + MDnsResponseBuilder.ServiceType, [])
            .Build();

        Assert.True(MDnsMessage.TryParseResponse(packet, packet.Length, out var records));
        Assert.Empty(records.Single().TxtStrings!);
    }

    [Fact]
    public void TryParseResponse_RejectsTruncatedDatagram()
    {
        var packet = MDnsResponseBuilder.DeviceAdvertisement();

        // Every prefix that still has a header must be rejected, not half-parsed into a device.
        for (var length = 12; length < packet.Length; length++)
        {
            Assert.False(MDnsMessage.TryParseResponse(packet, length, out var records), $"length {length}");
            Assert.Empty(records);
        }
    }

    [Fact]
    public void TryParseResponse_RejectsShortAndEmptyDatagrams()
    {
        Assert.False(MDnsMessage.TryParseResponse([], 0, out _));
        Assert.False(MDnsMessage.TryParseResponse(new byte[11], 11, out _));
        Assert.False(MDnsMessage.TryParseResponse(new byte[12], 20, out _)); // length beyond the buffer
    }

    [Fact]
    public void TryParseResponse_RejectsCompressionPointerLoop()
    {
        // Header claiming one answer, whose owner name is a pointer to itself.
        var packet = new byte[]
        {
            0x00, 0x00, 0x84, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00,
            0xC0, 0x0C, // name = pointer to offset 12, i.e. this record's own name
            0x00, 0x0C, 0x00, 0x01, 0x00, 0x00, 0x11, 0x94, 0x00, 0x00
        };

        Assert.False(MDnsMessage.TryParseResponse(packet, packet.Length, out _));
    }

    [Fact]
    public void TryParseResponse_RejectsPointerPastEndOfMessage()
    {
        var packet = new byte[]
        {
            0x00, 0x00, 0x84, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00,
            0xC0, 0xFF, // pointer to offset 255, well past this datagram
            0x00, 0x0C, 0x00, 0x01, 0x00, 0x00, 0x11, 0x94, 0x00, 0x00
        };

        Assert.False(MDnsMessage.TryParseResponse(packet, packet.Length, out _));
    }

    [Fact]
    public void TryParseResponse_SkipsTheQuestionSectionOfAResponse()
    {
        // Some responders echo the question back. Build a response by hand with QDCOUNT = 1.
        var advertisement = MDnsResponseBuilder.DeviceAdvertisement();
        var question = MDnsMessage.BuildPtrQuery(DaqifiService);
        var questionBody = question.Skip(12).ToArray();

        var packet = new byte[advertisement.Length + questionBody.Length];
        Buffer.BlockCopy(advertisement, 0, packet, 0, 12);
        Buffer.BlockCopy(questionBody, 0, packet, 12, questionBody.Length);
        Buffer.BlockCopy(advertisement, 12, packet, 12 + questionBody.Length, advertisement.Length - 12);
        packet[4] = 0;
        packet[5] = 1; // QDCOUNT = 1

        Assert.True(MDnsMessage.TryParseResponse(packet, packet.Length, out var records));
        Assert.Equal(4, records.Count);
    }

    #endregion

    #region Name comparison

    [Fact]
    public void NameEquals_IsCaseInsensitiveAndLengthSensitive()
    {
        Assert.True(MDnsMessage.NameEquals(["_DAQiFi", "_TCP", "Local"], DaqifiService));
        Assert.False(MDnsMessage.NameEquals(["_daqifi", "_tcp"], DaqifiService));
        Assert.False(MDnsMessage.NameEquals(null, DaqifiService));
    }

    [Fact]
    public void IsInstanceOf_MatchesOneLabelDeeperOnly()
    {
        Assert.True(MDnsMessage.IsInstanceOf(["DAQiFi-95A7", "_daqifi", "_tcp", "local"], DaqifiService));
        Assert.False(MDnsMessage.IsInstanceOf(["_daqifi", "_tcp", "local"], DaqifiService));
        Assert.False(MDnsMessage.IsInstanceOf(["Apple TV", "_airplay", "_tcp", "local"], DaqifiService));
        Assert.False(MDnsMessage.IsInstanceOf(null, DaqifiService));
    }

    #endregion
}
