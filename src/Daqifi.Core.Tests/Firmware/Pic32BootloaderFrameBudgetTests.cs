using System.IO;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device.Discovery;
using Daqifi.Core.Firmware;
using Microsoft.Extensions.Logging.Abstractions;

namespace Daqifi.Core.Tests.Firmware;

/// <summary>
/// The size of a PROGRAM_FLASH frame against the size of one HID OUT report (#840).
/// </summary>
/// <remarks>
/// <para>
/// A frame is SOH, then the DLE-escaped opcode, HEX record and CRC-16, then EOT. Each byte of the
/// escaped body that collides with SOH (0x01), EOT (0x04) or DLE (0x10) becomes two bytes, so a
/// record of legal length can still make a frame that no HID report can carry. The transport then
/// refuses the write — during programming, after the erase — and the board is left with no
/// application.
/// </para>
/// <para>
/// Every fixture here is a byte array. No test in this file touches a device: a real attempt would
/// have to erase the board first, which is the state these tests exist to prevent.
/// </para>
/// </remarks>
public class Pic32BootloaderFrameBudgetTests
{
    private const byte SOH = 0x01;
    private const byte EOT = 0x04;
    private const byte DLE = 0x10;

    /// <summary>The longest frame one HID OUT report carries, as the transports enforce it.</summary>
    private const int OneReport = 63;

    // ---------------------------------------------------------------------------------------
    // What the producer does today, pinned byte-exactly
    // ---------------------------------------------------------------------------------------

    public static TheoryData<string, byte[], int> ProgramFrameLengths => new()
    {
        // A short record with no byte to escape. The frame is the record plus SOH, the opcode,
        // the CRC-16 and EOT, and nothing more.
        { "8 data bytes, no byte needs an escape", DataRecord(0x0020, Repeat(0x55, 8)), 18 },

        // What XC32 emits. Note the byte-count field of a 16-byte record is 0x10, which is DLE
        // itself, so even a plain 16-byte record carries an escape.
        { "16 data bytes, 5 bytes need an escape", MixedRecord(), 31 },

        // The worst case: every byte of the record needs an escape.
        { "37 bytes, every byte needs an escape", Repeat(DLE, 37), 79 },

        // A legal 32-byte Intel HEX data record, full of bytes that need escaping. This is the
        // record from the issue. 74 bytes is 11 bytes more than one report holds.
        { "32 data bytes, every data byte needs an escape", DataRecord(0x0000, Repeat(DLE, 32)), 74 },

        // Exactly at the boundary: the longest frame a report still carries.
        { "37 bytes, 21 of them need an escape", Repeat(DLE, 21).Concat(Repeat(0x55, 16)).ToArray(), 63 },

        // One byte over it.
        { "37 bytes, 22 of them need an escape", Repeat(DLE, 22).Concat(Repeat(0x55, 15)).ToArray(), 64 }
    };

    [Theory]
    [MemberData(nameof(ProgramFrameLengths))]
    public void CreateProgramFlashMessage_FrameLengthGrowsWithTheBytesThatNeedAnEscape(
        string description,
        byte[] record,
        int expectedFrameLength)
    {
        var frame = Pic32BootloaderMessageProducer.CreateProgramFlashMessage(record);

        Assert.Equal(expectedFrameLength, frame.Length);
        Assert.Equal(SOH, frame[0]);
        Assert.Equal(EOT, frame[^1]);

        // The frame length is the one number that decides whether the write is refused, so say
        // which side of one report each fixture falls on.
        Assert.Equal(expectedFrameLength <= OneReport, frame.Length <= OneReport);
        Assert.NotEmpty(description);
    }

