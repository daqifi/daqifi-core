namespace Daqifi.Core.Communication.Transport;

/// <summary>
/// Provides data for transport status change events.
/// </summary>
public class TransportStatusEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the TransportStatusEventArgs class.
    /// </summary>
    /// <param name="isConnected">The current connection status.</param>
    /// <param name="connectionInfo">Information about the connection.</param>
    /// <param name="error">Any error that occurred during status change, if applicable.</param>
    public TransportStatusEventArgs(bool isConnected, string connectionInfo, Exception? error = null)
    {
        IsConnected = isConnected;
        ConnectionInfo = connectionInfo;
        Error = error;
    }

    /// <summary>
    /// Gets a value indicating whether the transport is connected.
    /// </summary>
    public bool IsConnected { get; }

    /// <summary>
    /// Gets information about the transport connection.
    /// </summary>
    public string ConnectionInfo { get; }

    /// <summary>
    /// Gets any error that occurred during the status change, if applicable.
    /// </summary>
    public Exception? Error { get; }
}