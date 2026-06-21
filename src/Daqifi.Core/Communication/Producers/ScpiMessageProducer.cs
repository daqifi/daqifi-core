using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Daqifi.Core.Communication.Messages;

namespace Daqifi.Core.Communication.Producers;

/// <summary>
/// Produces SCPI (Standard Commands for Programmable Instruments) messages for DAQiFi devices.
/// </summary>
/// <remarks> 
/// Example usage:
/// <code>
/// // Send a command
/// messageProducer.Send(ScpiMessageProducer.Reboot);
/// 
/// // Send a query
/// messageProducer.Send(ScpiMessageProducer.SystemInfo);
/// </code>
/// </remarks>
public class ScpiMessageProducer
{
    /// <summary>
    /// Creates a command message to reboot the device.
    /// </summary>
    /// <remarks>
    /// This command will cause the device to perform a complete restart.
    /// Command: SYSTem:REboot
    /// Example: messageProducer.Send(ScpiMessageProducer.Reboot);
    /// </remarks>
    public static IOutboundMessage<string> RebootDevice => new ScpiMessage("SYSTem:REboot");

    /// <summary>
    /// Creates a query message to request system information in protocol buffer format.
    /// </summary>
    /// <remarks>
    /// Returns device information including firmware version, serial number, and capabilities.
    /// Command: SYSTem:SYSInfoPB?
    /// Example: messageProducer.Send(ScpiMessageProducer.SystemInfo);
    /// </remarks>
    public static IOutboundMessage<string> GetDeviceInfo => new ScpiMessage("SYSTem:SYSInfoPB?");

    /// <summary>
    /// Creates a query message to pop the next entry from the device's SCPI error queue.
    /// </summary>
    /// <remarks>
    /// Returns the oldest queued error in standard SCPI format (e.g., <c>-200,"Execution error"</c>),
    /// or <c>0,"No error"</c> when the queue is empty. Each call removes one entry.
    /// Command: SYSTem:ERRor?
    /// </remarks>
    public static IOutboundMessage<string> GetSystemError => new ScpiMessage("SYSTem:ERRor?");

    /// <summary>
    /// Creates a command message to force the device into bootloader mode.
    /// </summary>
    /// <remarks>
    /// Command: SYSTem:FORceBoot
    /// Example: messageProducer.Send(ScpiMessageProducer.ForceBootloader);
    /// </remarks>
    public static IOutboundMessage<string> ForceBootloader => new ScpiMessage("SYSTem:FORceBoot");

    /// <summary>
    /// Creates a command message to turn the device on.
    /// </summary>
    /// <remarks>
    /// Command: SYSTem:POWer:STATe 1
    /// Example: messageProducer.Send(ScpiMessageProducer.DeviceOn);
    /// </remarks>
    public static IOutboundMessage<string> TurnDeviceOn => new ScpiMessage("SYSTem:POWer:STATe 1");

    /// <summary>
    /// Creates a command message to disable echo functionality.
    /// </summary>
    /// <remarks>
    /// When echo is disabled, the device will not echo back received commands.
    /// Command: SYSTem:ECHO -1
    /// Example: messageProducer.Send(ScpiMessageProducer.TurnOffEcho);
    /// </remarks>
    public static IOutboundMessage<string> DisableDeviceEcho => new ScpiMessage("SYSTem:ECHO -1");

    /// <summary>
    /// Creates a command message to enable echo functionality.
    /// </summary>
    /// <remarks>
    /// When echo is enabled, the device will echo back received commands.
    /// Command: SYSTem:ECHO 1
    /// Example: messageProducer.Send(ScpiMessageProducer.TurnOnEcho);
    /// </remarks>
    public static IOutboundMessage<string> EnableDeviceEcho => new ScpiMessage("SYSTem:ECHO 1");
    
