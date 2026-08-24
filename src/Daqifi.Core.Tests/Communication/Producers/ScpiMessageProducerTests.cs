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
    public void GetCapabilitiesApiVersion_ReturnsCorrectCommand()
    {
        // Act
        var message = ScpiMessageProducer.GetCapabilitiesApiVersion;

        // Assert
        Assert.Equal("CONFigure:CAPabilities:APIVersion?", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void GetCapabilitiesJson_ReturnsCorrectCommand()
    {
        // Act
        var message = ScpiMessageProducer.GetCapabilitiesJson;

        // Assert
        Assert.Equal("CONFigure:CAPabilities:JSON?", message.Data);
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
    public void SetDioPortDirection_WithNegativeChannel_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScpiMessageProducer.SetDioPortDirection(-1, 1));
    }

    // Channel 0 is a real channel on every DAQiFi board, so the guard's boundary matters:
    // rejecting only negatives must leave the lowest valid channel untouched.
    [Fact]
    public void SetDioPortDirection_WithChannelZero_StillReturnsCommand()
    {
        var message = ScpiMessageProducer.SetDioPortDirection(0, 0);
        Assert.Equal("DIO:PORt:DIRection 0,0", message.Data);
        AssertMessageFormat(message);
    }

    // `direction` is documented as a two-value enumeration (0 = input, 1 = output) and the
    // firmware understands nothing else, so anything outside that is caller error rather
    // than a frame to put on the wire.
    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(int.MaxValue)]
    public void SetDioPortDirection_WithOutOfRangeDirection_Throws(int direction)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => ScpiMessageProducer.SetDioPortDirection(0, direction));

        Assert.Equal("direction", ex.ParamName);
        Assert.Equal(direction, ex.ActualValue);
        Assert.StartsWith("Direction must be 0 (input) or 1 (output).", ex.Message, StringComparison.Ordinal);
    }

    // Both documented values must survive the guard unchanged, byte for byte.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void SetDioPortDirection_WithDocumentedDirection_StillReturnsCommand(int direction)
    {
        var message = ScpiMessageProducer.SetDioPortDirection(3, direction);
        Assert.Equal($"DIO:PORt:DIRection 3,{direction}", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SetDioPortDirection_WithNegativeChannel_ThrowsBeforeCheckingTheDirection()
    {
        // Both arguments are invalid here. The channel guard runs first, so the caller is
        // told about `channel` rather than about `direction`, matching SetDioPortState and
        // the PWM setters, which all validate the channel before the value.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => ScpiMessageProducer.SetDioPortDirection(-1, 7));

        Assert.Equal("channel", ex.ParamName);
    }

    [Fact]
    public void SetDioPortState_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.SetDioPortState(1, 1);
        Assert.Equal("DIO:PORt:STATe 1,1", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SetDioPortState_WithNegativeChannel_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScpiMessageProducer.SetDioPortState(-1, 1));
    }

    [Fact]
    public void SetDioPortState_WithChannelZero_StillReturnsCommand()
    {
        var message = ScpiMessageProducer.SetDioPortState(0, 1);
        Assert.Equal("DIO:PORt:STATe 0,1", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SetDioPortState_WithNegativeChannel_ThrowsBeforeCheckingTheValue()
    {
        // Both arguments are invalid here. The channel guard runs first, so the caller is
        // told about `channel` rather than about `value`, matching the sibling setters that
        // validate the channel before the value.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => ScpiMessageProducer.SetDioPortState(-1, double.NaN));

        Assert.Equal("channel", ex.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void SetDioPortState_FormatsDocumentedStatesIdenticallyUnderCommaDecimalCulture(double value)
    {
        // The documented contract is 0 = low, 1 = high. Those render the same under every
        // culture, so changing how `value` is formatted must leave them byte-for-byte alone.
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var message = ScpiMessageProducer.SetDioPortState(1, value);
            Assert.Equal($"DIO:PORt:STATe 1,{(int)value}", message.Data);
            AssertMessageFormat(message);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void SetDioPortState_FormatsFractionalValueWithInvariantDecimalPoint()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            // A culture whose decimal separator is a comma must not corrupt the SCPI argument:
            // a comma here would read as a third argument rather than a fractional state.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var message = ScpiMessageProducer.SetDioPortState(1, 1.5);
            Assert.Equal("DIO:PORt:STATe 1,1.5", message.Data);
            AssertMessageFormat(message);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void SetDioPortState_WithNonFiniteValue_Throws(double value)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => ScpiMessageProducer.SetDioPortState(1, value));

        // The finite check is shared with the other double-valued setters, so pin the
        // parameter name and wording this call site is responsible for supplying.
        Assert.Equal("value", ex.ParamName);
        Assert.Contains("State value must be a finite number.", ex.Message);
    }

    [Fact]
    public void SetDioPortState_WithNonFiniteValue_ThrowsBeforeBuildingAMessage()
    {
        // The guard must reject the value rather than let a culture-dependent
        // infinity symbol reach the command text.
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Throws<ArgumentOutOfRangeException>(() => ScpiMessageProducer.SetDioPortState(1, double.PositiveInfinity));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
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

    [Theory]
    [InlineData(true, "PWM:CHannel:ENable 4,1")]
    [InlineData(false, "PWM:CHannel:ENable 4,0")]
    public void SetPwmChannelEnabled_ReturnsCorrectCommand(bool enabled, string expected)
    {
        var message = ScpiMessageProducer.SetPwmChannelEnabled(4, enabled);
        Assert.Equal(expected, message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SetPwmChannelFrequency_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.SetPwmChannelFrequency(0, 1000);
        Assert.Equal("PWM:CHannel:FREQuency 0,1000", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SetPwmChannelDutyCycle_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.SetPwmChannelDutyCycle(4, 50);
        Assert.Equal("PWM:CHannel:DUTY 4,50", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void PwmProducers_RejectInvalidArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScpiMessageProducer.SetPwmChannelEnabled(-1, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScpiMessageProducer.SetPwmChannelFrequency(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScpiMessageProducer.SetPwmChannelFrequency(-1, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScpiMessageProducer.SetPwmChannelDutyCycle(4, 101));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScpiMessageProducer.SetPwmChannelDutyCycle(-1, 50));
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
    public void LoadNetworkLan_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.LoadNetworkLan;
        Assert.Equal("SYSTem:COMMunicate:LAN:LOAD", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void FactoryResetNetworkLan_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.FactoryResetNetworkLan;
        Assert.Equal("SYSTem:COMMunicate:LAN:FACRESET", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SaveAdcCalibration_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.SaveAdcCalibration;
        Assert.Equal("CONFigure:ADC:SAVEcal", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void LoadAdcCalibration_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.LoadAdcCalibration;
        Assert.Equal("CONFigure:ADC:LOADcal", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SaveVoltagePrecision_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.SaveVoltagePrecision;
        Assert.Equal("CONFigure:VOLTage:SAVE", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void LoadVoltagePrecision_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.LoadVoltagePrecision;
        Assert.Equal("CONFigure:VOLTage:LOAD", message.Data);
        AssertMessageFormat(message);
    }

    // ---------------------------------------------------------------------
    // Per-channel ADC calibration-constant write path (daqifi-core#386)
    // ---------------------------------------------------------------------

    [Fact]
    public void SetAdcCalibrationSlope_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.SetAdcCalibrationSlope(2, 1.0025);
        Assert.Equal("CONFigure:ADC:chanCALM 2,1.0025", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SetAdcCalibrationSlope_FormatsFractionalValueWithInvariantDecimalPoint()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            // A culture whose decimal separator is a comma must not corrupt the SCPI argument.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var message = ScpiMessageProducer.SetAdcCalibrationSlope(0, 2.5);
            Assert.Equal("CONFigure:ADC:chanCALM 0,2.5", message.Data);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void SetAdcCalibrationSlope_WithNegativeValue_FormatsLeadingMinus()
    {
        var message = ScpiMessageProducer.SetAdcCalibrationSlope(1, -0.5);
        Assert.Equal("CONFigure:ADC:chanCALM 1,-0.5", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SetAdcCalibrationSlope_WithNegativeChannel_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScpiMessageProducer.SetAdcCalibrationSlope(-1, 1.0));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void SetAdcCalibrationSlope_WithNonFiniteValue_Throws(double calM)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => ScpiMessageProducer.SetAdcCalibrationSlope(0, calM));

        // The finite check is shared with the other double-valued setters, so pin the
        // parameter name and wording this call site is responsible for supplying.
        Assert.Equal("calM", ex.ParamName);
        Assert.Contains("Calibration slope must be a finite number.", ex.Message);
    }

    [Fact]
    public void SetAdcCalibrationOffset_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.SetAdcCalibrationOffset(3, -0.0031);
        Assert.Equal("CONFigure:ADC:chanCALB 3,-0.0031", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SetAdcCalibrationOffset_FormatsFractionalValueWithInvariantDecimalPoint()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var message = ScpiMessageProducer.SetAdcCalibrationOffset(0, 1.5);
            Assert.Equal("CONFigure:ADC:chanCALB 0,1.5", message.Data);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void SetAdcCalibrationOffset_WithNegativeChannel_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScpiMessageProducer.SetAdcCalibrationOffset(-1, 1.0));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void SetAdcCalibrationOffset_WithNonFiniteValue_Throws(double calB)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => ScpiMessageProducer.SetAdcCalibrationOffset(0, calB));

        // The finite check is shared with the other double-valued setters, so pin the
        // parameter name and wording this call site is responsible for supplying.
        Assert.Equal("calB", ex.ParamName);
        Assert.Contains("Calibration offset must be a finite number.", ex.Message);
    }

    [Fact]
    public void GetAdcCalibrationSlope_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.GetAdcCalibrationSlope(0);
        Assert.Equal("CONFigure:ADC:chanCALM? 0", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void GetAdcCalibrationSlope_WithNegativeChannel_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScpiMessageProducer.GetAdcCalibrationSlope(-1));
    }

    [Fact]
    public void GetAdcCalibrationOffset_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.GetAdcCalibrationOffset(5);
        Assert.Equal("CONFigure:ADC:chanCALB? 5", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void GetAdcCalibrationOffset_WithNegativeChannel_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScpiMessageProducer.GetAdcCalibrationOffset(-1));
    }

    [Fact]
    public void SaveFactoryAdcCalibration_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.SaveFactoryAdcCalibration;
        Assert.Equal("CONFigure:ADC:SAVEFcal", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void LoadFactoryAdcCalibration_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.LoadFactoryAdcCalibration;
        Assert.Equal("CONFigure:ADC:LOADFcal", message.Data);
        AssertMessageFormat(message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void UseAdcCalibration_ReturnsCorrectCommand(int bank)
    {
        var message = ScpiMessageProducer.UseAdcCalibration(bank);
        Assert.Equal($"CONFigure:ADC:USECal {bank}", message.Data);
        AssertMessageFormat(message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void UseAdcCalibration_WithInvalidBank_Throws(int bank)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScpiMessageProducer.UseAdcCalibration(bank));
    }

    [Fact]
    public void GetAdcCalibrationBank_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.GetAdcCalibrationBank;
        Assert.Equal("CONFigure:ADC:USECal?", message.Data);
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
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => ScpiMessageProducer.SetAnalogOutputVoltage(0, voltage));

        // The finite check is shared with the other double-valued setters, so pin the
        // parameter name and wording this call site is responsible for supplying.
        Assert.Equal("voltage", ex.ParamName);
        Assert.Contains("Voltage must be a finite number.", ex.Message);
    }

    [Fact]
    public void UpdateDacOutputs_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.UpdateDacOutputs;
        Assert.Equal("CONFigure:DAC:UPDATE", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void GetAnalogOutputVoltage_ReturnsCorrectQuery()
    {
        var message = ScpiMessageProducer.GetAnalogOutputVoltage(2);
        Assert.Equal("SOURce:VOLTage:LEVel? 2", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void GetAnalogOutputVoltage_WithNegativeChannel_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScpiMessageProducer.GetAnalogOutputVoltage(-1));
    }

    // --- Logging & diagnostics ---

    [Fact]
    public void GetSystemLog_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.GetSystemLog;
        Assert.Equal("SYSTem:LOG?", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void ClearSystemLog_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.ClearSystemLog;
        Assert.Equal("SYSTem:LOG:CLEar", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void GetCommandHistory_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.GetCommandHistory;
        Assert.Equal("SYSTem:LOG:CMDHistory?", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void TestSystemLog_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.TestSystemLog;
        Assert.Equal("SYSTem:LOG:TEST", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void GetSystemErrorCount_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.GetSystemErrorCount;
        Assert.Equal("SYSTem:ERRor:COUNt?", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void GetStreamStats_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.GetStreamStats;
        Assert.Equal("SYSTem:STReam:STATS?", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void GetMemoryDiagnostics_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.GetMemoryDiagnostics;
        Assert.Equal("SYSTem:MEMory:FREE?", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SetLogLevel_FormatsModuleAndLevel()
    {
        var message = ScpiMessageProducer.SetLogLevel("STREAM", 2);
        Assert.Equal("SYSTem:LOG:LEVel STREAM,2", message.Data);
        AssertMessageFormat(message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SetLogLevel_WithEmptyModule_Throws(string? module)
    {
        Assert.Throws<ArgumentException>(() => ScpiMessageProducer.SetLogLevel(module!, 1));
    }

    [Theory]
    [InlineData("STREAM,extra")]
    [InlineData("a b")]
    [InlineData("a;b")]
    [InlineData("a\"b")]
    [InlineData("a\nb")]
    public void SetLogLevel_WithInjectionChars_Throws(string module)
    {
        Assert.Throws<ArgumentException>(() => ScpiMessageProducer.SetLogLevel(module, 1));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void SetLogLevel_WithLevelOutOfRange_Throws(int level)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScpiMessageProducer.SetLogLevel("STREAM", level));
    }

    [Fact]
    public void SetDeviceName_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.SetDeviceName("My Device");
        Assert.Equal("SYSTem:DEVice:NAME \"My Device\"", message.Data);
        AssertMessageFormat(message);
    }

    [Fact]
    public void SaveDeviceName_ReturnsCorrectCommand()
    {
        var message = ScpiMessageProducer.SaveDeviceName;
        Assert.Equal("SYSTem:DEVice:NAME:SAVE", message.Data);
        AssertMessageFormat(message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has a \"quote")]
    [InlineData("has a \\backslash")]
    [InlineData("has a \ncontrol char")]
    public void SetDeviceName_WithInvalidName_Throws(string? name)
    {
        Assert.Throws<ArgumentException>(() => ScpiMessageProducer.SetDeviceName(name));
    }

    [Fact]
    public void SetDeviceName_TooLong_Throws()
    {
        var name = new string('a', ScpiMessageProducer.MaxFriendlyNameLength + 1);
        Assert.Throws<ArgumentException>(() => ScpiMessageProducer.SetDeviceName(name));
    }

    [Theory]
    [InlineData("A")]
    [InlineData("My Device")]
    [InlineData("~!@#$%^&*()_+-=[]{}|;:,.<>/?")]
    public void IsFriendlyNameValid_WithValidName_ReturnsTrue(string name)
    {
        Assert.True(ScpiMessageProducer.IsFriendlyNameValid(name));
    }

    [Fact]
    public void IsFriendlyNameValid_AtMaxLength_ReturnsTrue()
    {
        var name = new string('a', ScpiMessageProducer.MaxFriendlyNameLength);
        Assert.True(ScpiMessageProducer.IsFriendlyNameValid(name));
    }

    [Fact]
    public void IsFriendlyNameValid_OverMaxLength_ReturnsFalse()
    {
        var name = new string('a', ScpiMessageProducer.MaxFriendlyNameLength + 1);
        Assert.False(ScpiMessageProducer.IsFriendlyNameValid(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has a \"quote")]
    [InlineData("has a \\backslash")]
    [InlineData("has a \ncontrol char")]
    public void IsFriendlyNameValid_WithInvalidName_ReturnsFalse(string? name)
    {
        Assert.False(ScpiMessageProducer.IsFriendlyNameValid(name));
    }

    // Each channel-addressed producer already has its own test proving it throws on a
    // negative channel. None of them pinned what callers can actually observe about that
    // throw, so nothing stopped the copies of the rule from drifting apart. They now
    // share one ValidateChannel helper; this pins the contract it is there to hold.
    //
    // The list is the whole of ScpiMessageProducer's channel-addressed surface: the two
    // DIO producers were the odd ones out until they were brought in, so a producer added
    // here without the guard fails this test rather than quietly shipping an unguarded one.
    [Fact]
    public void EveryChannelAddressedProducer_RejectsNegativeChannelIdentically()
    {
        var producers = new (string Name, Action Act)[]
        {
            (nameof(ScpiMessageProducer.SetPwmChannelEnabled), () => ScpiMessageProducer.SetPwmChannelEnabled(-1, true)),
            (nameof(ScpiMessageProducer.SetPwmChannelFrequency), () => ScpiMessageProducer.SetPwmChannelFrequency(-1, 100)),
            (nameof(ScpiMessageProducer.SetPwmChannelDutyCycle), () => ScpiMessageProducer.SetPwmChannelDutyCycle(-1, 50)),
            (nameof(ScpiMessageProducer.SetAnalogOutputVoltage), () => ScpiMessageProducer.SetAnalogOutputVoltage(-1, 1.0)),
            (nameof(ScpiMessageProducer.GetAnalogOutputVoltage), () => ScpiMessageProducer.GetAnalogOutputVoltage(-1)),
            (nameof(ScpiMessageProducer.SetAdcCalibrationSlope), () => ScpiMessageProducer.SetAdcCalibrationSlope(-1, 1.0)),
            (nameof(ScpiMessageProducer.SetAdcCalibrationOffset), () => ScpiMessageProducer.SetAdcCalibrationOffset(-1, 1.0)),
            (nameof(ScpiMessageProducer.GetAdcCalibrationSlope), () => ScpiMessageProducer.GetAdcCalibrationSlope(-1)),
            (nameof(ScpiMessageProducer.GetAdcCalibrationOffset), () => ScpiMessageProducer.GetAdcCalibrationOffset(-1)),
            (nameof(ScpiMessageProducer.SetDioPortDirection), () => ScpiMessageProducer.SetDioPortDirection(-1, 1)),
            (nameof(ScpiMessageProducer.SetDioPortState), () => ScpiMessageProducer.SetDioPortState(-1, 1)),
        };

        var observed = new List<(string Producer, string? ParamName, object? ActualValue, string Message)>();
        foreach (var (name, act) in producers)
        {
            var ex = Assert.Throws<ArgumentOutOfRangeException>(act);
            observed.Add((name, ex.ParamName, ex.ActualValue, ex.Message));
        }

        Assert.Equal(11, observed.Count);
        Assert.All(observed, o =>
        {
            Assert.Equal("channel", o.ParamName);
            Assert.Equal(-1, o.ActualValue);
            Assert.StartsWith("Channel number cannot be negative.", o.Message, StringComparison.Ordinal);
        });
    }

    // The companion to EveryChannelAddressedProducer_RejectsNegativeChannelIdentically, for
    // the other axis: the producers whose *second* argument has a documented range. Each
    // guard is written out at its own call site (the ranges differ, so there is no shared
    // helper to hold the rule), which is exactly why the observable shape needs pinning in
    // one place — SetDioPortDirection was the one that had drifted, emitting a frame for
    // any int at all. Anything the caller can see about the throw is compared across all
    // three rather than against a literal copied out of the source.
    [Fact]
    public void EveryValueRangedProducer_RejectsOutOfRangeArgumentIdentically()
    {
        var producers = new (string Name, string ParamName, object ActualValue, string MessagePrefix, Action Act)[]
        {
            (nameof(ScpiMessageProducer.SetPwmChannelFrequency), "frequencyHz", 0, "Frequency must be positive.",
                () => ScpiMessageProducer.SetPwmChannelFrequency(0, 0)),
            (nameof(ScpiMessageProducer.SetPwmChannelDutyCycle), "dutyCyclePercent", 101, "Duty cycle must be 0-100 percent.",
                () => ScpiMessageProducer.SetPwmChannelDutyCycle(0, 101)),
            (nameof(ScpiMessageProducer.SetDioPortDirection), "direction", 2, "Direction must be 0 (input) or 1 (output).",
                () => ScpiMessageProducer.SetDioPortDirection(0, 2)),
        };

        Assert.Equal(3, producers.Length);
        foreach (var (name, paramName, actualValue, messagePrefix, act) in producers)
        {
            var ex = Assert.Throws<ArgumentOutOfRangeException>(act);

            // Compared as one tuple so a failure names the producer that drifted.
            Assert.Equal((name, paramName, (object?)actualValue), (name, ex.ParamName, ex.ActualValue));
            Assert.StartsWith(messagePrefix, ex.Message, StringComparison.Ordinal);
        }
    }

    private static void AssertMessageFormat(IOutboundMessage<string> message)
    {
        var bytes = message.GetBytes();
        var expectedBytes = Encoding.ASCII.GetBytes($"{message.Data}\r\n");
        
        Assert.Equal(expectedBytes, bytes);
    }
} 