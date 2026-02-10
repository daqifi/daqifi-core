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
    private readonly bool _enableDtr;
    private readonly bool _enableRts;
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
    /// <param name="enableDtr">Whether to enable Data Terminal Ready (DTR) signal. Default is true.</param>
    /// <param name="enableRts">Whether to enable Request To Send (RTS) signal. Default is false.</param>
    public SerialStreamTransport(string portName, int baudRate = 9600, Parity parity = Parity.None,
        int dataBits = 8, StopBits stopBits = StopBits.One, bool enableDtr = true, bool enableRts = false)
    {
        _portName = portName ?? throw new ArgumentNullException(nameof(portName));
        _baudRate = baudRate;
        _parity = parity;
        _dataBits = dataBits;
        _stopBits = stopBits;
        _enableDtr = enableDtr;
        _enableRts = enableRts;
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
        await ConnectAsync(null);
    }

    /// <summary>
    /// Establishes the serial connection asynchronously with retry support.
    /// </summary>
    /// <param name="retryOptions">Configuration for retry behavior. If null, uses default single attempt.</param>
    /// <returns>A task representing the asynchronous connect operation.</returns>
    public async Task ConnectAsync(ConnectionRetryOptions? retryOptions)
    {
        ThrowIfDisposed();

        if (IsConnected)
            return;

        var options = retryOptions ?? ConnectionRetryOptions.NoRetry;
        var maxAttempts = options.Enabled ? options.MaxAttempts : 1;
        Exception? lastException = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                // Calculate delay for this attempt
                if (attempt > 1)
                {
                    var delay = options.CalculateDelay(attempt);
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay);
                    }
                }

                var timeout = (int)options.ConnectionTimeout.TotalMilliseconds;
                _serialPort = new SerialPort(_portName, _baudRate, _parity, _dataBits, _stopBits)
                {
                    ReadTimeout = timeout,
                    WriteTimeout = timeout,
                    DtrEnable = _enableDtr,
                    RtsEnable = _enableRts
                };

                _serialPort.Open();

                // After a successful open, lower the ReadTimeout to a short operational
                // value. The connection timeout is only needed for retry/backoff logic,
                // not for blocking reads during normal operation. A short ReadTimeout
                // ensures consumer threads can be stopped promptly (StopSafely).
                _serialPort.ReadTimeout = 500;

                OnStatusChanged(true, null);
                return; // Success!
            }
            catch (Exception ex)
            {
                lastException = ex;
                _serialPort?.Dispose();
                _serialPort = null;

                // If this is not the last attempt and retry is enabled, continue
                if (attempt < maxAttempts && options.Enabled)
                {
                    OnStatusChanged(false, new Exception($"Connection attempt {attempt}/{maxAttempts} failed, retrying...", ex));
                    continue;
                }

                // Last attempt failed or retry disabled
                OnStatusChanged(false, ex);
                throw;
            }
        }

        // Should not reach here, but just in case
        OnStatusChanged(false, lastException);
        throw lastException ?? new InvalidOperationException("Connection failed after all retry attempts.");
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