    /// <summary>
    /// Creates a command message to enable SD card logging.
    /// </summary>
    /// <remarks>
    /// Note: LAN must be disabled first to enable SD card logging.
    /// Command: SYSTem:STORage:SD:ENAble 1
    /// Example: messageProducer.Send(ScpiMessageProducer.EnableSdCard);
    /// </remarks>
    public static IOutboundMessage<string> EnableStorageSd => new ScpiMessage("SYSTem:STORage:SD:ENAble 1");

    /// <summary>
    /// Creates a command message to disable SD card logging.
    /// </summary>
    /// <remarks>
    /// Command: SYSTem:STORage:SD:ENAble 0
    /// Example: messageProducer.Send(ScpiMessageProducer.DisableSdCard);
    /// </remarks>
    public static IOutboundMessage<string> DisableStorageSd => new ScpiMessage("SYSTem:STORage:SD:ENAble 0");

    /// <summary>
    /// Creates a query message to get the list of files on the SD card.
    /// </summary>
    /// <remarks>
    /// Command: SYSTem:STORage:SD:LIST?
    /// Example: messageProducer.Send(ScpiMessageProducer.GetSdFileList);
    /// </remarks>
    public static IOutboundMessage<string> GetSdFileList => new ScpiMessage("SYSTem:STORage:SD:LIST?");

    /// <summary>
    /// Creates a query message to retrieve a specific file from the SD card.
    /// </summary>
    /// <param name="fileName">The name of the file to retrieve. Provide the bare name without surrounding quotes; they are added automatically.</param>
    /// <remarks>
    /// Command: SYSTem:STORage:SD:GET "filename.bin"
    /// Example: messageProducer.Send(ScpiMessageProducer.GetSdFile("data.bin"));
    /// </remarks>
    public static IOutboundMessage<string> GetSdFile(string fileName)
    {
        return new ScpiMessage($"SYSTem:STORage:SD:GET \"{fileName}\"");
    }

    /// <summary>
    /// Creates a command message to set the logging file name on the SD card.
    /// </summary>
    /// <param name="fileName">The name of the file to create or append to. Provide the bare name without surrounding quotes; they are added automatically.</param>
    /// <remarks>
    /// The specified file will be created if it doesn't exist, or appended to if it already exists.
    /// Command: SYSTem:STORage:SD:FILE "filename.bin"
    /// Requires firmware v3.5.0 or newer; the command was renamed from
    /// <c>SYSTem:STORage:SD:LOGging</c> and older firmware does not accept it (see daqifi-core#251).
    /// Example: messageProducer.Send(ScpiMessageProducer.SetSdLoggingFileName("data.bin"));
    /// </remarks>
    public static IOutboundMessage<string> SetSdLoggingFileName(string fileName)
    {
        return new ScpiMessage($"SYSTem:STORage:SD:FILE \"{fileName}\"");
    }

    /// <summary>
    /// Creates a command message to delete a file from the SD card.
    /// </summary>
    /// <param name="fileName">The name of the file to delete.</param>
    /// <remarks>
    /// Command: SYSTem:STORage:SD:DELete "filename"
    /// Example: messageProducer.Send(ScpiMessageProducer.DeleteSdFile("data.bin"));
    /// </remarks>
    public static IOutboundMessage<string> DeleteSdFile(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("Filename cannot be null or empty.", nameof(fileName));
        }

