using System;
using System.Globalization;
using System.Net;
using System.Text;
using Daqifi.Core.Communication;
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
        Assert.Equal("SYSTem:STORage:SD:FILE \"log.bin\"", message.Data);
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
        var message = ScpiMessageProducer.EnableAdcChannels("84");
        Assert.Equal("ENAble:VOLTage:DC 84", message.Data);
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
    public void SetLanAddress_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.SetLanAddress(IPAddress.Parse("192.168.1.42"));
        Assert.Equal("SYSTem:COMMunicate:LAN:ADDRess \"192.168.1.42\"", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SetLanAddress_NullAddress_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ScpiMessageProducer.SetLanAddress(null!));
    }

    [Fact]
    public void SetLanAddress_IPv6_Throws()
    {
        Assert.Throws<ArgumentException>(() => ScpiMessageProducer.SetLanAddress(IPAddress.IPv6Loopback));
    }

    [Fact]
    public void SetLanMask_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.SetLanMask(IPAddress.Parse("255.255.255.0"));
        Assert.Equal("SYSTem:COMMunicate:LAN:MASK \"255.255.255.0\"", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SetLanMask_NullMask_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ScpiMessageProducer.SetLanMask(null!));
    }

    [Fact]
    public void SetLanMask_IPv6_Throws()
    {
        Assert.Throws<ArgumentException>(() => ScpiMessageProducer.SetLanMask(IPAddress.IPv6Loopback));
    }

    [Fact]
    public void SetLanGateway_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.SetLanGateway(IPAddress.Parse("192.168.1.1"));
        Assert.Equal("SYSTem:COMMunicate:LAN:GATEway \"192.168.1.1\"", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SetLanGateway_NullGateway_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ScpiMessageProducer.SetLanGateway(null!));
    }

    [Fact]
    public void SetLanGateway_IPv6_Throws()
    {
        Assert.Throws<ArgumentException>(() => ScpiMessageProducer.SetLanGateway(IPAddress.IPv6Loopback));
    }

    [Fact]
    public void GetLanAddress_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.GetLanAddress;
        Assert.Equal("SYSTem:COMMunicate:LAN:ADDRess?", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void GetLanMask_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.GetLanMask;
        Assert.Equal("SYSTem:COMMunicate:LAN:MASK?", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void GetLanGateway_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.GetLanGateway;
        Assert.Equal("SYSTem:COMMunicate:LAN:GATEway?", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void GetLanConfiguredAddress_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.GetLanConfiguredAddress;
        Assert.Equal("SYSTem:COMMunicate:LAN:CONFigure:ADDRess?", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void GetLanConfiguredMask_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.GetLanConfiguredMask;
        Assert.Equal("SYSTem:COMMunicate:LAN:CONFigure:MASK?", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void GetLanConfiguredGateway_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.GetLanConfiguredGateway;
        Assert.Equal("SYSTem:COMMunicate:LAN:CONFigure:GATEway?", message.Data);
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

    [Fact]
    public void DeleteSdFile_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.DeleteSdFile("data.bin");
        Assert.Equal("SYSTem:STORage:SD:DELete \"data.bin\"", message.Data);
        AssertMessageFormat(message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DeleteSdFile_WithNullOrEmptyFileName_Throws(string? fileName)
    {
        Assert.Throws<ArgumentException>(
            () => ScpiMessageProducer.DeleteSdFile(fileName!));
    }

    [Fact]
    public void FormatSdCard_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.FormatSdCard;
        Assert.Equal("SYSTem:STORage:SD:FORmat", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SetSdMaxFileSize_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.SetSdMaxFileSize(1073741824);
        Assert.Equal("SYSTem:STORage:SD:MAXSize 1073741824", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SetSdMaxFileSize_WithZero_ReturnsDefaultCommand()
    {
        var message = ScpiMessageProducer.SetSdMaxFileSize(0);
        Assert.Equal("SYSTem:STORage:SD:MAXSize 0", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SetSdMaxFileSize_WithNegativeValue_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ScpiMessageProducer.SetSdMaxFileSize(-1));
    }

    [Fact]
    public void GetSdMaxFileSize_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.GetSdMaxFileSize;
        Assert.Equal("SYSTem:STORage:SD:MAXSize?", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void RunSdBenchmark_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.RunSdBenchmark(1048576);
        Assert.Equal("SYSTem:STORage:SD:BENCHmark 1048576", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void RunSdBenchmark_WithZeroOrNegative_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ScpiMessageProducer.RunSdBenchmark(0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ScpiMessageProducer.RunSdBenchmark(-1));
    }

    [Fact]
    public void GetSdBenchmarkResults_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.GetSdBenchmarkResults;
        Assert.Equal("SYSTem:STORage:SD:BENCHmark?", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void GetSdSpace_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.GetSdSpace;
        Assert.Equal("SYSTem:STORage:SD:SPACe?", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SetSdMinFreeSpace_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.SetSdMinFreeSpace(52428800);
        Assert.Equal("SYSTem:STORage:SD:MINFree 52428800", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SetSdMinFreeSpace_WithZero_DisablesGate()
    {
        var message = ScpiMessageProducer.SetSdMinFreeSpace(0);
        Assert.Equal("SYSTem:STORage:SD:MINFree 0", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SetSdMinFreeSpace_WithNegativeValue_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ScpiMessageProducer.SetSdMinFreeSpace(-1));
    }

    [Theory]
    [InlineData(StreamInterface.Usb, 0)]
    [InlineData(StreamInterface.WiFi, 1)]
    [InlineData(StreamInterface.SdCard, 2)]
    [InlineData(StreamInterface.All, 3)]
    public void SetStreamInterface_ReturnsCorrectCommand(StreamInterface iface, int expectedValue)
    {
        var message = ScpiMessageProducer.SetStreamInterface(iface);
        Assert.Equal($"SYSTem:STReam:INTerface {expectedValue}", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SetStreamInterface_WithUndefinedValue_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ScpiMessageProducer.SetStreamInterface((StreamInterface)99));
    }

    [Fact]
    public void GetStreamInterface_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.GetStreamInterface;
        Assert.Equal("SYSTem:STReam:INTerface?", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void ForceBootloader_ReturnsCorrectMessage()
    {
        var message = ScpiMessageProducer.ForceBootloader;
        Assert.Equal("SYSTem:FORceBoot", message.Data);
    }

    [Fact]
    public void SetAnalogOutputVoltage_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.SetAnalogOutputVoltage(0, 5.0);
        Assert.Equal("SOURce:VOLTage:LEVel 0,5", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SetAnalogOutputVoltage_FormatsFractionalVoltageWithInvariantDecimalPoint()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            // A culture whose decimal separator is a comma must not corrupt the SCPI argument.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var message = ScpiMessageProducer.SetAnalogOutputVoltage(2, 2.5);
            Assert.Equal("SOURce:VOLTage:LEVel 2,2.5", message.Data);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void SetAnalogOutputVoltage_WithNegativeVoltage_FormatsLeadingMinus()
    {
        // Negative channel is rejected, but negative voltage is valid and must render correctly.
        var message = ScpiMessageProducer.SetAnalogOutputVoltage(0, -3.3);
        Assert.Equal("SOURce:VOLTage:LEVel 0,-3.3", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SetAnalogOutputVoltage_WithNegativeChannel_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScpiMessageProducer.SetAnalogOutputVoltage(-1, 1.0));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void SetAnalogOutputVoltage_WithNonFiniteVoltage_Throws(double voltage)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScpiMessageProducer.SetAnalogOutputVoltage(0, voltage));
    }

    [Fact]
    public void UpdateDacOutputs_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.UpdateDacOutputs;
        Assert.Equal("CONFigure:DAC:UPDATE", message.Data);
        AssertMessageFormat(message);
    }

    private static void AssertMessageFormat(IOutboundMessage<string> message)
    {
        var bytes = message.GetBytes();
        var expectedBytes = Encoding.ASCII.GetBytes($"{message.Data}\r\n");
        
        Assert.Equal(expectedBytes, bytes);
    }
} 