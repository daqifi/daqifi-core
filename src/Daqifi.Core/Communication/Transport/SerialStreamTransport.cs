using System.IO.Ports;

namespace Daqifi.Core.Communication.Transport;

/// <summary>
/// Serial port implementation of IStreamTransport that provides stream-based communication
/// over serial connections. Handles connection lifecycle and provides the underlying
/// SerialPort BaseStream for message producers and consumers.
/// </summary>
public class SerialStreamTransport : IStreamTransport
{
    private readonly string _portName;
    private readonly int _baudRate;
    private readonly Parity _parity;
    private readonly int _dataBits;
    private readonly StopBits _stopBits;
    private SerialPort? _serialPort;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the SerialStreamTransport class.
    /// </summary>
    /// <param name="portName">The name of the serial port (e.g., "COM1", "/dev/ttyUSB0").</param>
    /// <param name="baudRate">The baud rate for the connection.</param>
    /// <param name="parity">The parity setting.</param>
    /// <param name="dataBits">The number of data bits.</param>
    /// <param name="stopBits">The stop bits setting.</param>
    public SerialStreamTransport(string portName, int baudRate = 115200, Parity parity = Parity.None, 
        int dataBits = 8, StopBits stopBits = StopBits.One)
    {
        _portName = portName ?? throw new ArgumentNullException(nameof(portName));
        _baudRate = baudRate;
        _parity = parity;
        _dataBits = dataBits;
        _stopBits = stopBits;
    }

    /// <summary>
    /// Gets the underlying stream for read/write operations.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when not connected.</exception>
    public Stream Stream
    {
        get
        {
            ThrowIfDisposed();
            return _serialPort?.BaseStream ?? throw new InvalidOperationException("Transport is not connected.");
        }
    }

    /// <summary>
    /// Gets a value indicating whether the transport is connected.
    /// </summary>
    public bool IsConnected => _serialPort?.IsOpen == true;

    /// <summary>
    /// Gets information about the transport connection.
    /// </summary>
    public string ConnectionInfo
    {
        get
        {
            if (!IsConnected)
                return $"Serial: Disconnected ({_portName})";

            return $"Serial: {_portName} @ {_baudRate} baud";
        }
    }

    /// <summary>
    /// Occurs when the connection status changes.
    /// </summary>
    public event EventHandler<TransportStatusEventArgs>? StatusChanged;

    /// <summary>
    /// Establishes the serial connection asynchronously.
    /// </summary>
    /// <returns>A task representing the asynchronous connect operation.</returns>
    public async Task ConnectAsync()
    {
        ThrowIfDisposed();

        if (IsConnected)
            return;

        try
        {
            _serialPort = new SerialPort(_portName, _baudRate, _parity, _dataBits, _stopBits)
            {
                ReadTimeout = 5000,
                WriteTimeout = 5000,
                DtrEnable = true,  // Enable DTR as desktop does
                RtsEnable = false
            };

            _serialPort.Open();
            OnStatusChanged(true, null);
        }
        catch (Exception ex)
        {
            _serialPort?.Dispose();
            _serialPort = null;
            OnStatusChanged(false, ex);
            throw;
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Closes the serial connection asynchronously.
    /// </summary>
    /// <returns>A task representing the asynchronous disconnect operation.</returns>
    public async Task DisconnectAsync()
    {
        if (!IsConnected)
            return;

        try
        {
            _serialPort?.Close();
        }
        catch (Exception ex)
        {
            OnStatusChanged(false, ex);
            throw;
        }
        finally
        {
            _serialPort?.Dispose();
            _serialPort = null;
            OnStatusChanged(false, null);
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Establishes the serial connection synchronously.
    /// </summary>
    public void Connect()
    {
        ConnectAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Closes the serial connection synchronously.
    /// </summary>
    public void Disconnect()
    {
        DisconnectAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Gets the available serial port names on the system.
    /// </summary>
    /// <returns>An array of available port names.</returns>
    public static string[] GetAvailablePortNames()
    {
        return SerialPort.GetPortNames();
    }

    /// <summary>
    /// Raises the StatusChanged event.
    /// </summary>
    /// <param name="isConnected">The current connection status.</param>
    /// <param name="error">Any error that occurred, if applicable.</param>
    protected virtual void OnStatusChanged(bool isConnected, Exception? error)
    {
        StatusChanged?.Invoke(this, new TransportStatusEventArgs(isConnected, ConnectionInfo, error));
    }

    /// <summary>
    /// Throws ObjectDisposedException if this instance has been disposed.
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SerialStreamTransport));
    }

    /// <summary>
    /// Disposes the transport and releases resources.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            try
            {
                Disconnect();
            }
            catch
            {
                // Ignore errors during disposal
            }

            _serialPort?.Dispose();
            _disposed = true;
        }
    }
}