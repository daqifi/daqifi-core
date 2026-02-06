using Daqifi.Core.Firmware;

namespace Daqifi.Core.Tests.Firmware;

public class Pic32BootloaderProtocolTests
{
    private readonly Pic32BootloaderProtocol _protocol = new();

    [Fact]
    public void ImplementsIBootloaderProtocol()
    {
        Assert.IsAssignableFrom<IBootloaderProtocol>(_protocol);
    }

    [Fact]
    public void CreateRequestVersionMessage_DelegatesToProducer()
    {
        var expected = Pic32BootloaderMessageProducer.CreateRequestVersionMessage();
        var result = _protocol.CreateRequestVersionMessage();

        Assert.Equal(expected, result);
    }

    [Fact]
    public void CreateEraseFlashMessage_DelegatesToProducer()
    {
        var expected = Pic32BootloaderMessageProducer.CreateEraseFlashMessage();
        var result = _protocol.CreateEraseFlashMessage();

        Assert.Equal(expected, result);
    }

    [Fact]
    public void CreateProgramFlashMessage_DelegatesToProducer()
    {
        var hexRecord = new byte[] { 0xAA, 0xBB };
        var expected = Pic32BootloaderMessageProducer.CreateProgramFlashMessage(hexRecord);
        var result = _protocol.CreateProgramFlashMessage(hexRecord);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void CreateJumpToApplicationMessage_DelegatesToProducer()
    {
        var expected = Pic32BootloaderMessageProducer.CreateJumpToApplicationMessage();
        var result = _protocol.CreateJumpToApplicationMessage();

        Assert.Equal(expected, result);
    }

    [Fact]
    public void DecodeVersionResponse_DelegatesToConsumer()
    {
        var data = new byte[] { 0x01, 0x10, 0x01, 0x02, 0x05 };
        var expected = Pic32BootloaderMessageConsumer.DecodeVersionResponse(data);
        var result = _protocol.DecodeVersionResponse(data);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void DecodeProgramFlashResponse_DelegatesToConsumer()
    {
        var data = new byte[] { 0x01, 0x03 };
        var expected = Pic32BootloaderMessageConsumer.DecodeProgramFlashResponse(data);
        var result = _protocol.DecodeProgramFlashResponse(data);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void DecodeEraseFlashResponse_DelegatesToConsumer()
    {
        var data = new byte[] { 0x01, 0x02 };
        var expected = Pic32BootloaderMessageConsumer.DecodeEraseFlashResponse(data);
        var result = _protocol.DecodeEraseFlashResponse(data);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ParseHexFile_DelegatesToParser()
    {
        var lines = new[] { ":020000041D00DD", ":00000001FF" };
        var result = _protocol.ParseHexFile(lines);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Constructor_WithCustomProtectedRange_UsesCustomRange()
    {
        var protocol = new Pic32BootloaderProtocol(0x00010000, 0x00020000);

        var lines = new[]
        {
            ":020000040001F9",                                   // Extended address 0x0001
            ":10000000AABBCCDDEEFF00112233445566778899F8",       // Data at 0x00010000 - protected
            ":00000001FF"                                        // EOF
        };

        var result = protocol.ParseHexFile(lines);

        // Data record should be filtered by custom range
        Assert.Equal(2, result.Count);
    }
}