    [Fact]
    public void WorstCaseFrameLength_IsTheLengthWithEveryByteEscaped()
    {
        // The bound the library programs against. A record of 27 bytes makes a frame of at most
        // 62 bytes, which one report carries. A record of 28 bytes makes one of up to 64, which
        // it does not.
        Assert.Equal(27, Pic32BootloaderFrameBudget.MaxSafeRecordLength);
        Assert.Equal(62, Pic32BootloaderFrameBudget.WorstCaseFrameLength(27));
        Assert.Equal(64, Pic32BootloaderFrameBudget.WorstCaseFrameLength(28));
        Assert.Equal(63, Pic32BootloaderFrameBudget.MaxFrameLength);

        // 22 data bytes is what 27 record bytes leave after the Intel HEX byte count, address,
        // type and checksum. A 16-byte record, which is what the split emits, is well inside it.
        Assert.Equal(22, Pic32BootloaderFrameBudget.MaxSafeDataByteCount);
        Assert.True(Pic32BootloaderFrameBudget.SplitDataByteCount <= Pic32BootloaderFrameBudget.MaxSafeDataByteCount);
        Assert.Equal(50, Pic32BootloaderFrameBudget.WorstCaseFrameLength(Pic32BootloaderFrameBudget.SplitDataByteCount + 5));
    }

    [Fact]
    public void WorstCaseFrameLength_IsNeverShorterThanTheFrameTheProducerMakes()
    {
        // The bound has to hold for data the fixtures above do not name, or splitting to it proves
        // nothing. Walk every record length the split can emit and the worst byte pattern for it.
        for (var length = 1; length <= Pic32BootloaderFrameBudget.MaxSafeRecordLength; length++)
        {
            var frame = Pic32BootloaderMessageProducer.CreateProgramFlashMessage(Repeat(DLE, length));

            Assert.True(
                frame.Length <= Pic32BootloaderFrameBudget.WorstCaseFrameLength(length),
                $"A {length}-byte record framed to {frame.Length} bytes, over the worst case of " +
                $"{Pic32BootloaderFrameBudget.WorstCaseFrameLength(length)}.");
            Assert.True(frame.Length <= OneReport, $"A {length}-byte record framed to {frame.Length} bytes.");
        }
    }

    // ---------------------------------------------------------------------------------------
    // Splitting a record that is too long
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void FitRecordsToOneReport_LeavesARecordThatAlreadyFitsExactlyAsItIs()
    {
        var short8 = DataRecord(0x0020, Repeat(0x55, 8));
        var xc32 = MixedRecord();

        var fitted = Pic32BootloaderFrameBudget.FitRecordsToOneReport([short8, xc32]);

        Assert.Equal(2, fitted.Count);
        Assert.Same(short8, fitted[0]);
        Assert.Same(xc32, fitted[1]);
    }

    [Fact]
    public void FitRecordsToOneReport_SplitsA32ByteRecordIntoTwo16ByteRecords()
    {
        var oversized = DataRecord(0x1234, Repeat(DLE, 32));

        var fitted = Pic32BootloaderFrameBudget.FitRecordsToOneReport([oversized]);

        // Byte-exact, because the offset address and the checksum are rewritten and a wrong one
        // would program the right bytes at the wrong place.
        Assert.Equal(2, fitted.Count);
        Assert.Equal(DataRecord(0x1234, Repeat(DLE, 16)), fitted[0]);
        Assert.Equal(DataRecord(0x1244, Repeat(DLE, 16)), fitted[1]);
    }

    [Fact]
    public void FitRecordsToOneReport_SplitsAnUnevenRecordAndKeepsTheTail()
    {
        var oversized = DataRecord(0x0100, Repeat(DLE, 24));

        var fitted = Pic32BootloaderFrameBudget.FitRecordsToOneReport([oversized]);

        Assert.Equal(2, fitted.Count);
        Assert.Equal(DataRecord(0x0100, Repeat(DLE, 16)), fitted[0]);
        Assert.Equal(DataRecord(0x0110, Repeat(DLE, 8)), fitted[1]);
    }

