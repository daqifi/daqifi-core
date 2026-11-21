using System;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Daqifi.Core.Communication;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;

namespace Daqifi.Core.Tests.Communication.Producers;

public class ScpiMessageProducerTests
{
    // ---------------------------------------------------------------------
    // Wire strings. Every row pins one producer call to the exact SCPI text it
    // must put on the wire (and, via AssertMessageFormat, to that text plus
    // CRLF as ASCII bytes). Rows are named by the producer call itself, captured
    // with CallerArgumentExpression, so a failing row reads e.g.
    // "SetDioPortDirection(0, 0)" in the test output. Adding a command is one
    // row, not a new method.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Theory rows of (producer call as written, message it produced, expected wire string).
    /// </summary>
    public sealed class ScpiCommandCases : TheoryData<string, IOutboundMessage<string>, string>
    {
        public void Add(
            IOutboundMessage<string> message,
            string expected,
            [CallerArgumentExpression(nameof(message))] string call = "")
        {
            Add(call.Replace("ScpiMessageProducer.", string.Empty, StringComparison.Ordinal), message, expected);
        }
    }

    // Commands that take no arguments: the string is the whole contract.
    public static ScpiCommandCases FixedCommands => new()
    {
        // Device & system
        { ScpiMessageProducer.RebootDevice, "SYSTem:REboot" },
        { ScpiMessageProducer.GetDeviceInfo, "SYSTem:SYSInfoPB?" },
        { ScpiMessageProducer.GetCapabilitiesApiVersion, "CONFigure:CAPabilities:APIVersion?" },
        { ScpiMessageProducer.GetCapabilitiesJson, "CONFigure:CAPabilities:JSON?" },
        { ScpiMessageProducer.DisableDeviceEcho, "SYSTem:ECHO -1" },
        { ScpiMessageProducer.EnableDeviceEcho, "SYSTem:ECHO 1" },
        { ScpiMessageProducer.TurnDeviceOn, "SYSTem:POWer:STATe 1" },
        { ScpiMessageProducer.ForceBootloader, "SYSTem:FORceBoot" },
        { ScpiMessageProducer.SaveDeviceName, "SYSTem:DEVice:NAME:SAVE" },
        { ScpiMessageProducer.GetIdentify, "SYSTem:IDENtify?" },

        // SD card
        { ScpiMessageProducer.EnableStorageSd, "SYSTem:STORage:SD:ENAble 1" },
        { ScpiMessageProducer.DisableStorageSd, "SYSTem:STORage:SD:ENAble 0" },
        { ScpiMessageProducer.GetSdFileList, "SYSTem:STORage:SD:LIST?" },
        { ScpiMessageProducer.FormatSdCard, "SYSTem:STORage:SD:FORmat" },
        { ScpiMessageProducer.GetSdMaxFileSize, "SYSTem:STORage:SD:MAXSize?" },
        { ScpiMessageProducer.GetSdBenchmarkResults, "SYSTem:STORage:SD:BENCHmark?" },
        { ScpiMessageProducer.GetSdSpace, "SYSTem:STORage:SD:SPACe?" },

        // Streaming
        { ScpiMessageProducer.StopStreaming, "SYSTem:StopStreamData" },
        { ScpiMessageProducer.SetProtobufStreamFormat, "SYSTem:STReam:FORmat 0" },
        { ScpiMessageProducer.SetJsonStreamFormat, "SYSTem:STReam:FORmat 1" },
        { ScpiMessageProducer.GetStreamFormat, "SYSTem:STReam:FORmat?" },
        { ScpiMessageProducer.GetStreamInterface, "SYSTem:STReam:INTerface?" },

        // Digital I/O
        { ScpiMessageProducer.EnableDioPorts(), "DIO:PORt:ENAble 1" },
        { ScpiMessageProducer.DisableDioPorts(), "DIO:PORt:ENAble 0" },

        // Network
        { ScpiMessageProducer.SetNetworkWifiModeSelfHosted, "SYSTem:COMMunicate:LAN:NETType 4" },
        { ScpiMessageProducer.SetNetworkWifiModeExisting, "SYSTem:COMMunicate:LAN:NETType 1" },
        { ScpiMessageProducer.SetNetworkWifiSecurityOpen, "SYSTem:COMMunicate:LAN:SECurity 0" },
        { ScpiMessageProducer.SetNetworkWifiSecurityWpa, "SYSTem:COMMunicate:LAN:SECurity 3" },
        { ScpiMessageProducer.GetLanAddress, "SYSTem:COMMunicate:LAN:ADDRess?" },
        { ScpiMessageProducer.GetLanMask, "SYSTem:COMMunicate:LAN:MASK?" },
        { ScpiMessageProducer.GetLanGateway, "SYSTem:COMMunicate:LAN:GATEway?" },
        { ScpiMessageProducer.GetLanConfiguredAddress, "SYSTem:COMMunicate:LAN:CONFigure:ADDRess?" },
        { ScpiMessageProducer.GetLanConfiguredMask, "SYSTem:COMMunicate:LAN:CONFigure:MASK?" },
        { ScpiMessageProducer.GetLanConfiguredGateway, "SYSTem:COMMunicate:LAN:CONFigure:GATEway?" },
        { ScpiMessageProducer.DisableNetworkLan, "SYSTem:COMMunicate:LAN:ENAbled 0" },
        { ScpiMessageProducer.EnableNetworkLan, "SYSTem:COMMunicate:LAN:ENAbled 1" },
        { ScpiMessageProducer.ApplyNetworkLan, "SYSTem:COMMunicate:LAN:APPLY" },
        { ScpiMessageProducer.SaveNetworkLan, "SYSTem:COMMunicate:LAN:SAVE" },
        { ScpiMessageProducer.LoadNetworkLan, "SYSTem:COMMunicate:LAN:LOAD" },
        { ScpiMessageProducer.FactoryResetNetworkLan, "SYSTem:COMMunicate:LAN:FACRESET" },
        { ScpiMessageProducer.SetLanFirmwareUpdateMode, "SYSTem:COMMUnicate:LAN:FWUpdate" },

        // ADC calibration & voltage precision
        { ScpiMessageProducer.SaveAdcCalibration, "CONFigure:ADC:SAVEcal" },
        { ScpiMessageProducer.LoadAdcCalibration, "CONFigure:ADC:LOADcal" },
        { ScpiMessageProducer.SaveFactoryAdcCalibration, "CONFigure:ADC:SAVEFcal" },
        { ScpiMessageProducer.LoadFactoryAdcCalibration, "CONFigure:ADC:LOADFcal" },
        { ScpiMessageProducer.GetAdcCalibrationBank, "CONFigure:ADC:USECal?" },
        { ScpiMessageProducer.SaveVoltagePrecision, "CONFigure:VOLTage:SAVE" },
        { ScpiMessageProducer.LoadVoltagePrecision, "CONFigure:VOLTage:LOAD" },

        // Analog output
        { ScpiMessageProducer.UpdateDacOutputs, "CONFigure:DAC:UPDATE" },

        // Logging & diagnostics
        { ScpiMessageProducer.GetSystemLog, "SYSTem:LOG?" },
        { ScpiMessageProducer.ClearSystemLog, "SYSTem:LOG:CLEar" },
        { ScpiMessageProducer.GetCommandHistory, "SYSTem:LOG:CMDHistory?" },
        { ScpiMessageProducer.TestSystemLog, "SYSTem:LOG:TEST" },
        { ScpiMessageProducer.GetSystemErrorCount, "SYSTem:ERRor:COUNt?" },
        { ScpiMessageProducer.GetStreamStats, "SYSTem:STReam:STATS?" },
        { ScpiMessageProducer.GetMemoryDiagnostics, "SYSTem:MEMory:FREE?" },
    };

