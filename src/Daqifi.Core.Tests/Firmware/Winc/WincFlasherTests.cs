using Daqifi.Core.Firmware.Winc;

namespace Daqifi.Core.Tests.Firmware.Winc;

/// <summary>
/// Covers the flash-level read sequences and the two <see cref="IWincFlasher"/> implementations.
/// </summary>
public class WincFlasherTests
{
    private const uint TransferDoneRegister = 0x10218;
    private const uint DummyRegister = 0x1084;
    private const uint ShareMemoryBase = 0xD0000;

    /// <summary>
    /// Builds a fake whose transfer-done register always reads 1, so flash sequences complete.
    /// </summary>
    private static FakeWincSerialPort CreateReadyPort()
    {
        var port = new FakeWincSerialPort();
        port.Registers[TransferDoneRegister] = 1;
        return port;
    }

    private static WincFlashReader CreateReader(FakeWincSerialPort port)
        => new(new WincSerialBridgeClient(port, TimeSpan.FromSeconds(1)));

    [Theory]
    [InlineData(0x001003A0u, true)]  // bench-observed: halted in download mode
    [InlineData(0x00150000u, true)]  // firmware running
    [InlineData(0x0015FFFFu, true)]
    [InlineData(0x00000000u, false)] // nothing there
    [InlineData(0xFFFFFFFFu, false)] // floating bus
    [InlineData(0x00200000u, false)] // some other part
    public void IsKnownWincChipId_RecognizesOnlyTheWinc1500Families(uint chipId, bool expected)
    {
        Assert.Equal(expected, WincFlashReader.IsKnownWincChipId(chipId));
    }

    [Fact]
    public void ReadChipId_ReadsTheIdentityRegister()
    {
        var port = CreateReadyPort();
        port.Registers[WincFlashReader.ChipIdRegister] = 0x001003A0;

        Assert.Equal(0x001003A0u, CreateReader(port).ReadChipId());
    }

    [Fact]
    public void ReadFlash_ChunksBelowTheSizeThatWedgesTheDevice()
    {
        // A 5 KB read must never issue a single block request at or above 2048 bytes, because the
        // device's read loop would never terminate. This is the property that matters most here.
        var port = CreateReadyPort();
        port.Blocks[ShareMemoryBase] = new byte[WincBridgeProtocol.MaxReadBlockSize];

        CreateReader(port).ReadFlash(0, 5000);

        var blockReadSizes = port.ReceivedHeaders
            .Where(h => h[0] == (byte)WincBridgeProtocol.Command.ReadBlock)
            .Select(h => (h[3] << 8) | h[2])
            .ToList();

        Assert.NotEmpty(blockReadSizes);
        Assert.All(blockReadSizes, size => Assert.True(size <= WincBridgeProtocol.MaxReadBlockSize));
    }

    [Fact]
    public void ReadFlash_ReturnsExactlyTheRequestedLength()
    {
        var port = CreateReadyPort();
        port.Blocks[ShareMemoryBase] = Enumerable.Range(0, WincBridgeProtocol.MaxReadBlockSize)
            .Select(i => (byte)(i & 0xFF))
            .ToArray();

        var data = CreateReader(port).ReadFlash(0, 3000);

        Assert.Equal(3000, data.Length);
    }

    [Fact]
    public void ReadFlash_IssuesTheFastReadCommandWithTheAddressInTheControllerWord()
    {
        // Mirrors the WINC host driver's load-to-shared-memory sequence: opcode 0x0B in the low
        // byte, then the 24-bit flash address ascending.
        var port = CreateReadyPort();
        port.Blocks[ShareMemoryBase] = new byte[16];

        CreateReader(port).ReadFlash(0x123456, 16);

        var buffer1Write = port.ReceivedHeaders.First(h =>
            h[0] == (byte)WincBridgeProtocol.Command.WriteRegister &&
            (((uint)h[7] << 24) | ((uint)h[6] << 16) | ((uint)h[5] << 8) | h[4]) == 0x1020C);

        var commandWord = ((uint)buffer1Write[11] << 24) | ((uint)buffer1Write[10] << 16)
                        | ((uint)buffer1Write[9] << 8) | buffer1Write[8];

        Assert.Equal(0x0Bu, commandWord & 0xFF);
        Assert.Equal(0x12u, (commandWord >> 8) & 0xFF);
        Assert.Equal(0x34u, (commandWord >> 16) & 0xFF);
        Assert.Equal(0x56u, (commandWord >> 24) & 0xFF);
    }

    [Fact]
    public void ReadFlashJedecId_ReturnsTheControllerResult()
    {
        var port = CreateReadyPort();
        port.Registers[DummyRegister] = 0x00C22018;

        Assert.Equal(0x00C22018u, CreateReader(port).ReadFlashJedecId());
    }

