namespace Daqifi.Core.Firmware.Winc;

/// <summary>
/// The narrow serial surface the WINC bridge protocol needs: raw byte I/O plus a baud rate that can
/// change on an already-open port.
/// </summary>
/// <remarks>
/// Core's <c>SerialStreamTransport</c> deliberately does not fit here — it fixes baud at
/// construction and adds a liveness watchdog, whereas the bridge re-negotiates from 115200 to
/// 500000 partway through a session and must not be probed underneath. Keeping this interface
/// separate also makes the whole protocol layer testable without hardware.
/// </remarks>
internal interface IWincSerialPort : IDisposable
{
    /// <summary>Whether the port is currently open.</summary>
    bool IsOpen { get; }

    /// <summary>
    /// Current baud rate. Setting it on an open port re-negotiates the host side, and must only be
    /// done after the bridge has acknowledged a <see cref="WincBridgeProtocol.Command.Reconfigure"/>.
    /// </summary>
    int BaudRate { get; set; }

    /// <summary>Opens the port.</summary>
    void Open();

    /// <summary>Closes the port.</summary>
    void Close();

    /// <summary>Drops any buffered inbound bytes, so a read starts from a known-clean state.</summary>
    void DiscardInBuffer();

    /// <summary>Writes <paramref name="count"/> bytes from <paramref name="buffer"/> to the port.</summary>
    void Write(byte[] buffer, int offset, int count);

    /// <summary>
    /// Reads exactly <paramref name="count"/> bytes into <paramref name="buffer"/>, blocking until
    /// they arrive or <paramref name="timeout"/> elapses.
    /// </summary>
    /// <exception cref="TimeoutException">Fewer than <paramref name="count"/> bytes arrived in time.</exception>
    void ReadExactly(byte[] buffer, int offset, int count, TimeSpan timeout);
}
