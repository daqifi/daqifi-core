using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Daqifi.Core.Firmware.Winc;

/// <summary>
/// Speaks the WINC serial bridge protocol over an <see cref="IWincSerialPort"/>: identify, register
/// read/write, block read/write and the baud re-negotiation. Every method is one complete bridge
/// exchange; sequencing and the flash-level meaning of those registers belong to callers.
/// </summary>
/// <remarks>
/// See <see cref="WincBridgeProtocol"/> for the wire format. This type does no retrying — a bridge
/// exchange that fails has usually left the bridge state machine mid-command, so recovery is a
/// caller-level decision, not a blind repeat.
/// </remarks>
internal sealed class WincSerialBridgeClient
{
    private readonly IWincSerialPort _port;
    private readonly ILogger _logger;
    private readonly TimeSpan _responseTimeout;

    internal WincSerialBridgeClient(
        IWincSerialPort port,
        TimeSpan? responseTimeout = null,
        ILogger? logger = null)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _responseTimeout = responseTimeout ?? TimeSpan.FromSeconds(2);
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Sends the identify op code and checks for the variable-baud bridge reply. This is the
    /// handshake that proves something is actually bridging on the other end of the port.
    /// </summary>
    /// <returns>True when the bridge answered with the expected identify byte.</returns>
    internal bool TryHandshake()
    {
        _port.DiscardInBuffer();
        _port.Write([WincBridgeProtocol.IdentifyVariableBaud], 0, 1);

        var response = new byte[1];
        try
        {
            _port.ReadExactly(response, 0, 1, _responseTimeout);
        }
        catch (TimeoutException)
        {
            _logger.LogDebug("WINC bridge handshake timed out; no identify response.");
            return false;
        }

        if (response[0] == WincBridgeProtocol.Response.IdVariableBaud)
        {
            return true;
        }

        _logger.LogDebug(
            "WINC bridge handshake returned 0x{Actual:X2}; expected 0x{Expected:X2}.",
            response[0],
            WincBridgeProtocol.Response.IdVariableBaud);
        return false;
    }

    /// <summary>Reads a 32-bit WINC register.</summary>
    internal uint ReadRegister(uint address)
    {
        SendCommand(WincBridgeProtocol.Command.ReadRegisterWithReturn, 0, address, 0);

        var response = new byte[4];
        _port.ReadExactly(response, 0, 4, _responseTimeout);
        return WincBridgeProtocol.DecodeRegisterValue(response);
    }

    /// <summary>Writes a 32-bit WINC register.</summary>
    internal void WriteRegister(uint address, uint value)
        => SendCommand(WincBridgeProtocol.Command.WriteRegister, 0, address, value);

    /// <summary>
    /// Reads a block of WINC memory. Callers must chunk to
    /// <see cref="WincBridgeProtocol.MaxReadBlockSize"/>; a larger request wedges the bridge.
    /// </summary>
    internal byte[] ReadBlock(uint address, int size)
    {
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "Block read size must be positive.");
        }

        if (size > WincBridgeProtocol.MaxReadBlockSize)
        {
            // Not a style preference: at or above the firmware's 2048-byte buffer the bridge's read
            // loop never terminates, so this would hang rather than fail.
            throw new ArgumentOutOfRangeException(
                nameof(size),
                size,
                $"Block reads must be at most {WincBridgeProtocol.MaxReadBlockSize} bytes; larger requests " +
                "do not terminate in the device's bridge read loop.");
        }

        SendCommand(WincBridgeProtocol.Command.ReadBlock, (ushort)size, address, 0);

        var payload = new byte[size];
        _port.ReadExactly(payload, 0, size, _responseTimeout);
        return payload;
    }

    /// <summary>
    /// Writes a block of WINC memory: header, ACK, payload, then a final ACK/NACK verdict.
    /// </summary>
    internal void WriteBlock(uint address, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length == 0)
        {
            throw new ArgumentException("Block write payload cannot be empty.", nameof(data));
        }

        if (data.Length > WincBridgeProtocol.MaxWriteBlockSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(data),
                data.Length,
                $"Block writes must be at most {WincBridgeProtocol.MaxWriteBlockSize} bytes.");
        }

        SendCommand(WincBridgeProtocol.Command.WriteBlock, (ushort)data.Length, address, 0);

        _port.Write(data, 0, data.Length);

        var verdict = new byte[1];
        _port.ReadExactly(verdict, 0, 1, _responseTimeout);

        if (verdict[0] != WincBridgeProtocol.Response.Ack)
        {
            throw new IOException(
                $"WINC bridge rejected a {data.Length}-byte block write at 0x{address:X8} " +
                $"(responded 0x{verdict[0]:X2}).");
        }
    }

    /// <summary>
    /// Re-negotiates the link speed: tells the bridge to switch, then moves the host side to match.
    /// </summary>
    /// <remarks>
    /// Order matters. The bridge changes its own rate as soon as it processes the command, so the
    /// host must follow immediately; anything sent in between is lost. The settle delay gives the
    /// device's UART time to reconfigure before the next byte arrives.
    /// </remarks>
    internal void ChangeBaudRate(
        int newBaudRate,
        TimeSpan settleDelay,
        CancellationToken cancellationToken = default)
    {
        if (newBaudRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(newBaudRate), newBaudRate, "Baud rate must be positive.");
        }

        SendCommand(WincBridgeProtocol.Command.Reconfigure, 0, 0, (uint)newBaudRate);

        _port.BaudRate = newBaudRate;

        if (settleDelay > TimeSpan.Zero)
        {
            // Cancellable rather than Thread.Sleep so a cancel during the settle window is
            // observed instead of being slept through.
            cancellationToken.WaitHandle.WaitOne(settleDelay);
            cancellationToken.ThrowIfCancellationRequested();
        }

        _port.DiscardInBuffer();
        _logger.LogDebug("WINC bridge baud rate changed to {BaudRate}.", newBaudRate);
    }

    /// <summary>
    /// Writes the start byte and header, then consumes the bridge's ACK/NACK verdict on the header.
    /// </summary>
    private void SendCommand(WincBridgeProtocol.Command command, ushort size, uint address, uint value)
    {
        var header = WincBridgeProtocol.BuildHeader(command, size, address, value);

        _port.Write([WincBridgeProtocol.StartCommand], 0, 1);
        _port.Write(header, 0, header.Length);

        var verdict = new byte[1];
        _port.ReadExactly(verdict, 0, 1, _responseTimeout);

        if (verdict[0] == WincBridgeProtocol.Response.Ack)
        {
            return;
        }

        // A NACK means the bridge rejected our checksum, so it has already returned to its
        // op-code state; anything else means we are out of sync with its state machine.
        var reason = verdict[0] == WincBridgeProtocol.Response.Nack
            ? "rejected the command header checksum"
            : $"returned an unexpected byte 0x{verdict[0]:X2} instead of an ACK";

        throw new IOException($"WINC bridge {reason} for command {command} at 0x{address:X8}.");
    }
}
