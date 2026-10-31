using System.IO;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device.Discovery;
using Daqifi.Core.Firmware;
using Microsoft.Extensions.Logging.Abstractions;

namespace Daqifi.Core.Tests.Firmware;

/// <summary>
/// What the library does with a HEX image that has no firmware data in it (#838).
/// </summary>
/// <remarks>
/// <para>
/// An Intel HEX file holds records of several types, and only a type-00 data record carries bytes
/// to program. A file can therefore be a valid HEX file, and parse without complaint, and still
/// program nothing: an end-of-file record on its own, an extended-linear-address record with an
/// end-of-file record, a data record whose byte count is zero. The old guard summed the length of
/// every record it kept, so each of those files gave a byte total above zero. The flow then erased
/// the application flash and wrote nothing into it.
/// </para>
/// <para>
/// Every fixture here is text. No test in this file touches a device, because a real attempt would
/// erase the board first, and that erase is the thing these tests exist to prevent.
/// </para>
/// </remarks>
public class Pic32BootloaderEmptyImageTests
{
    /// <summary>Opcodes the real protocol stamps on each frame, in the frame's second byte.</summary>
    private const byte ReadVersionOpcode = 0x01;

    private const byte EraseFlashOpcode = 0x02;
    private const byte ProgramFlashOpcode = 0x03;
    private const byte JumpToApplicationOpcode = 0x05;

    private const byte DleEscape = 0x10;

    // ---------------------------------------------------------------------------------------
    // Images with nothing to program
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// HEX files that carry no byte of firmware, with the shape of each one named. A real image
    /// ends with the end-of-file record too, so every row but the first two is a file a build or
    /// a packaging step can plausibly produce.
    /// </summary>
    public static TheoryData<string, string[]> ImagesWithNothingToProgram => new()
    {
        { "an empty file", [] },
        { "blank lines only", ["", "   ", ""] },
        { "an end-of-file record only", [":00000001FF"] },
        {
            "an extended-linear-address record and an end-of-file record",
            [":020000041D00DD", ":00000001FF"]
        },
        {
            "a data record with a byte count of zero, and an end-of-file record",
            [":0000000000", ":00000001FF"]
        },
        {
            "every data record inside the protected calibration range",
            // The parser drops a type-00 record at 0x1D1E0000..0x1D200000, so what reaches the
            // bootloader session is the address record and the end-of-file record.
            [":020000041D1EBF", ":10000000000102030405060708090A0B0C0D0E0F78", ":00000001FF"]
        }
    };

    [Theory]
    [MemberData(nameof(ImagesWithNothingToProgram))]
    public async Task UpdateFirmwareAsync_WithAnImageThatProgramsNothing_SendsNoEraseAtAll(
        string shape,
        string[] hexLines)
    {
        var hidTransport = new FakeHidTransport();

        // Queued so that the old code could run to completion: version, erase ack, program ack,
        // version again for the verify pass. The fix makes all of them unused.
        hidTransport.EnqueueRead([0x01, 0x10]);
        hidTransport.EnqueueRead([0x01, 0x02]);
        hidTransport.EnqueueRead([0x01, 0x03]);
        hidTransport.EnqueueRead([0x01, 0x10]);

        var service = CreateService(hidTransport);
        var device = new FakeStreamingDevice("COM3");
        var hexPath = WriteTempHexFile(hexLines);

        try
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(
                () => service.UpdateFirmwareAsync(device, hexPath));

            Assert.Contains("did not contain any writable records", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(hexPath);
        }

        // The ordered evidence. Before the fix this list read 0x01 version, 0x02 erase, 0x03
        // program, 0x01 version, 0x05 jump for every shape above except the two that parsed to no
        // records at all. The erase at position two is the damage: it clears the application
        // flash, and the image had nothing to put back.
        Assert.Empty(hidTransport.Writes.Select(OpcodeOf));
        Assert.DoesNotContain(EraseFlashOpcode, hidTransport.Writes.Select(OpcodeOf));

        // The refusal happens before the flow opens the bootloader at all, so the device never
        // even left the application. The shape is named so a failure says which file did it.
        Assert.Equal(0, hidTransport.ConnectAttempts);
        Assert.Empty(device.SentCommands);
        Assert.Equal(FirmwareUpdateState.Idle, service.CurrentState);
        Assert.False(string.IsNullOrEmpty(shape));
    }

