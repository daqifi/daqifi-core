using System.Text;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;

namespace Daqifi.Core.Tests.Communication.Producers;

public class ScpiMessageProducerTests
{
    [Fact]
    public void Reboot_ReturnsCorrectCommand()
    {
        // Act
        var message = ScpiMessageProducer.Reboot;

        // Assert
        Assert.Equal("SYSTem:REboot", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SystemInfo_ReturnsCorrectCommand()
    {
        // Act
        var message = ScpiMessageProducer.SystemInfo;

        // Assert
        Assert.Equal("SYSTem:SYSInfoPB?", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void TurnOffEcho_ReturnsCorrectCommand()
    {
        // Act
        var message = ScpiMessageProducer.TurnOffEcho;

        // Assert
        Assert.Equal("SYSTem:ECHO -1", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void TurnOnEcho_ReturnsCorrectCommand()
    {
        // Act
        var message = ScpiMessageProducer.TurnOnEcho;

        // Assert
        Assert.Equal("SYSTem:ECHO 1", message.Data);
        AssertMessageFormat(message);
    }

    private void AssertMessageFormat(IMessage message)
    {
        var bytes = message.GetBytes();
        var expectedBytes = Encoding.ASCII.GetBytes($"{message.Data}\r\n");
        
        Assert.Equal(expectedBytes, bytes);
    }
} 