    // Commands that format arguments into the string. Boundary rows (channel 0, a zero size,
    // a negative value) are here because each one is still a single fixed expectation; tests
    // that check behaviour beyond one string (culture, validation) live below.
    public static ScpiCommandCases ParameterisedCommands => new()
    {
        // Device & system
        { ScpiMessageProducer.SetDeviceName("My Device"), "SYSTem:DEVice:NAME \"My Device\"" },
        // Firmware v3.9.0 SCPI_SetIdentify accepts only the integers 0 and 1 (anything else is
        // -224), so the bool must map to exactly those, not ON/OFF or True/False.
        { ScpiMessageProducer.SetIdentify(true), "SYSTem:IDENtify 1" },
        { ScpiMessageProducer.SetIdentify(false), "SYSTem:IDENtify 0" },
        { ScpiMessageProducer.SetUsbTransparencyMode(1), "SYSTem:USB:SetTransparentMode 1" },

        // SD card
        { ScpiMessageProducer.GetSdFile("test.bin"), "SYSTem:STORage:SD:GET \"test.bin\"" },
        { ScpiMessageProducer.SetSdLoggingFileName("log.bin"), "SYSTem:STORage:SD:FILE \"log.bin\"" },
        { ScpiMessageProducer.DeleteSdFile("data.bin"), "SYSTem:STORage:SD:DELete \"data.bin\"" },
        { ScpiMessageProducer.SetSdMaxFileSize(1073741824), "SYSTem:STORage:SD:MAXSize 1073741824" },
        // Zero asks the firmware for its default maximum, so it must not be rejected.
        { ScpiMessageProducer.SetSdMaxFileSize(0), "SYSTem:STORage:SD:MAXSize 0" },
        { ScpiMessageProducer.RunSdBenchmark(1048576), "SYSTem:STORage:SD:BENCHmark 1048576" },
        { ScpiMessageProducer.SetSdMinFreeSpace(52428800), "SYSTem:STORage:SD:MINFree 52428800" },
        // Zero disables the free-space gate, so it must not be rejected.
        { ScpiMessageProducer.SetSdMinFreeSpace(0), "SYSTem:STORage:SD:MINFree 0" },

        // Streaming
        { ScpiMessageProducer.StartStreaming(100), "SYSTem:StartStreamData 100" },
        { ScpiMessageProducer.SetStreamInterface(StreamInterface.Usb), "SYSTem:STReam:INTerface 0" },
        { ScpiMessageProducer.SetStreamInterface(StreamInterface.WiFi), "SYSTem:STReam:INTerface 1" },
        { ScpiMessageProducer.SetStreamInterface(StreamInterface.SdCard), "SYSTem:STReam:INTerface 2" },
        { ScpiMessageProducer.SetStreamInterface(StreamInterface.All), "SYSTem:STReam:INTerface 3" },
        { ScpiMessageProducer.EnableAdcChannels("84"), "ENAble:VOLTage:DC 84" },

        // Digital I/O. Channel 0 is a real channel on every DAQiFi board, so the guard that
        // rejects negative channels must leave the lowest valid channel untouched, and both
        // documented directions (0 = input, 1 = output) must survive the direction guard.
        { ScpiMessageProducer.SetDioPortDirection(1, 1), "DIO:PORt:DIRection 1,1" },
        { ScpiMessageProducer.SetDioPortDirection(0, 0), "DIO:PORt:DIRection 0,0" },
        { ScpiMessageProducer.SetDioPortDirection(3, 0), "DIO:PORt:DIRection 3,0" },
        { ScpiMessageProducer.SetDioPortDirection(3, 1), "DIO:PORt:DIRection 3,1" },
        { ScpiMessageProducer.SetDioPortState(1, 1), "DIO:PORt:STATe 1,1" },
        { ScpiMessageProducer.SetDioPortState(0, 1), "DIO:PORt:STATe 0,1" },

        // PWM
        { ScpiMessageProducer.SetPwmChannelEnabled(4, true), "PWM:CHannel:ENable 4,1" },
        { ScpiMessageProducer.SetPwmChannelEnabled(4, false), "PWM:CHannel:ENable 4,0" },
        { ScpiMessageProducer.SetPwmChannelFrequency(0, 1000), "PWM:CHannel:FREQuency 0,1000" },
        { ScpiMessageProducer.SetPwmChannelDutyCycle(4, 50), "PWM:CHannel:DUTY 4,50" },

        // Network
        { ScpiMessageProducer.SetNetworkWifiSsid("MyNetwork"), "SYSTem:COMMunicate:LAN:SSID \"MyNetwork\"" },
        { ScpiMessageProducer.SetNetworkWifiPassword("password123"), "SYSTem:COMMunicate:LAN:PASs \"password123\"" },
        { ScpiMessageProducer.SetLanAddress(IPAddress.Parse("192.168.1.42")), "SYSTem:COMMunicate:LAN:ADDRess \"192.168.1.42\"" },
        { ScpiMessageProducer.SetLanMask(IPAddress.Parse("255.255.255.0")), "SYSTem:COMMunicate:LAN:MASK \"255.255.255.0\"" },
        { ScpiMessageProducer.SetLanGateway(IPAddress.Parse("192.168.1.1")), "SYSTem:COMMunicate:LAN:GATEway \"192.168.1.1\"" },

        // Per-channel ADC calibration constants (daqifi-core#386)
        { ScpiMessageProducer.SetAdcCalibrationSlope(2, 1.0025), "CONFigure:ADC:chanCALM 2,1.0025" },
        { ScpiMessageProducer.SetAdcCalibrationSlope(1, -0.5), "CONFigure:ADC:chanCALM 1,-0.5" },
        { ScpiMessageProducer.SetAdcCalibrationOffset(3, -0.0031), "CONFigure:ADC:chanCALB 3,-0.0031" },
        { ScpiMessageProducer.GetAdcCalibrationSlope(0), "CONFigure:ADC:chanCALM? 0" },
        { ScpiMessageProducer.GetAdcCalibrationOffset(5), "CONFigure:ADC:chanCALB? 5" },
        { ScpiMessageProducer.UseAdcCalibration(0), "CONFigure:ADC:USECal 0" },
        { ScpiMessageProducer.UseAdcCalibration(1), "CONFigure:ADC:USECal 1" },

        // Analog output. A negative channel is rejected, but a negative voltage is valid and
        // must render with a leading minus.
        { ScpiMessageProducer.SetAnalogOutputVoltage(0, 5.0), "SOURce:VOLTage:LEVel 0,5" },
        { ScpiMessageProducer.SetAnalogOutputVoltage(0, -3.3), "SOURce:VOLTage:LEVel 0,-3.3" },
        { ScpiMessageProducer.GetAnalogOutputVoltage(2), "SOURce:VOLTage:LEVel? 2" },

        // Logging & diagnostics
        { ScpiMessageProducer.SetLogLevel("STREAM", 2), "SYSTem:LOG:LEVel STREAM,2" },
    };

