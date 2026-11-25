using Daqifi.Core.Communication.Messages;

namespace Daqifi.Core.Device;

/// <summary>
/// Provides data for <see cref="IDevice.MessageReceived"/>,
/// <see cref="DaqifiDevice.StatusMessageReceived"/> and <see cref="DaqifiDevice.StreamMessageReceived"/>.
/// </summary>
public sealed class MessageReceivedEventArgs : EventArgs
{
    /// <summary>
    /// Gets the message received from the device.
    /// </summary>
    public DaqifiOutMessage Message { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="MessageReceivedEventArgs"/> class.
    /// </summary>
    /// <param name="message">The message received from the device.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="message"/> is null.</exception>
    public MessageReceivedEventArgs(DaqifiOutMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Message = message;
    }
}