    [Fact]
    public void FitRecordsToOneReport_ProgramsTheSameBytesAtTheSameAddresses()
    {
        // The split must not change the image. Rebuild the data and the addresses from the records
        // it produced and compare them with the record it was given.
        var data = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var oversized = DataRecord(0x0040, data);

        var fitted = Pic32BootloaderFrameBudget.FitRecordsToOneReport([oversized]);

        var rebuilt = new List<byte>();
        var nextOffset = 0x0040;
        foreach (var record in fitted)
        {
            Assert.Equal(nextOffset, (record[1] << 8) | record[2]);
            Assert.Equal(0x00, record[3]);
            Assert.Equal(record.Length - 5, record[0]);
            Assert.Equal(0, record.Aggregate(0, (sum, b) => (sum + b) & 0xFF));

            rebuilt.AddRange(record.Skip(4).Take(record[0]));
            nextOffset += record[0];
        }

        Assert.Equal(data, rebuilt);
    }

    [Fact]
    public void FitRecordsToOneReport_MakesEverySplitRecordFitOneReport()
    {
        // Every data length a 16-bit byte count can hold, filled with the bytes that escape worst.
        for (var dataLength = 1; dataLength <= 255; dataLength++)
        {
            var fitted = Pic32BootloaderFrameBudget.FitRecordsToOneReport(
                [DataRecord(0x0000, Repeat(DLE, dataLength))]);

            foreach (var record in fitted)
            {
                var frame = Pic32BootloaderMessageProducer.CreateProgramFlashMessage(record);
                Assert.True(
                    frame.Length <= OneReport,
                    $"A {dataLength}-byte record split to a record that framed to {frame.Length} bytes.");
            }
        }
    }

    public static TheoryData<string, byte[]> RecordsThatCannotBeSplit => new()
    {
        // Too long, and not a data record, so the byte count and address cannot be rewritten.
        { "an end-of-file record of impossible length", OversizedRecordOfType(0x01) },
        { "an extended-address record of impossible length", OversizedRecordOfType(0x04) },
        // A data record whose byte count disagrees with the bytes it carries.
        { "a data record with a wrong byte count", WrongByteCountRecord() },
        // A data record whose bytes run past the end of its 64 KB address block.
        { "a data record that wraps its address block", DataRecord(0xFFF0, Repeat(DLE, 32)) }
    };

    [Theory]
    [MemberData(nameof(RecordsThatCannotBeSplit))]
    public void FitRecordsToOneReport_RefusesARecordItCannotSplit(string description, byte[] record)
    {
        var error = Assert.Throws<InvalidDataException>(
            () => Pic32BootloaderFrameBudget.FitRecordsToOneReport([record]));

        Assert.Contains("the device flash is untouched", error.Message, StringComparison.Ordinal);
        Assert.NotEmpty(description);
    }

    // ---------------------------------------------------------------------------------------
    // The pre-flight: the refusal happens before the erase
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void PrepareHexImage_WithA32ByteRecord_HandsBackRecordsThatEachFitInOneReport()
    {
        // The image from the issue: one legal 32-byte data record, every data byte needing an
        // escape. Unsplit it frames to 74 bytes, which no report carries.
        var transport = new FakeHidTransport();
        var session = CreateSession(transport, new Pic32BootloaderProtocol());

        var (hexRecords, _, totalBytes) = session.PrepareHexImage([
            ":200000001010101010101010101010101010101010101010101010101010101010101010E0",
            ":00000001FF"
        ]);

        // Two 16-byte program records out of the one 32-byte record, then the end-of-file record.
        Assert.Equal(3, hexRecords.Count);
        Assert.Equal(47, totalBytes);

        var frameLengths = hexRecords
            .Select(record => Pic32BootloaderMessageProducer.CreateProgramFlashMessage(record).Length)
            .ToArray();

        Assert.Equal([43, 44, 11], frameLengths);
        Assert.All(frameLengths, length => Assert.True(length <= OneReport, $"A frame was {length} bytes."));

        // Nothing was asked of the device to work any of that out.
        Assert.Empty(transport.Writes);
        Assert.Equal(0, transport.ConnectAttempts);
    }

