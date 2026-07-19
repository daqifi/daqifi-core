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
    /// Test seam: injects the underlying <see cref="SerialPort"/> so the closed-port
    /// stream-access path (issue #238) can be exercised without real hardware — e.g. by
    /// passing a constructed-but-unopened port whose <see cref="SerialPort.IsOpen"/> is
    /// <c>false</c>. The transport takes ownership: any previously held port is disposed when
    /// replaced or cleared, and the current port is disposed on <see cref="Dispose"/>. Never
    /// used in production.
    /// </summary>
    /// <param name="serialPort">The serial port to use, or <c>null</c> to clear it.</param>
    internal void SetSerialPortForTesting(SerialPort? serialPort)
    {
        if (ReferenceEquals(_serialPort, serialPort))
        {
            return;
        }

        _serialPort?.Dispose();
        _serialPort = serialPort;
    }

    /// <summary>
    /// Gets the underlying stream for read/write operations.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown when the transport has been disposed.</exception>
    /// <exception cref="TransportNotConnectedException">
    /// Thrown when the serial port is not open — either never connected, or closed mid-operation
    /// by a device unplug or a DTR-triggered MCU reset that re-enumerated the COM port.
    /// </exception>
    public Stream Stream
    {
        get
        {
            ThrowIfDisposed();

            // Capture the field into a local so the check-then-access below is stable: a
            // concurrent Disconnect()/Dispose() nulls _serialPort in a finally with no
            // synchronization, which would otherwise turn the BaseStream dereference into a
            // NullReferenceException instead of the intended typed signal.
            var port = _serialPort;

            // Guard on IsOpen (not just non-null) before touching BaseStream. When the port is
            // non-null but closed — the device was unplugged, or a DTR-triggered MCU reset
            // re-enumerated the COM port mid-connect — SerialPort.BaseStream's getter itself
            // throws a raw InvalidOperationException ("The BaseStream is only available when the
            // port is open.") that reads like an app bug. Surface a typed, transport-state
            // exception instead so consumers can classify a dropped transport as a transient,
            // environmental condition (issue #238; serial analog of #237).
            if (port?.IsOpen != true)
            {
                throw new TransportNotConnectedException(
                    $"Serial transport is not connected ({_portName}).");
            }

            try
            {
                return port.BaseStream;
            }
            catch (InvalidOperationException ex)
            {
                // The port can close between the IsOpen check above and this getter — the exact
                // unplug / DTR-reset race #238 is about. SerialPort.BaseStream's only
                // InvalidOperationException is the "only available when the port is open" case,
                // so translate it to the typed signal (preserving the original as InnerException)
                // rather than leaking the raw framework message.
                throw new TransportNotConnectedException(
                    $"Serial transport is not connected ({_portName}).", ex);
            }
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

        await ConnectRetryExecutor.ExecuteAsync(
            retryOptions,
            connectAttempt: options =>
            {
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

                return Task.CompletedTask;
            },
            onAttemptFailed: () =>
            {
                _serialPort?.Dispose();
                _serialPort = null;
            },
            onStatusChanged: OnStatusChanged);
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