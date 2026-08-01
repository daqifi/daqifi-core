using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Daqifi.Core.Firmware.Winc;

/// <summary>
/// Managed, cross-platform access to the WINC module over its UART serial bridge — no external
/// tool, so it works on Linux and macOS where Microchip's programmer does not run.
/// </summary>
/// <remarks>
/// <para>
/// <b>This inspects; it does not flash.</b> It implements the non-destructive half of the WINC
/// protocol — bridge handshake, baud negotiation, chip and flash identity, and flash
/// read-back/verify — and the name says so rather than hiding it behind a "flasher" that cannot
/// write.
/// </para>
/// <para>
/// The erase/program path is absent because it could not be validated. Programming a WINC means
/// erase and page-program cycles against the module's SPI flash, and a wrong opcode or address
/// bricks the module with no recovery path outside Microchip's Windows tool. Shipping an
/// unexercised write path would be worse than shipping none: it would look complete, callers would
/// select it, and the first time it ran would be on someone's hardware. The framing such a writer
/// needs (<see cref="WincSerialBridgeClient"/>) is here and tested, so that work starts from a
/// known-good base.
/// </para>
/// </remarks>
public sealed class WincModuleInspector
{
    /// <summary>Rate the bridge starts at before renegotiation.</summary>
    public const int InitialBaudRate = 115200;

    /// <summary>
    /// Rate the bridge is moved to for bulk transfer, matching Microchip's tool.
    /// </summary>
    /// <remarks>
    /// Over the DAQiFi device's USB-CDC link this is largely ceremonial — CDC ignores the line
    /// rate — but the exchange still has to happen: it is what the bridge expects, and completing
    /// it proves the command path works before any bulk transfer starts.
    /// </remarks>
    public const int FastBaudRate = 500000;

    private readonly Func<string, int, IWincSerialPort> _portFactory;
    private readonly ILogger _logger;
    private readonly TimeSpan _baudSettleDelay;
    private readonly TimeSpan _responseTimeout;

    /// <summary>
    /// Creates an inspector that opens real serial ports.
    /// </summary>
    public WincModuleInspector(ILogger<WincModuleInspector>? logger = null)
        : this((port, baud) => new SystemWincSerialPort(port, baud), logger)
    {
    }

    /// <summary>
    /// Test seam: supply the serial port implementation.
    /// </summary>
    internal WincModuleInspector(
        Func<string, int, IWincSerialPort> portFactory,
        ILogger? logger = null,
        TimeSpan? baudSettleDelay = null,
        TimeSpan? responseTimeout = null)
    {
        _portFactory = portFactory ?? throw new ArgumentNullException(nameof(portFactory));
        _logger = logger ?? NullLogger.Instance;
        _baudSettleDelay = baudSettleDelay ?? TimeSpan.FromMilliseconds(100);
        _responseTimeout = responseTimeout ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>
    /// Opens the bridge, handshakes, moves to <see cref="FastBaudRate"/>, and reports the module's
    /// chip and flash identity. Changes nothing on the device.
    /// </summary>
    public Task<WincModuleIdentity> ReadIdentityAsync(
        string portName,
        CancellationToken cancellationToken = default)
        => Task.Run(() => ReadIdentity(portName, cancellationToken), cancellationToken);

    /// <summary>
    /// Reads a span of the module's SPI flash. Changes nothing on the device.
    /// </summary>
    public Task<byte[]> ReadFlashAsync(
        string portName,
        uint offset,
        int length,
        CancellationToken cancellationToken = default)
        => Task.Run(() => ReadFlash(portName, offset, length, cancellationToken), cancellationToken);

    private WincModuleIdentity ReadIdentity(string portName, CancellationToken cancellationToken)
    {
        using var session = OpenSession(portName, cancellationToken);

        var chipId = session.Reader.ReadChipId();
        var flashId = session.Reader.ReadFlashJedecId();

        _logger.LogInformation(
            "WINC identity: chipId=0x{ChipId:X8} flashJedecId=0x{FlashId:X8} baud={Baud}.",
            chipId,
            flashId,
            session.Port.BaudRate);

        return new WincModuleIdentity
        {
            ChipId = chipId,
            FlashJedecId = flashId,
            IsRecognizedWinc = WincFlashReader.IsKnownWincChipId(chipId),
            NegotiatedBaudRate = session.Port.BaudRate
        };
    }

    private byte[] ReadFlash(string portName, uint offset, int length, CancellationToken cancellationToken)
    {
        using var session = OpenSession(portName, cancellationToken);
        return session.Reader.ReadFlash(offset, length, cancellationToken);
    }

    /// <summary>
    /// Opens the port, proves a bridge is listening, and negotiates up to the fast baud rate.
    /// </summary>
    private BridgeSession OpenSession(string portName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            throw new ArgumentException("Port name cannot be empty.", nameof(portName));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var port = _portFactory(portName, InitialBaudRate);
        try
        {
            port.Open();

            var bridge = new WincSerialBridgeClient(port, _responseTimeout, _logger);

            if (!bridge.TryHandshake())
            {
                throw new IOException(
                    $"No WINC serial bridge responded on {portName}. The device must be in WiFi " +
                    "firmware-update (bridge) mode before the module can be reached.");
            }

            bridge.ChangeBaudRate(FastBaudRate, _baudSettleDelay);

            // Re-handshake at the new rate: this both confirms the switch actually took and leaves
            // the bridge back in its op-code state before any command is issued.
            if (!bridge.TryHandshake())
            {
                throw new IOException(
                    $"The WINC bridge on {portName} stopped responding after switching to " +
                    $"{FastBaudRate} baud.");
            }

            return new BridgeSession(port, new WincFlashReader(bridge, logger: _logger));
        }
        catch
        {
            port.Dispose();
            throw;
        }
    }

    private sealed class BridgeSession(IWincSerialPort port, WincFlashReader reader) : IDisposable
    {
        internal IWincSerialPort Port { get; } = port;

        internal WincFlashReader Reader { get; } = reader;

        public void Dispose() => Port.Dispose();
    }
}