    [Fact]
    public void PrepareHexImage_WithARecordItCannotSplit_RefusesBeforeTouchingTheDevice()
    {
        var transport = new FakeHidTransport();
        var session = CreateSession(transport, new FakeBootloaderProtocol([
            [0xA1, 0x02],
            OversizedRecordOfType(0x01)
        ]));

        var error = Assert.Throws<InvalidDataException>(() => session.PrepareHexImage(["dummy"]));

        Assert.Contains("the device flash is untouched", error.Message, StringComparison.Ordinal);
        Assert.Empty(transport.Writes);
        Assert.Equal(0, transport.ConnectAttempts);
    }

    [Fact]
    public async Task UpdateFirmwareAsync_WithARecordItCannotSplit_SendsNoEraseAtAll()
    {
        // The ordered list of what reached the device. It has to be empty: the erase is the
        // command that makes the board unbootable, so the refusal has to come first.
        var hidTransport = new FakeHidTransport();
        var service = CreateService(
            hidTransport,
            new FakeBootloaderProtocol([[0xA1, 0x02], OversizedRecordOfType(0x01)]));

        var hexPath = WriteTempHexFile(":00000001FF");
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(
                () => service.UpdateFirmwareAsync(new FakeStreamingDevice("COM3"), hexPath));
        }
        finally
        {
            File.Delete(hexPath);
        }