        return new ScpiMessage($"SYSTem:STORage:SD:DELete \"{fileName}\"");
    }

    /// <summary>
    /// Creates a command message to format the entire SD card.
    /// </summary>
    /// <remarks>
    /// Warning: This is a destructive operation that erases all data on the SD card.
    /// Command: SYSTem:STORage:SD:FORmat
    /// Example: messageProducer.Send(ScpiMessageProducer.FormatSdCard);
    /// </remarks>
    public static IOutboundMessage<string> FormatSdCard => new ScpiMessage("SYSTem:STORage:SD:FORmat");

    /// <summary>
    /// Creates a command message to set the maximum file size before auto-splitting on the SD card.
    /// </summary>
    /// <param name="bytes">The maximum file size in bytes. Use 0 for the default (3.9 GB).</param>
    /// <remarks>
    /// Command: SYSTem:STORage:SD:MAXSize bytes
    /// Example: messageProducer.Send(ScpiMessageProducer.SetSdMaxFileSize(1073741824)); // 1 GB
    /// </remarks>
    public static IOutboundMessage<string> SetSdMaxFileSize(long bytes)
    {
        if (bytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), bytes, "Max file size cannot be negative.");
        }

        return new ScpiMessage($"SYSTem:STORage:SD:MAXSize {bytes}");
    }

    /// <summary>
    /// Creates a query message to get the current maximum file size setting for the SD card.
    /// </summary>
    /// <remarks>
    /// Command: SYSTem:STORage:SD:MAXSize?
    /// Example: messageProducer.Send(ScpiMessageProducer.GetSdMaxFileSize);
    /// </remarks>
    public static IOutboundMessage<string> GetSdMaxFileSize => new ScpiMessage("SYSTem:STORage:SD:MAXSize?");

    /// <summary>
    /// Creates a query message to get the free and total byte counts of the SD card.
    /// </summary>
    /// <remarks>
    /// Returns a single line of the form <c>"free,total"</c>, where both values are
    /// unsigned byte counts.
    /// Command: SYSTem:STORage:SD:SPACe?
    /// Example: messageProducer.Send(ScpiMessageProducer.GetSdSpace);
    /// </remarks>
    public static IOutboundMessage<string> GetSdSpace => new ScpiMessage("SYSTem:STORage:SD:SPACe?");

    /// <summary>
    /// Creates a command message to set the firmware-enforced minimum free-space floor on the SD card.
    /// When the free space would drop below this threshold, the firmware refuses to start an SD-output
    /// stream (responding with <c>-200 "Execution error"</c>) rather than silently truncating the capture.
    /// </summary>
    /// <param name="bytes">The minimum free space to keep available, in bytes. Use 0 to disable the gate
    /// (the firmware default).</param>
    /// <remarks>
    /// First available on firmware v3.5.0 — at or below the v3.5.0 supported floor, so safe to send
    /// unconditionally. The firmware gate is a safety mechanism; client software is responsible for the
    /// user-facing low-space warning (see <see cref="Device.SdCard.SdCardSpaceCheck"/>).
    /// Command: SYSTem:STORage:SD:MINFree bytes
    /// Example: messageProducer.Send(ScpiMessageProducer.SetSdMinFreeSpace(52428800)); // 50 MB floor
    /// </remarks>
    public static IOutboundMessage<string> SetSdMinFreeSpace(long bytes)
    {
        if (bytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), bytes, "Minimum free space cannot be negative.");
        }

        return new ScpiMessage($"SYSTem:STORage:SD:MINFree {bytes}");
    }

    /// <summary>
    /// Creates a command message to run an SD card write speed benchmark.
    /// </summary>
    /// <param name="size">The size in bytes to benchmark.</param>
    /// <remarks>
    /// Command: SYSTem:STORage:SD:BENCHmark size
    /// Example: messageProducer.Send(ScpiMessageProducer.RunSdBenchmark(1048576)); // 1 MB
    /// </remarks>
    public static IOutboundMessage<string> RunSdBenchmark(long size)
    {
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "Benchmark size must be positive.");
        }

        return new ScpiMessage($"SYSTem:STORage:SD:BENCHmark {size}");
    }

    /// <summary>
    /// Creates a query message to get the SD card benchmark results.
    /// </summary>
    /// <remarks>
    /// Command: SYSTem:STORage:SD:BENCHmark?
    /// Example: messageProducer.Send(ScpiMessageProducer.GetSdBenchmarkResults);
    /// </remarks>
    public static IOutboundMessage<string> GetSdBenchmarkResults => new ScpiMessage("SYSTem:STORage:SD:BENCHmark?");

    /// <summary>
    /// Creates a command message to set the streaming target interface.
    /// </summary>
    /// <param name="streamInterface">The target interface for streaming data.</param>
    /// <remarks>
    /// Command: SYSTem:STReam:INTerface value
    /// Example: messageProducer.Send(ScpiMessageProducer.SetStreamInterface(StreamInterface.SdCard));
    /// </remarks>
    public static IOutboundMessage<string> SetStreamInterface(StreamInterface streamInterface)
    {
        if (!Enum.IsDefined(typeof(StreamInterface), streamInterface))
        {
            throw new ArgumentOutOfRangeException(nameof(streamInterface), streamInterface, "Unknown stream interface.");
        }

        return new ScpiMessage($"SYSTem:STReam:INTerface {(int)streamInterface}");
    }

    /// <summary>
    /// Creates a query message to get the current streaming target interface.
    /// </summary>
    /// <remarks>
    /// Command: SYSTem:STReam:INTerface?
    /// Example: messageProducer.Send(ScpiMessageProducer.GetStreamInterface);
    /// </remarks>
    public static IOutboundMessage<string> GetStreamInterface => new ScpiMessage("SYSTem:STReam:INTerface?");

    /// <summary>
    /// Creates a command message to start data streaming at the specified frequency.
    /// </summary>
    /// <param name="frequency">The streaming frequency in Hz (1-1000).</param>
    /// <remarks>
    /// Starts streaming data from enabled channels at the specified frequency.
    /// Command: SYSTem:StartStreamData frequency
    /// Example: messageProducer.Send(ScpiMessageProducer.StartStreaming(100)); // Stream at 100Hz
    /// </remarks>
    public static IOutboundMessage<string> StartStreaming(int frequency)
    {
        return new ScpiMessage($"SYSTem:StartStreamData {frequency}");
    }

    /// <summary>
    /// Creates a command message to stop data streaming.
    /// </summary>
    /// <remarks>
    /// Command: SYSTem:StopStreamData
    /// Example: messageProducer.Send(ScpiMessageProducer.StopStreaming);
    /// </remarks>
    public static IOutboundMessage<string> StopStreaming => new ScpiMessage("SYSTem:StopStreamData");

    /// <summary>
    /// Creates a command message to set the stream format to Protocol Buffer.
    /// </summary>
    /// <remarks>
    /// Sets the stream format to Protobuf (format = 0).
    /// Command: SYSTem:STReam:FORmat 0
    /// Example: messageProducer.Send(ScpiMessageProducer.SetProtobufStreamFormat);
    /// </remarks>
    public static IOutboundMessage<string> SetProtobufStreamFormat => new ScpiMessage("SYSTem:STReam:FORmat 0");

    /// <summary>
    /// Creates a command message to set the stream format to JSON.
    /// </summary>
    /// <remarks>
    /// Sets the stream format to JSON (format = 1).
    /// Command: SYSTem:STReam:FORmat 1
    /// Example: messageProducer.Send(ScpiMessageProducer.SetJsonStreamFormat);
    /// </remarks>
    public static IOutboundMessage<string> SetJsonStreamFormat => new ScpiMessage("SYSTem:STReam:FORmat 1");

    /// <summary>
    /// Creates a command message to set the stream format to CSV.
    /// </summary>
    /// <remarks>
    /// Sets the stream format to CSV (format = 2).
    /// Command: SYSTem:STReam:FORmat 2
    /// Example: messageProducer.Send(ScpiMessageProducer.SetCsvStreamFormat);
    /// </remarks>
    public static IOutboundMessage<string> SetCsvStreamFormat => new ScpiMessage("SYSTem:STReam:FORmat 2");

    /// <summary>
    /// Creates a query message to get the current stream format.
    /// </summary>
    /// <remarks>
    /// Returns the current stream format:
    /// - 0 = Protobuf
    /// - 1 = JSON
    /// - 2 = CSV
    /// Command: SYSTem:STReam:FORmat?
    /// Example: messageProducer.Send(ScpiMessageProducer.GetStreamFormat);
    /// </remarks>
    public static IOutboundMessage<string> GetStreamFormat => new ScpiMessage("SYSTem:STReam:FORmat?");

    /// <summary>
    /// Creates a command message to enable ADC channels using a decimal bitmask.
    /// </summary>
    /// <param name="channelSetString">A decimal integer string representing a bitmask where each bit enables a channel. For example, "84" (0b1010100) enables channels 2, 4, and 6.</param>
    /// <remarks>
    /// The firmware parses this value as a decimal integer and interprets it as a bitmask:
    /// - Bit 0 (value 1): Channel 0
    /// - Bit 1 (value 2): Channel 1
    /// - Bit 2 (value 4): Channel 2
    /// etc.
    ///
    /// Command: ENAble:VOLTage:DC decimalMask
    /// <code>
    /// // Enable channels 2, 4, and 6 (bitmask = 4 + 16 + 64 = 84)
    /// device.Send(ScpiMessageProducer.EnableAdcChannels("84"));
    ///
    /// // Enable channels 0 and 1 (bitmask = 1 + 2 = 3)
    /// device.Send(ScpiMessageProducer.EnableAdcChannels("3"));
    /// </code>
    /// </remarks>
    public static IOutboundMessage<string> EnableAdcChannels(string channelSetString)
    {
        return new ScpiMessage($"ENAble:VOLTage:DC {channelSetString}");
    }

    /// <summary>
    /// Creates a command message to set the direction of a digital I/O port.
    /// </summary>
    /// <param name="channel">The channel number.</param>
    /// <param name="direction">The direction value (0 = input, 1 = output).</param>
    /// <remarks>
    /// Command: DIO:PORt:DIRection channel,direction
    /// Example: messageProducer.Send(ScpiMessageProducer.SetDioPortDirection(1, 1)); // Set channel 1 as output
    /// </remarks>
    public static IOutboundMessage<string> SetDioPortDirection(int channel, int direction)
    {
        return new ScpiMessage($"DIO:PORt:DIRection {channel},{direction}");
    }

    /// <summary>
    /// Creates a command message to set the state of a digital I/O port.
    /// </summary>
    /// <param name="channel">The channel number.</param>
    /// <param name="value">The state value (0 = low, 1 = high).</param>
    /// <remarks>
    /// Command: DIO:PORt:STATe channel,value
    /// Example: messageProducer.Send(ScpiMessageProducer.SetDioPortState(1, 1)); // Set channel 1 to high
    /// </remarks>
    public static IOutboundMessage<string> SetDioPortState(int channel, double value)
    {
        return new ScpiMessage($"DIO:PORt:STATe {channel},{value}");
    }

    /// <summary>
    /// Creates a command message to enable all digital I/O ports.
    /// </summary>
    /// <remarks>
    /// Command: DIO:PORt:ENAble 1
    /// Example: messageProducer.Send(ScpiMessageProducer.EnableDioPorts());
    /// </remarks>
    public static IOutboundMessage<string> EnableDioPorts()
    {
        return new ScpiMessage("DIO:PORt:ENAble 1");
    }

    /// <summary>
    /// Creates a command message to disable all digital I/O ports.
    /// </summary>
    /// <remarks>
    /// Command: DIO:PORt:ENAble 0
    /// Example: messageProducer.Send(ScpiMessageProducer.DisableDioPorts());   
    /// </remarks>
    public static IOutboundMessage<string> DisableDioPorts()
    {
        return new ScpiMessage("DIO:PORt:ENAble 0");
    }

    /// <summary>
    /// Creates a command message to stage an analog output (DAC) voltage on a channel.
    /// </summary>
    /// <param name="channel">The analog output channel number.</param>
    /// <param name="voltage">The output voltage, in volts.</param>
    /// <remarks>
    /// Analog output is available on NQ3 hardware only. The staged value is applied on
    /// the next <see cref="UpdateDacOutputs"/>, allowing multiple channels to be updated
    /// together. The voltage is formatted with an invariant decimal point so the command
    /// is locale-independent.
    /// Command: SOURce:VOLTage:LEVel channel,voltage
    /// Example: messageProducer.Send(ScpiMessageProducer.SetAnalogOutputVoltage(0, 5.0)); // Channel 0 to 5 V
    /// </remarks>
    public static IOutboundMessage<string> SetAnalogOutputVoltage(int channel, double voltage)
    {
        if (channel < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channel), channel, "Channel number cannot be negative.");
        }

        if (!double.IsFinite(voltage))
        {
            // NaN/Infinity would render as "NaN"/"Infinity" — tokens the firmware cannot parse.
            throw new ArgumentOutOfRangeException(nameof(voltage), voltage, "Voltage must be a finite number.");
        }

        return new ScpiMessage($"SOURce:VOLTage:LEVel {channel},{voltage.ToString(CultureInfo.InvariantCulture)}");
    }

    /// <summary>
    /// Creates a command message to apply all staged analog output (DAC) voltages.
    /// </summary>
    /// <remarks>
    /// Latches the values previously staged via <see cref="SetAnalogOutputVoltage"/> so they
    /// take effect on the hardware. Analog output is available on NQ3 hardware only.
    /// Command: CONFigure:DAC:UPDATE
    /// Example: messageProducer.Send(ScpiMessageProducer.UpdateDacOutputs);
    /// </remarks>
    public static IOutboundMessage<string> UpdateDacOutputs => new ScpiMessage("CONFigure:DAC:UPDATE");

    /// <summary>
    /// Creates a command message to set the device to create its own WiFi network (Self-Hosted mode).
    /// </summary>
    /// <remarks>
    /// Command: SYSTem:COMMunicate:LAN:NETType 4
    /// </remarks>
    public static IOutboundMessage<string> SetNetworkWifiModeSelfHosted => new ScpiMessage("SYSTem:COMMunicate:LAN:NETType 4");

    /// <summary>
    /// Creates a command message to set the device to connect to an existing WiFi network.
    /// </summary>
    /// <remarks>
    /// Command: SYSTem:COMMunicate:LAN:NETType 1
    /// </remarks>
    public static IOutboundMessage<string> SetNetworkWifiModeExisting => new ScpiMessage("SYSTem:COMMunicate:LAN:NETType 1");

    /// <summary>
    /// Creates a command message to set the SSID for the WiFi network.
    /// </summary>
    /// <param name="ssid">The SSID of the WiFi network.</param>
    /// <remarks>
    /// Command: SYSTem:COMMunicate:LAN:SSID "ssid" 
    /// Example: messageProducer.Send(ScpiMessageProducer.SetSsid("MyNetwork"));
    /// </remarks>
    public static IOutboundMessage<string> SetNetworkWifiSsid(string ssid)
    {
        return new ScpiMessage($"SYSTem:COMMunicate:LAN:SSID \"{ssid}\"");
    }

    /// <summary>
    /// Creates a command message to set WiFi security to an open network (no security).
    /// </summary>
    /// <remarks>
    /// Command: SYSTem:COMMunicate:LAN:SECurity 0
    /// </remarks>
    public static IOutboundMessage<string> SetNetworkWifiSecurityOpen => new ScpiMessage("SYSTem:COMMunicate:LAN:SECurity 0");

    /// <summary>
    /// Creates a command message to set WiFi security to WPA with passphrase.
    /// </summary>
    /// <remarks>
    /// Command: SYSTem:COMMunicate:LAN:SECurity 3
    /// </remarks>
    public static IOutboundMessage<string> SetNetworkWifiSecurityWpa => new ScpiMessage("SYSTem:COMMunicate:LAN:SECurity 3");

    /// <summary>
    /// Creates a command message to set the password for the WiFi network.
    /// </summary>
    /// <param name="password">The password for the WiFi network.</param>
    /// <remarks>
    /// Command: SYSTem:COMMunicate:LAN:PASs "password"
    /// </remarks>
    public static IOutboundMessage<string> SetNetworkWifiPassword(string password)
    {
        return new ScpiMessage($"SYSTem:COMMunicate:LAN:PASs \"{password}\"");
    }
        
    /// <summary>
    /// Creates a command message to set the static IP address for the LAN.
    /// </summary>
    /// <param name="address">The static IP address to assign to the device.</param>
    /// <remarks>
    /// The new value is staged into the device's runtime WiFi settings and takes
    /// effect on the next <see cref="ApplyNetworkLan"/>. Persist across reboots
    /// with <see cref="SaveNetworkLan"/>.
    /// Command: SYSTem:COMMunicate:LAN:ADDRess "x.x.x.x"
    /// </remarks>
    public static IOutboundMessage<string> SetLanAddress(IPAddress address)
    {
        RequireIPv4(address, nameof(address));
        return new ScpiMessage($"SYSTem:COMMunicate:LAN:ADDRess \"{address}\"");
    }

    /// <summary>
    /// Creates a command message to set the subnet mask for the LAN.
    /// </summary>
    /// <param name="mask">The subnet mask to assign to the device.</param>
    /// <remarks>
    /// Command: SYSTem:COMMunicate:LAN:MASK "x.x.x.x"
    /// </remarks>
    public static IOutboundMessage<string> SetLanMask(IPAddress mask)
    {
        RequireIPv4(mask, nameof(mask));
        return new ScpiMessage($"SYSTem:COMMunicate:LAN:MASK \"{mask}\"");
    }

    /// <summary>
    /// Creates a command message to set the default gateway for the LAN.
    /// </summary>
    /// <param name="gateway">The default gateway to assign to the device.</param>
    /// <remarks>
    /// Command: SYSTem:COMMunicate:LAN:GATEway "x.x.x.x"
    /// </remarks>
    public static IOutboundMessage<string> SetLanGateway(IPAddress gateway)
    {
        RequireIPv4(gateway, nameof(gateway));
        return new ScpiMessage($"SYSTem:COMMunicate:LAN:GATEway \"{gateway}\"");
    }

    // Firmware's LAN setters parse the payload via inet_addr (IPv4 dotted quad
    // only — see SCPILAN.c's SCPI_LANAddrSetImpl). IPv6 inputs would stringify
    // with colons and silently mis-set on the device, so reject them here.
    private static void RequireIPv4(IPAddress address, string paramName)
    {
        if (address == null)
        {
            throw new ArgumentNullException(paramName);
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException(
                "Address must be IPv4 (AddressFamily.InterNetwork).",
                paramName);
        }
    }

    /// <summary>
    /// Creates a query message to read the staged (pre-apply) LAN IP address.
    /// </summary>
    /// <remarks>
    /// Returns the address currently in the device's runtime WiFi settings — the value
    /// that <see cref="ApplyNetworkLan"/> would push next, which may differ from the
    /// active address reported by <see cref="GetLanAddress"/>.
    /// Command: SYSTem:COMMunicate:LAN:CONFigure:ADDRess?
    /// </remarks>
    public static IOutboundMessage<string> GetLanConfiguredAddress => new ScpiMessage("SYSTem:COMMunicate:LAN:CONFigure:ADDRess?");

    /// <summary>
    /// Creates a query message to read the staged (pre-apply) LAN subnet mask.
    /// </summary>
    /// <remarks>
    /// Command: SYSTem:COMMunicate:LAN:CONFigure:MASK?
    /// </remarks>
    public static IOutboundMessage<string> GetLanConfiguredMask => new ScpiMessage("SYSTem:COMMunicate:LAN:CONFigure:MASK?");

    /// <summary>
    /// Creates a query message to read the staged (pre-apply) LAN default gateway.
    /// </summary>
    /// <remarks>
    /// Command: SYSTem:COMMunicate:LAN:CONFigure:GATEway?
    /// </remarks>
    public static IOutboundMessage<string> GetLanConfiguredGateway => new ScpiMessage("SYSTem:COMMunicate:LAN:CONFigure:GATEway?");

    /// <summary>
    /// Creates a query message to read the active LAN IP address.
    /// </summary>
    /// <remarks>
    /// Command: SYSTem:COMMunicate:LAN:ADDRess?
    /// </remarks>
    public static IOutboundMessage<string> GetLanAddress => new ScpiMessage("SYSTem:COMMunicate:LAN:ADDRess?");

    /// <summary>
    /// Creates a query message to read the active LAN subnet mask.
    /// </summary>
    /// <remarks>
    /// Command: SYSTem:COMMunicate:LAN:MASK?
    /// </remarks>
    public static IOutboundMessage<string> GetLanMask => new ScpiMessage("SYSTem:COMMunicate:LAN:MASK?");

    /// <summary>
    /// Creates a query message to read the active LAN default gateway.
    /// </summary>
    /// <remarks>
    /// Command: SYSTem:COMMunicate:LAN:GATEway?
    /// </remarks>
    public static IOutboundMessage<string> GetLanGateway => new ScpiMessage("SYSTem:COMMunicate:LAN:GATEway?");

    /// <summary>
    /// Creates a command message to disable LAN communication.
    /// </summary>
    /// <remarks>
    /// Command: SYSTem:COMMunicate:LAN:ENAbled 0
    /// </remarks>
    public static IOutboundMessage<string> DisableNetworkLan => new ScpiMessage("SYSTem:COMMunicate:LAN:ENAbled 0");
        
    /// <summary>
    /// Creates a command message to enable LAN communication.
    /// </summary>
    /// <remarks>
    /// Command: SYSTem:COMMunicate:LAN:ENAbled 1
    /// </remarks>
    public static IOutboundMessage<string> EnableNetworkLan => new ScpiMessage("SYSTem:COMMunicate:LAN:ENAbled 1");
        
    /// <summary>
    /// Creates a command message to apply the LAN configuration.
    /// </summary>
    /// <remarks>
    /// Command: SYSTem:COMMunicate:LAN:APPLY
    /// </remarks>
    public static IOutboundMessage<string> ApplyNetworkLan => new ScpiMessage("SYSTem:COMMunicate:LAN:APPLY");
    
    /// <summary>
    /// Creates a command message to save the LAN configuration. This will persist settings upon restart
    /// </summary>
    /// <remarks>
    /// Command: SYSTem:COMMunicate:LAN:SAVE
    /// </remarks>
    public static IOutboundMessage<string> SaveNetworkLan => new ScpiMessage("SYSTem:COMMunicate:LAN:SAVE");

    /// <summary>
    /// Creates a command message to set the LAN firmware update mode.
    /// </summary>
    /// <remarks>
    /// Command: SYSTem:COMMUnicate:LAN:FWUpdate
    /// </remarks>
    public static IOutboundMessage<string> SetLanFirmwareUpdateMode => new ScpiMessage("SYSTem:COMMUnicate:LAN:FWUpdate");

    /// <summary>
    /// Creates a command message to set the USB transparency mode (bypassing the SCPI consle layer).
    /// </summary>
    /// <param name="mode">The transparency mode (0 = disabled, 1 = enabled).</param>
    /// <remarks>
    /// Command: SYSTem:USB:SetTransparentMode mode
    /// </remarks>
    public static IOutboundMessage<string> SetUsbTransparencyMode(int mode)
    {
        return new ScpiMessage($"SYSTem:USB:SetTransparentMode {mode}");
    }

    /// <summary>
    /// Creates a query message to get LAN chip information including the WiFi module firmware version.
    /// </summary>
    /// <remarks>
    /// The device returns a JSON response:
    /// <c>{"ChipId":&lt;id&gt;,"FwVersion":"&lt;version&gt;","BuildDate":"&lt;date&gt;"}</c>
    /// Command: SYSTem:COMMunicate:LAN:GETChipInfo?
    /// Example: messageProducer.Send(ScpiMessageProducer.GetLanChipInfo);
    /// </remarks>
    public static IOutboundMessage<string> GetLanChipInfo => new ScpiMessage("SYSTem:COMMunicate:LAN:GETChipInfo?");
}