using System.IO.Ports;

namespace Daqifi.Core.Firmware.Winc;

/// <summary>
/// <see cref="IWincSerialPort"/> over <see cref="SerialPort"/>. Cross-platform: this is what makes a
/// native WINC flash possible on Linux and macOS, where Microchip's tool does not run.
/// </summary>
internal sealed class SystemWincSerialPort : IWincSerialPort
{
    private readonly SerialPort _port;
    private bool _disposed;

    internal SystemWincSerialPort(string portName, int baudRate = 115200)
    {
        _port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            // USB CDC delivers nothing to the host without DTR asserted — the DAQiFi firmware
            // gates its writes on the host being present.
            DtrEnable = true,
            RtsEnable = false,
            ReadTimeout = 2000,
            WriteTimeout = 2000
        };
    }

    public bool IsOpen => _port.IsOpen;

    public int BaudRate
    {
        get => _port.BaudRate;
        set => _port.BaudRate = value;
    }

    public void Open()
    {
        if (!_port.IsOpen)
        {
            _port.Open();
        }
    }

    public void Close()
    {
        if (_port.IsOpen)
        {
            _port.Close();
        }
    }

    public void DiscardInBuffer()
    {
        if (_port.IsOpen)
        {
            _port.DiscardInBuffer();
        }
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        _port.Write(buffer, offset, count);
    }

    public void ReadExactly(byte[] buffer, int offset, int count, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var read = 0;

        while (read < count)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException(
                    $"Timed out reading from {_port.PortName}: wanted {count} bytes, got {read}.");
            }

            // SerialPort.Read returns as soon as *any* bytes are available, so loop until the full
            // frame has arrived rather than assuming one read yields everything.
            _port.ReadTimeout = Math.Max(1, (int)remaining.TotalMilliseconds);

            int chunk;
            try
            {
                chunk = _port.Read(buffer, offset + read, count - read);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"Timed out reading from {_port.PortName}: wanted {count} bytes, got {read}.");
            }

            if (chunk <= 0)
            {
                throw new IOException($"Serial port {_port.PortName} returned no data before closing.");
            }

            read += chunk;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            Close();
        }
        catch (Exception)
        {
            // Disposal must not throw; a port that is already gone is not an error worth surfacing.
        }

        _port.Dispose();
    }
}
