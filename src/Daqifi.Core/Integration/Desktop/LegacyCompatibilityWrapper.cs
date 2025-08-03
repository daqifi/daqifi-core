using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;

namespace Daqifi.Core.Integration.Desktop;

/// <summary>
/// Provides backward compatibility for applications that expect string-based message events.
/// This wrapper converts object-based messages back to string format for legacy code.
/// </summary>
public class LegacyMessageEventArgs : EventArgs
{
    public LegacyMessageEventArgs(string message)
    {
        Message = message;
    }

    public string Message { get; }
}

/// <summary>
/// Wrapper that provides backward-compatible string-based events from object-based CoreDeviceAdapter.
/// Use this for gradual migration of existing desktop applications.
/// </summary>
public class LegacyCoreDeviceAdapter : IDisposable
{
    private readonly CoreDeviceAdapter _coreAdapter;
    private bool _disposed;

    /// <summary>
    /// Initializes a new LegacyCoreDeviceAdapter with backward-compatible string events.
    /// </summary>
    /// <param name="coreAdapter">The modern CoreDeviceAdapter to wrap.</param>
    public LegacyCoreDeviceAdapter(CoreDeviceAdapter coreAdapter)
    {
        _coreAdapter = coreAdapter ?? throw new ArgumentNullException(nameof(coreAdapter));
        _coreAdapter.MessageReceived += OnCoreMessageReceived;
    }

    /// <summary>
    /// Legacy event that fires with string messages (backward compatible).
    /// </summary>
    public event EventHandler<LegacyMessageEventArgs>? MessageReceived;

    /// <summary>
    /// Pass-through properties from the underlying adapter.
    /// </summary>
    public bool IsConnected => _coreAdapter.IsConnected;
    public string ConnectionInfo => _coreAdapter.ConnectionInfo;

    /// <summary>
    /// Pass-through methods from the underlying adapter.
    /// </summary>
    public bool Connect() => _coreAdapter.Connect();
    public async Task<bool> ConnectAsync() => await _coreAdapter.ConnectAsync();
    public bool Disconnect() => _coreAdapter.Disconnect();
    public async Task<bool> DisconnectAsync() => await _coreAdapter.DisconnectAsync();
    public bool Write(string command) => _coreAdapter.Write(command);

    /// <summary>
    /// Creates a legacy-compatible TCP adapter for existing desktop applications.
    /// </summary>
    /// <param name="host">The hostname or IP address.</param>
    /// <param name="port">The port number.</param>
    /// <returns>A legacy-compatible adapter.</returns>
    public static LegacyCoreDeviceAdapter CreateTcpAdapter(string host, int port)
    {
        var coreAdapter = CoreDeviceAdapter.CreateTcpAdapter(host, port);
        return new LegacyCoreDeviceAdapter(coreAdapter);
    }

    /// <summary>
    /// Creates a legacy-compatible Serial adapter for existing desktop applications.
    /// </summary>
    /// <param name="portName">The serial port name.</param>
    /// <param name="baudRate">The baud rate.</param>
    /// <returns>A legacy-compatible adapter.</returns>
    public static LegacyCoreDeviceAdapter CreateSerialAdapter(string portName, int baudRate = 115200)
    {
        var coreAdapter = CoreDeviceAdapter.CreateSerialAdapter(portName, baudRate);
        return new LegacyCoreDeviceAdapter(coreAdapter);
    }

    private void OnCoreMessageReceived(object? sender, MessageReceivedEventArgs<object> e)
    {
        // Convert object messages to string format for backward compatibility
        string messageText = e.Message.Data switch
        {
            string textMsg => textMsg,
            DaqifiOutMessage protobufMsg => $"[Protobuf Message: {protobufMsg.GetType().Name}]",
            _ => e.Message.Data?.ToString() ?? ""
        };

        MessageReceived?.Invoke(this, new LegacyMessageEventArgs(messageText));
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _coreAdapter.MessageReceived -= OnCoreMessageReceived;
            _coreAdapter?.Dispose();
            _disposed = true;
        }
    }
}