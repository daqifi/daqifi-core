using Daqifi.Core.Firmware.Winc;

namespace Daqifi.Core.Tests.Firmware.Winc;

/// <summary>
/// Exercises the bridge client against a fake that re-implements the firmware's parser, so a
/// mis-framed command is rejected here the same way the device would reject it.
/// </summary>
public class WincSerialBridgeClientTests
{
    private static WincSerialBridgeClient CreateClient(FakeWincSerialPort port)
        => new(port, TimeSpan.FromSeconds(1));

    [Fact]
    public void TryHandshake_SucceedsWhenTheBridgeIdentifies()
    {
        var port = new FakeWincSerialPort();

        Assert.True(CreateClient(port).TryHandshake());
    }

    [Fact]
    public void TryHandshake_ReturnsFalseWhenNothingAnswers()
    {
        // A silent port means the device is not in bridge mode — a normal, recoverable condition,
        // so this reports rather than throws.
        var port = new FakeWincSerialPort { SuppressIdentityResponse = true };

        Assert.False(CreateClient(port).TryHandshake());
    }

    [Fact]
    public void TryHandshake_DiscardsStaleInputFirst()
    {
        var port = new FakeWincSerialPort();

        CreateClient(port).TryHandshake();

        Assert.True(port.DiscardCount >= 1);
    }

    [Fact]
    public void ReadRegister_RoundTripsABigEndianValue()
    {
        var port = new FakeWincSerialPort();
        port.Registers[0x1000] = 0x001003A0; // bench-observed WINC1500 chip id

        Assert.Equal(0x001003A0u, CreateClient(port).ReadRegister(0x1000));
    }

    [Fact]
    public void ReadRegister_SendsAWellFormedHeaderTheDeviceAccepts()
    {
        var port = new FakeWincSerialPort();
        port.Registers[0x10218] = 1;

        CreateClient(port).ReadRegister(0x10218);

        var header = Assert.Single(port.ReceivedHeaders);
        Assert.True(WincBridgeProtocol.IsHeaderValid(header));
        Assert.Equal((byte)WincBridgeProtocol.Command.ReadRegisterWithReturn, header[0]);
    }

    [Fact]
    public void WriteRegister_SendsValueAndAddressInTheHeader()
    {
        var port = new FakeWincSerialPort();

        CreateClient(port).WriteRegister(0x10208, 0x400);

        var header = Assert.Single(port.ReceivedHeaders);
        Assert.Equal((byte)WincBridgeProtocol.Command.WriteRegister, header[0]);
        Assert.Equal(0x08, header[4]);
        Assert.Equal(0x02, header[5]);
        Assert.Equal(0x01, header[6]);
        Assert.Equal(0x00, header[8]);
        Assert.Equal(0x04, header[9]);
    }

    [Fact]
    public void ReadBlock_ReturnsTheRequestedBytes()
    {
        var port = new FakeWincSerialPort();
        port.Blocks[0xD0000] = [1, 2, 3, 4, 5, 6, 7, 8];

        var data = CreateClient(port).ReadBlock(0xD0000, 8);

        Assert.Equal<byte>([1, 2, 3, 4, 5, 6, 7, 8], data);
    }

    [Theory]
    [InlineData(2048)]
    [InlineData(4096)]
    [InlineData(65536)]
    public void ReadBlock_RejectsSizesThatWouldWedgeTheDevice(int size)
    {
        // At or above the firmware's 2048-byte buffer its read loop never terminates. Failing fast
        // here is what keeps a bulk read from hanging forever against real hardware.
        var port = new FakeWincSerialPort();

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateClient(port).ReadBlock(0xD0000, size));

