using System;
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
    /// Creates a query message to get the current SD card logging state.
    /// </summary>
    /// <remarks>
    /// Command: SYSTem:STORage:SD:LOGging?
    /// Example: messageProducer.Send(ScpiMessageProducer.GetSdLoggingState);
    /// </remarks>
    public static IOutboundMessage<string> GetSdLoggingState => new ScpiMessage("SYSTem:STORage:SD:LOGging?");

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
    /// <param name="fileName">The name of the file to retrieve. Must be enclosed in quotes.</param>
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
    /// <param name="fileName">The name of the file to create or append to. Must be enclosed in quotes.</param>
    /// <remarks>
    /// The specified file will be created if it doesn't exist, or appended to if it already exists.
    /// Command: SYSTem:STORage:SD:LOGging "filename.bin"
    /// Example: messageProducer.Send(ScpiMessageProducer.SetSdLoggingFileName("data.bin"));
    /// </remarks>
    public static IOutboundMessage<string> SetSdLoggingFileName(string fileName)
    {
        return new ScpiMessage($"SYSTem:STORage:SD:LOGging \"{fileName}\"");
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
    /// Creates a command message to enable ADC channels using a binary string.
    /// </summary>
    /// <param name="channelSetString">A binary string where each character represents a channel (0 = disabled, 1 = enabled), right-to-left. For example, "0001010100" enables channels 2, 4, and 6.</param>
    /// <remarks>
    /// The binary string is read from right to left, where position 0 is the rightmost bit:
    /// - Position 0: Channel 0
    /// - Position 1: Channel 1
    /// - Position 2: Channel 2
    /// etc.
    /// 
    /// Command: ENAble:VOLTage:DC binaryString
    /// Example: 
    /// <code>
    /// // Enable channels 2, 4, and 6
    /// messageProducer.Send(ScpiMessageProducer.EnableAdcChannels("0001010100"));
    /// 
    /// // Enable channels 0 and 1
    /// messageProducer.Send(ScpiMessageProducer.EnableAdcChannels("0000000011"));
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
}