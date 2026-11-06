using System.IO;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device.Discovery;
using Daqifi.Core.Firmware;
using Microsoft.Extensions.Logging.Abstractions;

namespace Daqifi.Core.Tests.Firmware;

/// <summary>
/// What the library does with a HEX image that puts bytes where the PIC32 bootloader will not
/// write them (#854).
/// </summary>
/// <remarks>
/// <para>
/// The bootloader erases and programs only the lower program-flash panel, physical
/// <c>0x1D000000</c>-<c>0x1D0FFFFF</c>, so that the NVM settings pages at <c>0x1D1E0000</c> survive
/// a customer update (firmware #909). A record above that panel, or in boot flash, is either
/// dropped with an ACK or failed outright by the device, and either way the board is left holding
/// an incomplete image after its application flash has already been erased. A half-flashed PIC32
/// boots garbage on the next power cycle and needs the firmware repository's physical button-hold
/// sequence to recover, so the image has to be refused before the erase, not after it.
/// </para>
/// <para>
/// Every fixture here is text. No test in this file touches a device, because a real attempt would
/// erase the board first, and that erase is the thing these tests exist to prevent.
/// </para>
/// </remarks>
public class Pic32FlashWindowTests
{
    /// <summary>Opcodes the real protocol stamps on each frame, in the frame's second byte.</summary>
    private const byte ReadVersionOpcode = 0x01;

    private const byte EraseFlashOpcode = 0x02;
    private const byte ProgramFlashOpcode = 0x03;
    private const byte DleEscape = 0x10;

    /// <summary>The message fragment every out-of-window refusal carries.</summary>
    internal const string RefusalFragment = "places data outside the flash the bootloader programs";

    private readonly Pic32BootloaderProtocol _protocol = new();

    // ---------------------------------------------------------------------------------------
    // Addresses the image may not reach
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A 16-byte data record at each address an image must not place bytes at, named by what is
    /// there. Every row is paired with a valid lower-panel record in the fixtures below, so each
    /// one fails for its address and not for being an image with nothing in it (#838).
    /// </summary>
    public static TheoryData<string, ushort, ushort> AddressesOutsideTheWritableWindow => new()
    {
        // The upper program-flash panel: application code space the bootloader stopped erasing.
        { "the middle of the upper flash panel", 0x1D15, 0x0000 },
        { "the first address of the upper flash panel", 0x1D10, 0x0000 },
        // Boot flash: silently dropped by the device, with the command still ACKed (#764).
        { "boot flash", 0x1FC0, 0x0000 },
        // Linker-emitted data below application flash.
        { "address zero, below application flash", 0x0000, 0x0000 },
        // A record whose start is the last 8 bytes of the panel but which runs 8 bytes past its
        // end. The device writes per word, so its tail would be dropped while the host counted
        // the whole record as programmed.
        { "a span straddling the top of the lower panel", 0x1D0F, 0xFFF8 }
    };

    [Theory]
    [MemberData(nameof(AddressesOutsideTheWritableWindow))]
    public void ParseHexFile_WithADataRecordOutsideTheWritableWindow_RefusesTheImage(
        string what,
        ushort baseAddress,
        ushort offset)
    {
        var lines = ImageWithOneOutOfWindowRecord(baseAddress, offset);

        var error = Assert.Throws<InvalidDataException>(() => _protocol.ParseHexFile(lines));

        Assert.Contains(RefusalFragment, error.Message, StringComparison.Ordinal);

        // The refusal names the offending record, so whoever reads it can find it in the file,
        // and it names the window, so the message says where the record should have been.
        var offendingAddress = ((uint)baseAddress << 16) | offset;
        Assert.True(
            error.Message.Contains($"0x{offendingAddress:X8}", StringComparison.Ordinal)
            && error.Message.Contains("0x1D000000-0x1D0FFFFF", StringComparison.Ordinal),
            $"The refusal for a record at {what} (0x{offendingAddress:X8}) should name both that " +
            $"address and the writable window, and reads: {error.Message}");
    }

