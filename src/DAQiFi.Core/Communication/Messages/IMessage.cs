namespace DAQiFi.Core.Communication.Messages;

public interface IMessage
{
    object Data { get; set;}

    byte[] GetBytes();
}