    [Fact]
    public void ReadFlash_ThrowsWhenTheControllerNeverReportsDone()
    {
        // The firmware's own poll loop is unbounded; ours must not be, or a WINC that stops
        // answering would hang the host indefinitely.
        var port = new FakeWincSerialPort();
        port.Registers[TransferDoneRegister] = 0;

        var reader = new WincFlashReader(
            new WincSerialBridgeClient(port, TimeSpan.FromSeconds(1)), transferPollLimit: 3);

        var ex = Assert.Throws<TimeoutException>(() => reader.ReadFlash(0, 16));

        Assert.Contains("transfer-done", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ReadFlash_RejectsNonPositiveLengths(int length)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateReader(CreateReadyPort()).ReadFlash(0, length));
    }

    [Fact]
    public void ReadFlash_ObservesCancellation()
    {
        var port = CreateReadyPort();
        port.Blocks[ShareMemoryBase] = new byte[WincBridgeProtocol.MaxReadBlockSize];
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => CreateReader(port).ReadFlash(0, 5000, cts.Token));
    }

    // ---- WincModuleInspector -------------------------------------------------

    [Fact]
    public async Task Inspector_ReadIdentityAsync_HandshakesNegotiatesBaudThenReadsIdentity()
    {
        var port = CreateReadyPort();
        port.Registers[WincFlashReader.ChipIdRegister] = 0x001003A0;
        port.Registers[DummyRegister] = 0x00C22018;

        var flasher = new WincModuleInspector((_, _) => port, baudSettleDelay: TimeSpan.Zero);

        var identity = await flasher.ReadIdentityAsync("COM1");

        Assert.Equal(0x001003A0u, identity.ChipId);
        Assert.Equal(0x00C22018u, identity.FlashJedecId);
        Assert.True(identity.IsRecognizedWinc);
        Assert.Equal(WincModuleInspector.FastBaudRate, identity.NegotiatedBaudRate);
        Assert.Contains(WincModuleInspector.FastBaudRate, port.BaudRateHistory);
    }

    [Fact]
    public async Task Inspector_ReadIdentityAsync_ReportsAnUnrecognizedChip()
    {
        // Bridge up but WINC not answering is a distinct, actionable condition from a dead port.
        var port = CreateReadyPort();
        port.Registers[WincFlashReader.ChipIdRegister] = 0xFFFFFFFF;

        var flasher = new WincModuleInspector((_, _) => port, baudSettleDelay: TimeSpan.Zero);

        var identity = await flasher.ReadIdentityAsync("COM1");

        Assert.False(identity.IsRecognizedWinc);
    }

    [Fact]
    public async Task Inspector_ReadIdentityAsync_ExplainsWhenNoBridgeIsListening()
    {
        var port = new FakeWincSerialPort { SuppressIdentityResponse = true };
        var flasher = new WincModuleInspector((_, _) => port, baudSettleDelay: TimeSpan.Zero);

        var ex = await Assert.ThrowsAsync<IOException>(() => flasher.ReadIdentityAsync("COM1"));

        Assert.Contains("bridge", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(port.WasDisposed);
    }

    [Fact]
    public async Task Inspector_ReadIdentityAsync_RejectsAnEmptyPortName()
    {
        var flasher = new WincModuleInspector((_, _) => CreateReadyPort());

        await Assert.ThrowsAsync<ArgumentException>(() => flasher.ReadIdentityAsync("  "));
    }

    // ---- WincFlashToolLocator -------------------------------------------

    [Fact]
    public void Locator_IsAvailable_WhenTheFirmwarePathIsTheToolItself()
    {
        var toolPath = Path.Combine(Path.GetTempPath(), $"winc_flash_tool_{Guid.NewGuid():N}.cmd");
        File.WriteAllText(toolPath, "@echo off");

        try
        {
            var locator = new WincFlashToolLocator("winc_flash_tool.cmd");

            Assert.True(locator.IsAvailable(toolPath));
        }
        finally
        {
            File.Delete(toolPath);
        }
    }

    [Fact]
    public void Locator_IsAvailable_FindsTheToolBeneathADirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"winc_{Guid.NewGuid():N}");
        var nested = Path.Combine(root, "winc");
        Directory.CreateDirectory(nested);
        var toolPath = Path.Combine(nested, "winc_flash_tool.cmd");
        File.WriteAllText(toolPath, "@echo off");

        try
        {
            var locator = new WincFlashToolLocator("winc_flash_tool.cmd");

            Assert.True(locator.IsAvailable(root));
            Assert.True(locator.TryResolveToolPath(root, out var resolved));
            Assert.Equal(toolPath, resolved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Locator_IsNotAvailable_WhenTheToolIsMissing()
    {
        // This is the Linux/macOS case that motivates issue #271 — the answer must be a clean
        // "no", not an exception mid-update.
        var root = Path.Combine(Path.GetTempPath(), $"winc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var locator = new WincFlashToolLocator("winc_flash_tool.cmd");

            Assert.False(locator.IsAvailable(root));
            Assert.False(locator.TryResolveToolPath(root, out _));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Locator_IsNotAvailable_ForAPathThatDoesNotExist()
    {
        var locator = new WincFlashToolLocator("winc_flash_tool.cmd");

        Assert.False(locator.IsAvailable(Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}")));
    }

    [Fact]
    public void Locator_RejectsAnEmptyToolName()
    {
        Assert.Throws<ArgumentException>(() => new WincFlashToolLocator("  "));
    }


}
