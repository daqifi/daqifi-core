using System.Text;

namespace Daqifi.Core.Communication.Messages;

/// <summary>
/// Represents a Standard Commands for Programmable Instruments (SCPI) message
/// to be sent to the device (Outbound).
/// Implements IOutboundMessage.
/// </summary>
/// <param name="command">The SCPI command string.</param>
public class ScpiMessage(string command) : IOutboundMessage<string>
{
    /// <summary>
    /// Gets or sets the data associated with the message, which is the SCPI command string.
    /// </summary>
    public string Data { get; set; } = command;

    /// <summary>
    /// Converts the SCPI command string to a byte array suitable for transmission,
    /// appending the required carriage return and newline characters.
    /// </summary>
    /// <returns>A byte array representing the SCPI message.</returns>
    public byte[] GetBytes()
    {
        return Encoding.ASCII.GetBytes(Data + "\r\n");
    }
}