    [Theory]
    [MemberData(nameof(AddressesOutsideTheWritableWindow))]
    public void ComputeCrcRegions_WithADataRecordOutsideTheWritableWindow_RefusesTheImage(
        string what,
        ushort baseAddress,
        ushort offset)
    {
        Assert.NotEmpty(what);
        var lines = ImageWithOneOutOfWindowRecord(baseAddress, offset);

        // Before #854 this quietly left the record out of the regions and returned a CRC over the
        // rest, so the flow erased the board and programmed a partial image that verified clean.
        var error = Assert.Throws<InvalidDataException>(() => _protocol.ComputeCrcRegions(lines));

        Assert.Contains(RefusalFragment, error.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // Addresses the image may reach
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ParseHexFile_WithALowerPanelOnlyImage_IsAccepted()
    {
        var lines = new[]
        {
            ExtendedAddressLine(0x1D00),
            DataLine(0x0000, Bytes(16)),
            EndOfFileLine
        };

        var records = _protocol.ParseHexFile(lines);

        Assert.Equal(3, records.Count);
    }

    [Fact]
    public void ParseHexFile_WithAReleaseShapedImage_IsAccepted()
    {
        // The shape every real release image has, checked against the published .hex files for
        // 3.5.0, 3.7.2 and 3.8.0: a 408-byte reset vector at 0x1D000000, then the bulk from
        // 0x1D000480 up. None of the three carries a byte above 0x1D0B8A07, in boot flash, below
        // application flash, or in the protected range - two of them predate the #909 erase
        // change and the release gate that now forbids it, and they are clean anyway. So the
        // window check costs a real update nothing.
        var lines = new List<string> { ExtendedAddressLine(0x1D00) };
        for (ushort offset = 0; offset < 408; offset += 8)
        {
            lines.Add(DataLine(offset, Bytes(8)));
        }

        lines.Add(DataLine(0x0480, Bytes(16)));
        lines.Add(ExtendedAddressLine(0x1D0B));
        lines.Add(DataLine(0x8A00, Bytes(8)));
        lines.Add(EndOfFileLine);

        var records = _protocol.ParseHexFile([.. lines]);

        Assert.Equal(lines.Count, records.Count);

        // The reset vector coalesces into one region, and the two later records into their own.
        var regions = _protocol.ComputeCrcRegions([.. lines]);
        Assert.Equal(3, regions.Count);
        Assert.Equal(0x9D000000u, regions[0].Address);
        Assert.Equal(408u, regions[0].Length);
        Assert.Equal(0x9D000480u, regions[1].Address);
        Assert.Equal(0x9D0B8A00u, regions[2].Address);
    }

    [Fact]
    public void ParseHexFile_WithARecordEndingOnTheLastByteOfThePanel_IsAccepted()
    {
        // 0x1D0FFFF8..0x1D0FFFFF is the last word-aligned 8 bytes of the lower panel. The same
        // base and offset with 16 bytes is refused (the straddling row above), so this pins that
        // the bound is the panel's last byte and not its last record.
        var lines = new[]
        {
            ExtendedAddressLine(0x1D0F),
            DataLine(0xFFF8, Bytes(8)),
            EndOfFileLine
        };

        var records = _protocol.ParseHexFile(lines);

        Assert.Equal(3, records.Count);

        var region = Assert.Single(_protocol.ComputeCrcRegions(lines));
        Assert.Equal(0x9D0FFFF8u, region.Address);
        Assert.Equal(8u, region.Length);
    }

    [Fact]
    public void ParseHexFile_WithRecordsInTheProtectedRange_DropsThemRatherThanRefusingTheImage()
    {
        // The default protected range starts at 0x1D1E0000, which is above the writable window, so
        // the order of the two checks decides what happens to an image carrying records there:
        // dropped, as this library already shipped and as the bootloader's deliberate refusal to
        // erase that flash implies, or refused outright. The three release images checked while
        // writing this (3.5.0, 3.7.2, 3.8.0) carry nothing above 0x1D0B8A07, so this order is
        // about honouring the shipped contract and the custom-range constructor, not about
        // rescuing an image anyone has actually seen.
        var lines = new[]
        {
            ExtendedAddressLine(0x1D00),
            DataLine(0x0000, Bytes(16)),
            ExtendedAddressLine(0x1D1E),
            DataLine(0x0000, Bytes(16)),
            EndOfFileLine
        };

        var records = _protocol.ParseHexFile(lines);

        // The address record and data record of the protected page are both gone; the lower-panel
        // pair and the end-of-file record are not.
        Assert.Equal(4, records.Count);
        Assert.Single(records, record => record[3] == 0x00 && record[0] == 16);
    }

    // ---------------------------------------------------------------------------------------
    // The CRC window
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ComputeCrcRegions_CoversTheLowerPanelOnly()
    {
        // A custom protected range outside the window keeps the protected-range filter out of the
        // way, so this tests the window bound on its own. Before #854 the window ended at
        // 0x1D1FFFFF, so the upper-panel record below produced a second CRC region covering flash
        // the bootloader never erases - a CRC the device can only fail.
        var protocol = new Pic32BootloaderProtocol(0x00010000, 0x00020000);
        var withinThePanel = new[]
        {
            ExtendedAddressLine(0x1D00),
            DataLine(0x0000, Bytes(8)),
            EndOfFileLine
        };

        var region = Assert.Single(protocol.ComputeCrcRegions(withinThePanel));
        Assert.Equal(0x9D000000u, region.Address);
        Assert.Equal(8u, region.Length);

        string[] reachingTheUpperPanel =
        [
            ExtendedAddressLine(0x1D00),
            DataLine(0x0000, Bytes(8)),
            ExtendedAddressLine(0x1D1F),
            DataLine(0x0000, Bytes(8)),
            EndOfFileLine
        ];

        var error = Assert.Throws<InvalidDataException>(() => protocol.ComputeCrcRegions(reachingTheUpperPanel));
        Assert.Contains("0x1D1F0000", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0x1D000000u, 1u, true)]
    [InlineData(0x1D0FFFFFu, 1u, true)]
    [InlineData(0x1D0FFFF8u, 8u, true)]
    [InlineData(0x1D0FFFF8u, 9u, false)]
    [InlineData(0x1D100000u, 1u, false)]
    [InlineData(0x1CFFFFFFu, 1u, false)]
    [InlineData(0xFFFFFFF0u, 32u, false)]
    public void ContainsWholeSpan_IsTheLowerPanelAndNothingElse(uint start, uint length, bool expected)
    {
        // The last case runs the span past the top of the address space; the answer must be "no"
        // rather than an overflowed address that lands back inside the panel.
        Assert.Equal(expected, Pic32ApplicationFlash.ContainsWholeSpan(start, length));
    }

    [Fact]
    public void WritableWindowConstants_MatchTheFirmwareHeaders()
    {
        // system_config.h: APP_FLASH_BASE_ADDRESS (0x9D000000), APP_FLASH_LOWER_PANEL_SIZE
        // (0x100000) and APP_FLASH_END_ADDRESS (base + size - 1). These are those three, physical.
        Assert.Equal(0x1D000000u, Pic32ApplicationFlash.PhysicalStart);
        Assert.Equal(0x100000u, Pic32ApplicationFlash.LowerPanelSize);
        Assert.Equal(0x1D0FFFFFu, Pic32ApplicationFlash.PhysicalEnd);
        Assert.Equal(0x9D000000u, Pic32ApplicationFlash.Kseg0Start);
        Assert.Equal(0x9D0FFFFFu, Pic32ApplicationFlash.Kseg0End);
    }

    // ---------------------------------------------------------------------------------------
    // The whole flow: no device command at all
    // ---------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AddressesOutsideTheWritableWindow))]
    public async Task UpdateFirmwareAsync_WithAnOutOfWindowImage_SendsNoEraseAtAll(
        string what,
        ushort baseAddress,
        ushort offset)
    {
        var hidTransport = new FakeHidTransport();

        // Queued so that the old code could run to completion: version, erase ack, program acks,
        // version again for the verify pass. The fix makes all of them unused.
        hidTransport.EnqueueRead(FrameReply([ReadVersionOpcode, 3, 1]));
        hidTransport.EnqueueRead(FrameReply([EraseFlashOpcode]));
        hidTransport.EnqueueRead(FrameReply([ProgramFlashOpcode]));
        hidTransport.EnqueueRead(FrameReply([ProgramFlashOpcode]));
        hidTransport.EnqueueRead(FrameReply([ReadVersionOpcode, 3, 1]));

        var service = CreateService(hidTransport);
        var device = new FakeStreamingDevice("COM3");
        var hexPath = WriteTempHexFile(ImageWithOneOutOfWindowRecord(baseAddress, offset));

        try
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(
                () => service.UpdateFirmwareAsync(device, hexPath));

            Assert.Contains(RefusalFragment, error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(hexPath);
        }

        // The ordered evidence, and the assertion that fails against the unchanged code: before
        // #854 an image with a record at an address like this one erased the board and programmed
        // what it could, and for an upper-panel record the CRC pass then failed on a board with no
        // working application on it.
        var opcodes = hidTransport.Writes.Select(OpcodeOf).ToArray();
        Assert.True(
            opcodes.Length == 0,
            $"The device saw {opcodes.Length} command(s) for an image with a record at {what}: " +
            $"[{string.Join(", ", opcodes.Select(opcode => $"0x{opcode:X2}"))}]. " +
            $"{(opcodes.Contains(EraseFlashOpcode) ? "One of them is the erase." : string.Empty)}");

        // The refusal happens before the flow opens the bootloader at all, so the device never
        // even left the application.
        Assert.Equal(0, hidTransport.ConnectAttempts);
        Assert.Empty(device.SentCommands);
        Assert.Equal(FirmwareUpdateState.Idle, service.CurrentState);
    }

    // ---------------------------------------------------------------------------------------
    // Fixtures and helpers
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A valid 16-byte lower-panel record, then one 16-byte record at the given address. The
    /// valid record is there so a refusal can only be about the second record's address.
    /// </summary>
    private static string[] ImageWithOneOutOfWindowRecord(ushort baseAddress, ushort offset) =>
    [
        ExtendedAddressLine(0x1D00),
        DataLine(0x0000, Bytes(16)),
        ExtendedAddressLine(baseAddress),
        DataLine(offset, Bytes(16)),
        EndOfFileLine
    ];

    private static byte[] Bytes(int count) =>
        Enumerable.Range(0, count).Select(value => (byte)value).ToArray();

    private const string EndOfFileLine = ":00000001FF";

    /// <summary>A type-04 extended-linear-address record setting the high 16 bits of the address.</summary>
    private static string ExtendedAddressLine(ushort baseAddress) =>
        HexLine(0x04, 0x0000, [(byte)(baseAddress >> 8), (byte)baseAddress]);

    /// <summary>A type-00 data record at <paramref name="offset"/> within the current page.</summary>
    private static string DataLine(ushort offset, byte[] data) => HexLine(0x00, offset, data);

    /// <summary>
    /// Builds one Intel HEX line - byte count, big-endian offset, record type, data - and computes
    /// its two's-complement checksum, so a fixture here is written in addresses rather than in
    /// hand-totalled hex.
    /// </summary>
    private static string HexLine(byte recordType, ushort offset, byte[] data)
    {
        byte[] body = [(byte)data.Length, (byte)(offset >> 8), (byte)offset, recordType, .. data];

        byte sum = 0;
        foreach (var value in body)
        {
            sum += value;
        }

        return ":" + Convert.ToHexString([.. body, unchecked((byte)-sum)]);
    }

    /// <summary>Strips the framing and the escapes and returns the opcode the frame carries.</summary>
    private static byte OpcodeOf(byte[] frame) => frame[1] == DleEscape ? frame[2] : frame[1];

    private static byte[] FrameReply(byte[] content)
    {
        var frameCrc = new Crc16(content).Crc;
        byte[] body = [.. content, (byte)frameCrc, (byte)(frameCrc >> 8)];
        var frame = new List<byte> { 0x01 };
        foreach (var value in body)
        {
            if (value is 0x01 or 0x04 or 0x10)
            {
                frame.Add(DleEscape);
            }

            frame.Add(value);
        }

        frame.Add(0x04);
        return frame.ToArray();
    }

    private static FirmwareUpdateService CreateService(FakeHidTransport hidTransport)
    {
        return new FirmwareUpdateService(
            hidTransport,
            new FakeFirmwareDownloadService(),
            new FakeExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance,
            new Pic32BootloaderProtocol(),
            new FakeHidDeviceEnumerator([
                Array.Empty<HidDeviceInfo>(),
                [new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN-1", "DAQiFi Bootloader")]
            ]),
            FastOptions());
    }

    private static string WriteTempHexFile(string[] lines)
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
