using System.Text;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;

namespace Daqifi.Core.Tests.Communication.Producers;

public class ScpiMessageProducerTests
{
    [Fact]
    public void RebootDevice_ReturnsCorrectCommand()
    {
        // Act
        var message = ScpiMessageProducer.RebootDevice;

        // Assert
        Assert.Equal("SYSTem:REboot", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void GetDeviceInfo_ReturnsCorrectCommand()
    {
        // Act
        var message = ScpiMessageProducer.GetDeviceInfo;

        // Assert
        Assert.Equal("SYSTem:SYSInfoPB?", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void DisableDeviceEcho_ReturnsCorrectCommand()
    {
        // Act
        var message = ScpiMessageProducer.DisableDeviceEcho;

        // Assert
        Assert.Equal("SYSTem:ECHO -1", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void EnableDeviceEcho_ReturnsCorrectCommand()
    {
        // Act
        var message = ScpiMessageProducer.EnableDeviceEcho;

        // Assert
        Assert.Equal("SYSTem:ECHO 1", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void TurnDeviceOn_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.TurnDeviceOn;
        Assert.Equal("SYSTem:POWer:STATe 1", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void EnableStorageSd_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.EnableStorageSd;
        Assert.Equal("SYSTem:STORage:SD:ENAble 1", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void DisableStorageSd_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.DisableStorageSd;
        Assert.Equal("SYSTem:STORage:SD:ENAble 0", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void GetSdLoggingState_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.GetSdLoggingState;
        Assert.Equal("SYSTem:STORage:SD:LOGging?", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void GetSdFileList_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.GetSdFileList;
        Assert.Equal("SYSTem:STORage:SD:LIST?", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void GetSdFile_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.GetSdFile("test.bin");
        Assert.Equal("SYSTem:STORage:SD:GET \"test.bin\"", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SetSdLoggingFileName_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.SetSdLoggingFileName("log.bin");
        Assert.Equal("SYSTem:STORage:SD:LOGging \"log.bin\"", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void StartStreaming_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.StartStreaming(100);
        Assert.Equal("SYSTem:StartStreamData 100", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void StopStreaming_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.StopStreaming;
        Assert.Equal("SYSTem:StopStreamData", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SetProtobufStreamFormat_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.SetProtobufStreamFormat;
        Assert.Equal("SYSTem:STReam:FORmat 0", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SetJsonStreamFormat_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.SetJsonStreamFormat;
        Assert.Equal("SYSTem:STReam:FORmat 1", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void GetStreamFormat_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.GetStreamFormat;
        Assert.Equal("SYSTem:STReam:FORmat?", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void EnableAdcChannels_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.EnableAdcChannels("0001010100");
        Assert.Equal("ENAble:VOLTage:DC 0001010100", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SetDioPortDirection_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.SetDioPortDirection(1, 1);
        Assert.Equal("DIO:PORt:DIRection 1,1", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SetDioPortState_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.SetDioPortState(1, 1);
        Assert.Equal("DIO:PORt:STATe 1,1", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void EnableDioPorts_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.EnableDioPorts();
        Assert.Equal("DIO:PORt:ENAble 1", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void DisableDioPorts_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.DisableDioPorts();
        Assert.Equal("DIO:PORt:ENAble 0", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SetNetworkWifiModeSelfHosted_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.SetNetworkWifiModeSelfHosted;
        Assert.Equal("SYSTem:COMMunicate:LAN:NETType 4", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SetNetworkWifiModeExisting_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.SetNetworkWifiModeExisting;
        Assert.Equal("SYSTem:COMMunicate:LAN:NETType 1", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SetNetworkWifiSsid_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.SetNetworkWifiSsid("MyNetwork");
        Assert.Equal("SYSTem:COMMunicate:LAN:SSID \"MyNetwork\"", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SetNetworkWifiSecurityOpen_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.SetNetworkWifiSecurityOpen;
        Assert.Equal("SYSTem:COMMunicate:LAN:SECurity 0", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SetNetworkWifiSecurityWpa_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.SetNetworkWifiSecurityWpa;
        Assert.Equal("SYSTem:COMMunicate:LAN:SECurity 3", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SetNetworkWifiPassword_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.SetNetworkWifiPassword("password123");
        Assert.Equal("SYSTem:COMMunicate:LAN:PASs \"password123\"", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void DisableNetworkLan_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.DisableNetworkLan;
        Assert.Equal("SYSTem:COMMunicate:LAN:ENAbled 0", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void EnableNetworkLan_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.EnableNetworkLan;
        Assert.Equal("SYSTem:COMMunicate:LAN:ENAbled 1", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void ApplyNetworkLan_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.ApplyNetworkLan;
        Assert.Equal("SYSTem:COMMunicate:LAN:APPLY", message.Data);
        AssertMessageFormat(message);
    }
    
    [Fact]
    public void SaveNetworkLan_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.SaveNetworkLan;
        Assert.Equal("SYSTem:COMMunicate:LAN:SAVE", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SetLanFirmwareUpdateMode_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.SetLanFirmwareUpdateMode;
        Assert.Equal("SYSTem:COMMUnicate:LAN:FWUpdate", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SetUsbTransparencyMode_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.SetUsbTransparencyMode(1);
        Assert.Equal("SYSTem:USB:SetTransparentMode 1", message.Data);
        AssertMessageFormat(message);
    }

    private static void AssertMessageFormat(IMessage message)
    {
        var bytes = message.GetBytes();
        var expectedBytes = Encoding.ASCII.GetBytes($"{message.Data}\r\n");
        
        Assert.Equal(expectedBytes, bytes);
    }
} 