    // ---------------------------------------------------------------------------------------
    // An image that does program something is untouched
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task UpdateFirmwareAsync_WithOneDataRecord_StillErasesThenProgramsIt()
    {
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0x01, 0x10]); // version
        hidTransport.EnqueueRead([0x01, 0x02]); // erase ack
        hidTransport.EnqueueRead([0x01, 0x03]); // program ack, the data record
        hidTransport.EnqueueRead([0x01, 0x03]); // program ack, the end-of-file record
        hidTransport.EnqueueRead([0x01, 0x10]); // version, read again to verify

        var service = CreateService(hidTransport);
        var hexPath = WriteTempHexFile([
            ":10000000000102030405060708090A0B0C0D0E0F78",
            ":00000001FF"
        ]);

        try
        {
            await service.UpdateFirmwareAsync(new FakeStreamingDevice("COM3"), hexPath);
        }
        finally
        {
            File.Delete(hexPath);
        }

        Assert.Equal(FirmwareUpdateState.Complete, service.CurrentState);

        // One data record of 16 bytes is enough to program, so the erase still goes out and both
        // records still follow it. This is the row of the table that must not change.
        Assert.Equal<byte[]>(
            [
                ReadVersionOpcode,
                EraseFlashOpcode,
                ProgramFlashOpcode,
                ProgramFlashOpcode,
                ReadVersionOpcode,
                JumpToApplicationOpcode
            ],
            hidTransport.Writes.Select(OpcodeOf).ToArray());
    }

    [Fact]
    public async Task UpdateFirmwareAsync_WithOneDataRecordOfOneByte_StillProgramsIt()
    {
        // The smallest image that programs anything at all. One data byte has to be enough,
        // because the guard asks whether there is a byte to program, not how many.
        var hidTransport = new FakeHidTransport();
        hidTransport.EnqueueRead([0x01, 0x10]); // version
        hidTransport.EnqueueRead([0x01, 0x02]); // erase ack
        hidTransport.EnqueueRead([0x01, 0x03]); // program ack, the data record
        hidTransport.EnqueueRead([0x01, 0x03]); // program ack, the end-of-file record
        hidTransport.EnqueueRead([0x01, 0x10]); // version, read again to verify

        var service = CreateService(hidTransport);
        var hexPath = WriteTempHexFile([":010000007F80", ":00000001FF"]);

        try
        {
            await service.UpdateFirmwareAsync(new FakeStreamingDevice("COM3"), hexPath);
        }
        finally
        {
            File.Delete(hexPath);
        }

        Assert.Equal(FirmwareUpdateState.Complete, service.CurrentState);
        Assert.Contains(EraseFlashOpcode, hidTransport.Writes.Select(OpcodeOf));
    }

    // ---------------------------------------------------------------------------------------
    // The count itself
    // ---------------------------------------------------------------------------------------

    public static TheoryData<byte[][], long> DataByteCounts => new()
    {
        // Nothing to count.
        { [], 0 },
        // An end-of-file record: 5 bytes of record, no data byte.
        { [[0x00, 0x00, 0x00, 0x01, 0xFF]], 0 },
        // An extended-linear-address record: 7 bytes of record, no data byte.
        { [[0x02, 0x00, 0x00, 0x04, 0x1D, 0x00, 0xDD]], 0 },
        // A data record with a byte count of zero.
        { [[0x00, 0x00, 0x00, 0x00, 0x00]], 0 },
        // A data record of two bytes, with the address record and the end-of-file record it
        // normally travels with. Only the two data bytes count.
        {
            [
                [0x02, 0x00, 0x00, 0x04, 0x1D, 0x00, 0xDD],
                [0x02, 0x00, 0x00, 0x00, 0xAA, 0xBB, 0x99],
                [0x00, 0x00, 0x00, 0x01, 0xFF]
            ],
            2
        },
        // A record whose byte count disagrees with the data it carries is not counted. The parser
        // rejects such a record before this point, so the count only has to refuse to guess.
        { [[0x08, 0x00, 0x00, 0x00, 0xAA, 0xBB, 0x99]], 0 }
    };

    [Theory]
    [MemberData(nameof(DataByteCounts))]
    public void CountProgrammableDataBytes_CountsDataBytesAndNotRecordBytes(byte[][] records, long expected)
    {
        Assert.Equal(expected, Pic32BootloaderImageContent.CountProgrammableDataBytes(records));
    }

    [Fact]
    public void CountProgrammableDataBytes_WithARecordTooShortToClassify_CountsItAsProgrammable()
    {
        // A record this short cannot come from IntelHexParser, which refuses any line below 11
        // characters. The count treats it as programmable on purpose: the guard refuses only an
        // image it can show is empty, and guessing the other way would refuse a valid image.
        Assert.Equal(1, Pic32BootloaderImageContent.CountProgrammableDataBytes([[0x10]]));
    }

    // ---------------------------------------------------------------------------------------
    // Fixtures and helpers
    // ---------------------------------------------------------------------------------------

    /// <summary>Strips the framing and the escapes and returns the opcode the frame carries.</summary>
    private static byte OpcodeOf(byte[] frame) => frame[1] == DleEscape ? frame[2] : frame[1];

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
