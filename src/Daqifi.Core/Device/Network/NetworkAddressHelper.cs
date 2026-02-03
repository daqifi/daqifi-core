using Google.Protobuf;

namespace Daqifi.Core.Device.Network;

/// <summary>
/// Provides utility methods for decoding network address fields from <see cref="DaqifiOutMessage"/> protobuf messages.
/// </summary>
public static class NetworkAddressHelper
{
    /// <summary>
    /// Gets the IP address as a dotted-decimal string (e.g., "192.168.1.100") from the message's <see cref="DaqifiOutMessage.IpAddr"/> field.
    /// </summary>
    /// <param name="message">The protobuf message containing device information.</param>
    /// <returns>The formatted IP address string, or <see cref="string.Empty"/> if the field is empty.</returns>
    public static string GetIpAddressString(DaqifiOutMessage message)
    {
        return FormatAsIpAddress(message.IpAddr);
    }

    /// <summary>
    /// Gets the MAC address as a hyphen-separated hex string (e.g., "AA-BB-CC-DD-EE-FF") from the message's <see cref="DaqifiOutMessage.MacAddr"/> field.
    /// </summary>
    /// <param name="message">The protobuf message containing device information.</param>
    /// <returns>The formatted MAC address string, or <see cref="string.Empty"/> if the field is empty.</returns>
    public static string GetMacAddressString(DaqifiOutMessage message)
    {
        return message.MacAddr.Length > 0
            ? BitConverter.ToString(message.MacAddr.ToByteArray())
            : string.Empty;
    }

    /// <summary>
    /// Gets the subnet mask as a dotted-decimal string (e.g., "255.255.255.0") from the message's <see cref="DaqifiOutMessage.NetMask"/> field.
    /// </summary>
    /// <param name="message">The protobuf message containing device information.</param>
    /// <returns>The formatted subnet mask string, or <see cref="string.Empty"/> if the field is empty.</returns>
    public static string GetSubnetMaskString(DaqifiOutMessage message)
    {
        return FormatAsIpAddress(message.NetMask);
    }

    /// <summary>
    /// Gets the gateway address as a dotted-decimal string (e.g., "192.168.1.1") from the message's <see cref="DaqifiOutMessage.Gateway"/> field.
    /// </summary>
    /// <param name="message">The protobuf message containing device information.</param>
    /// <returns>The formatted gateway address string, or <see cref="string.Empty"/> if the field is empty.</returns>
    public static string GetGatewayString(DaqifiOutMessage message)
    {
        return FormatAsIpAddress(message.Gateway);
    }

    /// <summary>
    /// Gets the primary DNS address as a dotted-decimal string (e.g., "8.8.8.8") from the message's <see cref="DaqifiOutMessage.PrimaryDns"/> field.
    /// </summary>
    /// <param name="message">The protobuf message containing device information.</param>
    /// <returns>The formatted primary DNS address string, or <see cref="string.Empty"/> if the field is empty.</returns>
    public static string GetPrimaryDnsString(DaqifiOutMessage message)
    {
        return FormatAsIpAddress(message.PrimaryDns);
    }

    /// <summary>
    /// Gets the secondary DNS address as a dotted-decimal string (e.g., "1.1.1.1") from the message's <see cref="DaqifiOutMessage.SecondaryDns"/> field.
    /// </summary>
    /// <param name="message">The protobuf message containing device information.</param>
    /// <returns>The formatted secondary DNS address string, or <see cref="string.Empty"/> if the field is empty.</returns>
    public static string GetSecondaryDnsString(DaqifiOutMessage message)
    {
        return FormatAsIpAddress(message.SecondaryDns);
    }

    /// <summary>
    /// Formats a <see cref="ByteString"/> as a dotted-decimal IP address string.
    /// </summary>
    private static string FormatAsIpAddress(ByteString data)
    {
        return data.Length > 0
            ? string.Join(".", data.ToByteArray())
            : string.Empty;
    }
}
