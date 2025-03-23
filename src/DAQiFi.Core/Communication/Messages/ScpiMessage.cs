using System.Text;

namespace DAQiFi.Core.Communication.Messages;

public class ScpiMessage(string command) : IMessage
{
    public object Data { get; set; } = command;

    public byte[] GetBytes()
    {
        return Encoding.ASCII.GetBytes((string)Data + "\r\n");
    }
}