    [Theory]
    [MemberData(nameof(FixedCommands))]
    public void FixedCommand_ProducesExpectedWireString(string call, IOutboundMessage<string> message, string expected)
    {
        Assert.Equal((call, expected), (call, message.Data));
        AssertMessageFormat(message);
    }

    [Theory]
    [MemberData(nameof(ParameterisedCommands))]
    public void ParameterisedCommand_ProducesExpectedWireString(string call, IOutboundMessage<string> message, string expected)
    {
        Assert.Equal((call, expected), (call, message.Data));
        AssertMessageFormat(message);
    }

    // ---------------------------------------------------------------------
    // SD filenames
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetSdFile_WithNullOrEmptyFileName_Throws(string? fileName)
    {
        var ex = Assert.Throws<ArgumentException>(() => ScpiMessageProducer.GetSdFile(fileName!));
        Assert.Equal("fileName", ex.ParamName);
    }

    // The newline cases are the ones that matter most: the transport frames commands by line,
    // so anything after a \n in an interpolated filename arrives at the device as a command of
    // its own ("log.bin\nSYSTem:REboot" would reboot it). The SdCardOperations entry points
    // call the same validator, so this is the whole definition of a legal SD filename.
    [Theory]
    [InlineData("a\"b")]
    [InlineData("a;b")]
    [InlineData("log.bin\nSYSTem:REboot")]
    [InlineData("log.bin\rSYSTem:REboot")]
    public void GetSdFile_WithInjectionChars_Throws(string fileName)
    {
        var ex = Assert.Throws<ArgumentException>(() => ScpiMessageProducer.GetSdFile(fileName));
        Assert.Equal("fileName", ex.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SetSdLoggingFileName_WithNullOrEmptyFileName_Throws(string? fileName)
    {
        var ex = Assert.Throws<ArgumentException>(() => ScpiMessageProducer.SetSdLoggingFileName(fileName!));
        Assert.Equal("fileName", ex.ParamName);
    }

    [Theory]
    [InlineData("a\"b")]
    [InlineData("a;b")]
    [InlineData("log.bin\nSYSTem:REboot")]
    [InlineData("log.bin\rSYSTem:REboot")]
    public void SetSdLoggingFileName_WithInjectionChars_Throws(string fileName)
    {
        var ex = Assert.Throws<ArgumentException>(() => ScpiMessageProducer.SetSdLoggingFileName(fileName));
        Assert.Equal("fileName", ex.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DeleteSdFile_WithNullOrEmptyFileName_Throws(string? fileName)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => ScpiMessageProducer.DeleteSdFile(fileName!));
        Assert.Equal("fileName", ex.ParamName);
    }

    [Theory]
    [InlineData("a\"b")]
    [InlineData("a;b")]
    [InlineData("log.bin\nSYSTem:REboot")]
    [InlineData("log.bin\rSYSTem:REboot")]
    public void DeleteSdFile_WithInjectionChars_Throws(string fileName)
    {
        // DELete interpolates into the same quoted argument as GET and FILE, so it goes
        // through the same validator rather than keeping its own weaker null/empty-only check.
        var ex = Assert.Throws<ArgumentException>(() => ScpiMessageProducer.DeleteSdFile(fileName));
        Assert.Equal("fileName", ex.ParamName);
    }

    // The other side of the guard: names the device writes itself (log_yyyyMMdd_HHmmss plus
    // each format's extension), and the ordinary characters a user-chosen name carries --
    // spaces, extra dots, underscores, hyphens, a directory prefix, a long name -- are all
    // still passed through verbatim inside the quotes by every filename command.
    public static TheoryData<string> LegitimateSdFileNames => new()
    {
        "log_20260927_103158.bin",
        "log_20260927_103158.json",
        "log_20260927_103158.csv",
        "my run 2.bin",
        "bench.test.v2.bin",
        "run-01_final.csv",
        "Daqifi/log_20260927_103158.bin",
        new string('a', 200) + ".bin",
    };

    [Theory]
    [MemberData(nameof(LegitimateSdFileNames))]
    public void SdFileCommands_WithLegitimateFileName_PassItThroughVerbatim(string fileName)
    {
        Assert.Equal($"SYSTem:STORage:SD:GET \"{fileName}\"", ScpiMessageProducer.GetSdFile(fileName).Data);
        Assert.Equal($"SYSTem:STORage:SD:FILE \"{fileName}\"", ScpiMessageProducer.SetSdLoggingFileName(fileName).Data);
        Assert.Equal($"SYSTem:STORage:SD:DELete \"{fileName}\"", ScpiMessageProducer.DeleteSdFile(fileName).Data);
    }

    // ---------------------------------------------------------------------
    // Digital I/O state formatting
    // ---------------------------------------------------------------------

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

    // ---------------------------------------------------------------------
    // Network address validation
    // ---------------------------------------------------------------------

    public static TheoryData<string, Type, Func<IOutboundMessage<string>>> InvalidLanAddresses => new()
    {
        { "SetLanAddress(null)", typeof(ArgumentNullException), () => ScpiMessageProducer.SetLanAddress(null!) },
        { "SetLanAddress(IPv6)", typeof(ArgumentException), () => ScpiMessageProducer.SetLanAddress(IPAddress.IPv6Loopback) },
        { "SetLanMask(null)", typeof(ArgumentNullException), () => ScpiMessageProducer.SetLanMask(null!) },
        { "SetLanMask(IPv6)", typeof(ArgumentException), () => ScpiMessageProducer.SetLanMask(IPAddress.IPv6Loopback) },
        { "SetLanGateway(null)", typeof(ArgumentNullException), () => ScpiMessageProducer.SetLanGateway(null!) },
        { "SetLanGateway(IPv6)", typeof(ArgumentException), () => ScpiMessageProducer.SetLanGateway(IPAddress.IPv6Loopback) },
    };

    // The firmware only takes dotted-quad IPv4 here, so a null or IPv6 address is caller error.
    // The exact exception type is pinned (ArgumentNullException is not just any ArgumentException).
    [Theory]
    [MemberData(nameof(InvalidLanAddresses))]
    public void SetLanAddressFamily_WithNullOrIPv6_Throws(string call, Type expectedException, Func<IOutboundMessage<string>> act)
    {
        var ex = Record.Exception(() => act());

        // Compared as one tuple so a failure names the row; the type is matched exactly.
        Assert.Equal((call, expectedException), (call, ex?.GetType()));
    }

    // ---------------------------------------------------------------------
    // Per-channel ADC calibration-constant formatting (daqifi-core#386)
    // ---------------------------------------------------------------------

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

    // ---------------------------------------------------------------------
    // Analog output formatting
    // ---------------------------------------------------------------------

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

    // ---------------------------------------------------------------------
    // Logging module validation
    // ---------------------------------------------------------------------

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

    // ---------------------------------------------------------------------
    // Device friendly name
    // ---------------------------------------------------------------------

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

    // ---------------------------------------------------------------------
    // Argument-guard censuses
    // ---------------------------------------------------------------------

    // The one place negative-channel rejection is pinned for every channel-addressed
    // producer: what callers can observe about the throw (parameter name, actual value,
    // message) must be identical, because they all share one ValidateChannel helper.
    //
    // The list is the whole of ScpiMessageProducer's channel-addressed surface: the two
    // DIO producers were the odd ones out until they were brought in, so a producer added
    // here without the guard fails this test rather than quietly shipping an unguarded one.
    //
    // The DIO rows also pass an invalid value (direction 7, state NaN). The channel guard
    // runs first, so the caller is told about `channel` rather than about the value,
    // matching the PWM and analog setters, which all validate the channel before the value.
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
            (nameof(ScpiMessageProducer.SetDioPortDirection), () => ScpiMessageProducer.SetDioPortDirection(-1, 7)),
            (nameof(ScpiMessageProducer.SetDioPortState), () => ScpiMessageProducer.SetDioPortState(-1, double.NaN)),
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
    // the other axis: every producer in this class whose *value* argument has a documented
    // range. Each guard is written out at its own call site (the ranges differ, so there is
    // no shared helper to hold the rule), which is exactly why the observable shape needs
    // pinning in one place — SetDioPortDirection was the one that had drifted, emitting a
    // frame for any int at all. Anything the caller can see about the throw is compared
    // across all of them rather than against a literal copied out of the source.
    //
    // This list is the COMPLETE set of inline value-range guards in ScpiMessageProducer, not
    // a sample: it is the whole population of `throw new ArgumentOutOfRangeException` sites
    // outside the shared ValidateChannel/RequireFinite helpers — verified by grepping that
    // throw against this table. A producer that gains a ranged value argument must be added
    // here. Where a range is bounded on both sides, both edges have a row (and
    // SetDioPortDirection also int.MaxValue). The distinct-producer count below is a
    // deliberate tripwire, not a proof of completeness: it fails the moment someone edits
    // the table without reading this note, which is the same mechanism
    // EveryChannelAddressedProducer_RejectsNegativeChannelIdentically uses.
    [Fact]
    public void EveryValueRangedProducer_RejectsOutOfRangeArgumentIdentically()
    {
        var producers = new (string Name, string ParamName, object ActualValue, string MessagePrefix, Action Act)[]
        {
            (nameof(ScpiMessageProducer.SetPwmChannelFrequency), "frequencyHz", 0, "Frequency must be positive.",
                () => ScpiMessageProducer.SetPwmChannelFrequency(0, 0)),
            (nameof(ScpiMessageProducer.SetPwmChannelDutyCycle), "dutyCyclePercent", 101, "Duty cycle must be 0-100 percent.",
                () => ScpiMessageProducer.SetPwmChannelDutyCycle(0, 101)),
            (nameof(ScpiMessageProducer.SetDioPortDirection), "direction", -1, "Direction must be 0 (input) or 1 (output).",
                () => ScpiMessageProducer.SetDioPortDirection(0, -1)),
            (nameof(ScpiMessageProducer.SetDioPortDirection), "direction", 2, "Direction must be 0 (input) or 1 (output).",
                () => ScpiMessageProducer.SetDioPortDirection(0, 2)),
            (nameof(ScpiMessageProducer.SetDioPortDirection), "direction", int.MaxValue, "Direction must be 0 (input) or 1 (output).",
                () => ScpiMessageProducer.SetDioPortDirection(0, int.MaxValue)),
            (nameof(ScpiMessageProducer.UseAdcCalibration), "bank", -1, "Calibration bank must be 0 (factory) or 1 (user).",
                () => ScpiMessageProducer.UseAdcCalibration(-1)),
            (nameof(ScpiMessageProducer.UseAdcCalibration), "bank", 2, "Calibration bank must be 0 (factory) or 1 (user).",
                () => ScpiMessageProducer.UseAdcCalibration(2)),
            (nameof(ScpiMessageProducer.SetLogLevel), "level", -1, "Log level must be between 0 (None) and 3 (Debug).",
                () => ScpiMessageProducer.SetLogLevel("STREAM", -1)),
            (nameof(ScpiMessageProducer.SetLogLevel), "level", 4, "Log level must be between 0 (None) and 3 (Debug).",
                () => ScpiMessageProducer.SetLogLevel("ADC", 4)),
            (nameof(ScpiMessageProducer.SetSdMaxFileSize), "bytes", -1L, "Max file size cannot be negative.",
                () => ScpiMessageProducer.SetSdMaxFileSize(-1)),
            (nameof(ScpiMessageProducer.SetSdMinFreeSpace), "bytes", -1L, "Minimum free space cannot be negative.",
                () => ScpiMessageProducer.SetSdMinFreeSpace(-1)),
            (nameof(ScpiMessageProducer.RunSdBenchmark), "size", 0L, "Benchmark size must be positive.",
                () => ScpiMessageProducer.RunSdBenchmark(0)),
            (nameof(ScpiMessageProducer.RunSdBenchmark), "size", -1L, "Benchmark size must be positive.",
                () => ScpiMessageProducer.RunSdBenchmark(-1)),
            // An undefined enum value is the same rule expressed over an enum's range, and it
            // throws the same exception in the same shape, so it belongs in the census too.
            (nameof(ScpiMessageProducer.SetStreamInterface), "streamInterface", (StreamInterface)999, "Unknown stream interface.",
                () => ScpiMessageProducer.SetStreamInterface((StreamInterface)999)),
        };

        Assert.Equal(9, producers.Select(p => p.Name).Distinct().Count());
        foreach (var (name, paramName, actualValue, messagePrefix, act) in producers)
        {
            var ex = Assert.Throws<ArgumentOutOfRangeException>(act);

            // Compared as one tuple so a failure names the producer (and value) that drifted.
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
