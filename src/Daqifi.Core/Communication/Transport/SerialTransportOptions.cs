namespace Daqifi.Core.Communication.Transport;

/// <summary>
/// How a <see cref="SerialStreamTransport"/> opens its port and watches it afterwards.
/// </summary>
/// <remarks>
/// There is nothing here for parity, data bits or stop bits: the device's port is a USB CDC one,
/// which has no line framing for them to set.
/// </remarks>
public sealed class SerialTransportOptions
{
    private readonly TimeSpan _livenessCheckInterval = SerialStreamTransport.DefaultLivenessCheckInterval;

    /// <summary>
    /// Gets the baud rate the port is opened at. Default is 9600.
    /// </summary>
    public int BaudRate { get; init; } = 9600;

    /// <summary>
    /// Gets whether the Data Terminal Ready (DTR) signal is raised. Default is <see langword="true"/>.
    /// </summary>
    public bool EnableDtr { get; init; } = true;

    /// <summary>
    /// Gets whether the Request To Send (RTS) signal is raised. Default is <see langword="false"/>.
    /// </summary>
    public bool EnableRts { get; init; }

    /// <summary>
    /// Gets how often an established connection re-checks that its port is still present. Default is
    /// <see cref="SerialStreamTransport.DefaultLivenessCheckInterval"/>; <see cref="TimeSpan.Zero"/>
    /// disables the check and relies on I/O fault escalation alone.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is negative.</exception>
    public TimeSpan LivenessCheckInterval
    {
        get => _livenessCheckInterval;
        init
        {
            if (value < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    "Liveness check interval cannot be negative. Use TimeSpan.Zero to disable the check.");
            }

            _livenessCheckInterval = value;
        }
    }
}
