namespace Daqifi.Core.Communication.Messages;

/// <summary>
/// Represents a message containing data received from the DAQiFi device (Inbound).
/// </summary>
/// <typeparam name="T">The type of the data payload.</typeparam>
public interface IInboundMessage<out T> // Using 'out' for covariance
{
    /// <summary>
    /// Gets the payload data received from the device.
    /// </summary>
    T Data { get; }
} 