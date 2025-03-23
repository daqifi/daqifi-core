using Daqifi.Core.Communication.Messages;

namespace Daqifi.Core.Communication.Producers;

public class ScpiMessageProducer
{
    public static IMessage Reboot => new ScpiMessage("SYSTem:REboot");
    public static IMessage SystemInfo => new ScpiMessage("SYSTem:SYSInfoPB?");
    public static IMessage TurnOffEcho => new ScpiMessage("SYSTem:ECHO -1");
    public static IMessage TurnOnEcho => new ScpiMessage("SYSTem:ECHO 1");
}