        Assert.Equal("size", ex.ParamName);
        Assert.Empty(port.ReceivedHeaders);
    }

    [Fact]
    public void ReadBlock_AcceptsTheMaximumSafeSize()
    {
        var port = new FakeWincSerialPort();
        port.Blocks[0xD0000] = new byte[WincBridgeProtocol.MaxReadBlockSize];

        var data = CreateClient(port).ReadBlock(0xD0000, WincBridgeProtocol.MaxReadBlockSize);

        Assert.Equal(WincBridgeProtocol.MaxReadBlockSize, data.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ReadBlock_RejectsNonPositiveSizes(int size)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateClient(new FakeWincSerialPort()).ReadBlock(0xD0000, size));
    }

    [Fact]
    public void WriteBlock_SendsThePayloadAfterTheHeaderAck()
    {
        var port = new FakeWincSerialPort();
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF];

        CreateClient(port).WriteBlock(0xD0000, payload);

        Assert.Equal(payload, Assert.Single(port.ReceivedPayloads));
        var header = Assert.Single(port.ReceivedHeaders);
        Assert.Equal((byte)WincBridgeProtocol.Command.WriteBlock, header[0]);
        Assert.Equal(4, (header[3] << 8) | header[2]);
    }

    [Fact]
    public void WriteBlock_ThrowsWhenTheDeviceNacksThePayload()
    {
        var port = new FakeWincSerialPort { FailNextBlockWrite = true };

        var ex = Assert.Throws<IOException>(
            () => CreateClient(port).WriteBlock(0xD0000, [1, 2, 3, 4]));

        Assert.Contains("rejected", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WriteBlock_RejectsAnEmptyPayload()
    {
        Assert.Throws<ArgumentException>(
            () => CreateClient(new FakeWincSerialPort()).WriteBlock(0xD0000, []));
    }

    [Fact]
    public void WriteBlock_RejectsAnOversizePayload()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateClient(new FakeWincSerialPort())
                .WriteBlock(0xD0000, new byte[WincBridgeProtocol.MaxWriteBlockSize + 1]));
    }

    [Fact]
    public void ChangeBaudRate_TellsTheDeviceThenMovesTheHost()
    {
        // Order is load-bearing: the device switches as soon as it processes the command, so the
        // header must already be on the wire before the host rate changes.
        var port = new FakeWincSerialPort();

        CreateClient(port).ChangeBaudRate(500000, TimeSpan.Zero);

        var header = Assert.Single(port.ReceivedHeaders);
        Assert.Equal((byte)WincBridgeProtocol.Command.Reconfigure, header[0]);
        Assert.Equal(500000, Assert.Single(port.BaudRateHistory));
        Assert.Equal(500000, port.BaudRate);
    }

    [Fact]
    public void ChangeBaudRate_DiscardsBytesStragglingInAtTheOldRate()
    {
        var port = new FakeWincSerialPort();

        CreateClient(port).ChangeBaudRate(500000, TimeSpan.Zero);

        Assert.True(port.DiscardCount >= 1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-9600)]
    public void ChangeBaudRate_RejectsNonPositiveRates(int baud)
    {
        var port = new FakeWincSerialPort();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => CreateClient(port).ChangeBaudRate(baud, TimeSpan.Zero));
        Assert.Empty(port.ReceivedHeaders);
    }

    [Fact]
    public void Commands_AreAlwaysPrefixedWithTheStartByte()
    {
        // Without the 0xA5 prefix the device stays in its op-code state and the header is read as a
        // stream of unknown op codes, which fails silently rather than loudly.
        var port = new RecordingPort();
        var client = new WincSerialBridgeClient(port, TimeSpan.FromSeconds(1));

        try
        {
            client.WriteRegister(0x1000, 1);
        }
        catch (TimeoutException)
        {
            // The recording port never answers; we only care about what went out.
        }

        Assert.Equal(WincBridgeProtocol.StartCommand, port.Written[0]);
        Assert.Equal(1 + WincBridgeProtocol.HeaderSize, port.Written.Count);
    }

    [Fact]
    public void Commands_ThrowWhenTheDeviceNacksTheHeader()
    {
        var port = new NackingPort();
        var client = new WincSerialBridgeClient(port, TimeSpan.FromSeconds(1));

        var ex = Assert.Throws<IOException>(() => client.ReadRegister(0x1000));

        Assert.Contains("checksum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Commands_ThrowWhenTheDeviceReturnsAnUnexpectedByte()
    {
        // Out-of-sync with the bridge state machine is a distinct failure from a rejected checksum,
        // and the message should say so.
        var port = new NackingPort { Verdict = 0x77 };
        var client = new WincSerialBridgeClient(port, TimeSpan.FromSeconds(1));

        var ex = Assert.Throws<IOException>(() => client.ReadRegister(0x1000));

        Assert.Contains("unexpected", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0x77", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Constructor_RejectsANullPort()
    {
        Assert.Throws<ArgumentNullException>(() => new WincSerialBridgeClient(null!));
    }

    /// <summary>Captures outbound bytes and never answers.</summary>
    private sealed class RecordingPort : IWincSerialPort
    {
        internal List<byte> Written { get; } = [];

        public bool IsOpen => true;
        public int BaudRate { get; set; } = 115200;
        public void Open() { }
        public void Close() { }
        public void DiscardInBuffer() { }

        public void Write(byte[] buffer, int offset, int count)
        {
            for (var i = 0; i < count; i++)
            {
                Written.Add(buffer[offset + i]);
            }
        }

        public void ReadExactly(byte[] buffer, int offset, int count, TimeSpan timeout)
            => throw new TimeoutException("Recording port never answers.");

        public void Dispose() { }
    }

    /// <summary>Answers every header with a fixed verdict byte.</summary>
    private sealed class NackingPort : IWincSerialPort
    {
        internal byte Verdict { get; set; } = WincBridgeProtocol.Response.Nack;

        public bool IsOpen => true;
        public int BaudRate { get; set; } = 115200;
        public void Open() { }
        public void Close() { }
        public void DiscardInBuffer() { }
        public void Write(byte[] buffer, int offset, int count) { }

        public void ReadExactly(byte[] buffer, int offset, int count, TimeSpan timeout)
        {
            for (var i = 0; i < count; i++)
            {
                buffer[offset + i] = Verdict;
            }
        }

        public void Dispose() { }
    }
}