        // FakeBootloaderProtocol stamps each command with its own first byte: 0x11 version,
        // 0x22 erase, 0x33 program, 0x44 read CRC, 0x55 jump. None of them went out.
        Assert.Empty(hidTransport.Writes);
        Assert.Equal(0, hidTransport.ConnectAttempts);
        Assert.Equal(FirmwareUpdateState.Idle, service.CurrentState);
    }

    [Fact]
    public async Task UpdateFirmwareAsync_WithA32ByteRecord_WritesTheEraseThenOneReportPerRecord()
    {
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0x01, 0x10]); // version
        hidTransport.EnqueueRead([0x01, 0x02]); // erase ack
        hidTransport.EnqueueRead([0x01, 0x03]); // program ack, first 16 bytes
        hidTransport.EnqueueRead([0x01, 0x03]); // program ack, second 16 bytes
        hidTransport.EnqueueRead([0x01, 0x03]); // program ack, end-of-file record
        hidTransport.EnqueueRead([0x01, 0x10]); // version, read again to verify

        var service = CreateService(hidTransport, new Pic32BootloaderProtocol());

        var hexPath = WriteTempHexFile(
            ":200000001010101010101010101010101010101010101010101010101010101010101010E0",
            ":00000001FF");
        try
        {
            await service.UpdateFirmwareAsync(new FakeStreamingDevice("COM3"), hexPath);
        }
        finally
        {
            File.Delete(hexPath);
        }

        Assert.Equal(FirmwareUpdateState.Complete, service.CurrentState);

        // The opcode of every frame, in the order the device saw it. One program frame per record,
        // and three records because the 32-byte one was split in two before the erase went out.
        Assert.Equal<byte[]>(
            [0x01, 0x02, 0x03, 0x03, 0x03, 0x01, 0x05],
            hidTransport.Writes.Select(OpcodeOf).ToArray());

        // One HID report each, in the same order: version 7, erase 5, the two split records 43
        // and 44, the end-of-file record 11, version again 7, jump 5. This is what failed before
        // #840: the unsplit 32-byte record framed to 74 bytes and the transport refused that
        // write, after the erase had already run.
        Assert.Equal([7, 5, 43, 44, 11, 7, 5], hidTransport.Writes.Select(w => w.Length).ToArray());
        Assert.All(
            hidTransport.Writes,
            write => Assert.True(write.Length <= OneReport, $"A frame of {write.Length} bytes reached the transport."));
    }

    // ---------------------------------------------------------------------------------------
    // Fixtures and helpers
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Builds a valid Intel HEX data record: byte count, 16-bit offset, record type 0x00, the
    /// data, and the two's-complement checksum.
    /// </summary>
    private static byte[] DataRecord(int offset, byte[] data)
    {
        var record = new byte[data.Length + 5];
        record[0] = (byte)data.Length;
        record[1] = (byte)(offset >> 8);
        record[2] = (byte)offset;
        record[3] = 0x00;
        Array.Copy(data, 0, record, 4, data.Length);

        byte sum = 0;
        for (var i = 0; i < record.Length - 1; i++)
        {
            sum += record[i];
        }

        record[^1] = (byte)-sum;
        return record;
    }

    /// <summary>A 16-byte data record whose data mixes bytes that need an escape with bytes that do not.</summary>
    private static byte[] MixedRecord() => DataRecord(
        0x0100,
        [0x55, SOH, 0x66, EOT, 0x77, DLE, 0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF, 0x22, 0x33]);

    /// <summary>A record of a non-data type, long enough that it cannot fit one report.</summary>
    private static byte[] OversizedRecordOfType(byte recordType)
    {
        var record = Repeat(DLE, 37);
        record[0] = 0x20;
        record[1] = 0x00;
        record[2] = 0x00;
        record[3] = recordType;
        return record;
    }

    /// <summary>A data record whose byte-count field disagrees with the data it carries.</summary>
    private static byte[] WrongByteCountRecord()
    {
        var record = DataRecord(0x0000, Repeat(DLE, 32));
        record[0] = 0x10;
        return record;
    }

    private static byte[] Repeat(byte value, int count) => Enumerable.Repeat(value, count).ToArray();

    /// <summary>Strips the framing and the escapes and returns the opcode the frame carries.</summary>
    private static byte OpcodeOf(byte[] frame) => frame[1] == DLE ? frame[2] : frame[1];

    private static Pic32BootloaderSession CreateSession(FakeHidTransport transport, IBootloaderProtocol protocol)
    {
        var context = new FirmwareUpdateContext(new object(), NullLogger.Instance, FastOptions());

        return new Pic32BootloaderSession(
            context,
            transport,
            protocol,
            new FakeHidDeviceEnumerator([], [new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN-1", "DAQiFi Bootloader")]),
            new FakeUsbLocationProvider(new Dictionary<string, string>()));
    }

    private static FirmwareUpdateService CreateService(
        FakeHidTransport hidTransport,
        IBootloaderProtocol protocol)
    {
        return new FirmwareUpdateService(
            hidTransport,
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            protocol,
            new FakeHidDeviceEnumerator([
                Array.Empty<HidDeviceInfo>(),
                [new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN-1", "DAQiFi Bootloader")]
            ]),
            FastOptions());
    }

    private static string WriteTempHexFile(params string[] lines)
    {
        var path = Path.GetTempFileName();
        File.WriteAllLines(path, lines);
        return path;
    }

    private static FirmwareUpdateServiceOptions FastOptions() => new()
    {
        PollInterval = TimeSpan.FromMilliseconds(5),
        PreparingDeviceTimeout = TimeSpan.FromSeconds(5),
        WaitingForBootloaderTimeout = TimeSpan.FromSeconds(5),
        ConnectingTimeout = TimeSpan.FromSeconds(5),
        ErasingFlashTimeout = TimeSpan.FromSeconds(5),
        ProgrammingTimeout = TimeSpan.FromSeconds(10),
        VerifyingTimeout = TimeSpan.FromSeconds(5),
        JumpingToApplicationTimeout = TimeSpan.FromSeconds(5),
        BootloaderResponseTimeout = TimeSpan.FromMilliseconds(250),
        PostForceBootDelay = TimeSpan.FromMilliseconds(5),
        PostLanFirmwareModeDelay = TimeSpan.FromMilliseconds(5),
        PostWifiReconnectDelay = TimeSpan.FromMilliseconds(5),
        HidConnectRetryDelay = TimeSpan.FromMilliseconds(5),
        FlashWriteRetryDelay = TimeSpan.FromMilliseconds(5),
        PostLanDisconnectPortReleaseDelay = TimeSpan.Zero,
        WincBootPromptResponseDelay = TimeSpan.Zero,
        WifiFlashRetryDelay = TimeSpan.FromMilliseconds(5),
        PostReconnectStaleHandleDelay = TimeSpan.Zero
    };
}
