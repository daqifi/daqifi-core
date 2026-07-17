using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;

namespace Daqifi.Core.Firmware;

/// <summary>
/// Transient SCPI-over-serial helper that kicks the device's WiFi module into
/// bridge mode during a firmware update.
/// </summary>
/// <remarks>
/// Used between the initial LAN-FW-update-mode handshake (sent over the regular
/// device transport) and the WINC flash tool's programming phase.
/// The flash tool needs exclusive access to the serial port, so this
/// helper opens a short-lived transport, sends the
/// <c>SYSTem:COMMUnicate:LAN:FWUpdate</c> / <c>SYSTem:COMMunicate:LAN:APPLY</c>
/// pair with the timing the firmware's WiFi-deinit/reinit state machine
/// expects, then closes the port so it can be handed back.
/// </remarks>
public static class WifiBridgeActivator
{
    // USB CDC virtual ports ignore the baud rate, but firmware checks DTR
    // (isCdcHostConnected) before accepting commands. Match SerialStreamTransport
    // defaults so port settings stay in one place.
    private static readonly TimeSpan DtrSettleDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan InterCommandDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ApplySettleDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Briefly opens <paramref name="portName"/>, sends the SCPI sequence that
    /// puts the device's WiFi module into bridge mode for firmware update, then
    /// closes the port.
    /// </summary>
    /// <param name="portName">The serial port name (e.g. <c>COM5</c>, <c>/dev/cu.usbmodem1</c>).</param>
    /// <param name="cancellationToken">Cancellation token observed between port-open, each command write, and each inter-command delay.</param>
    /// <exception cref="ArgumentNullException"><paramref name="portName"/> is null.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static void Activate(string portName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(portName);
        cancellationToken.ThrowIfCancellationRequested();

        using var transport = new SerialStreamTransport(portName);
        transport.Connect();

        // Allow firmware to recognise DTR before sending commands.
        WaitOrCancel(DtrSettleDelay, cancellationToken);

        // Re-assert the FW-update-requested flag (idempotent with the earlier handshake).
        WriteCommand(transport, ScpiMessageProducer.SetLanFirmwareUpdateMode);
        WaitOrCancel(InterCommandDelay, cancellationToken);

        // Trigger the WiFi manager REINIT to bridge-mode state machine.
        WriteCommand(transport, ScpiMessageProducer.ApplyNetworkLan);

        // Give firmware time to enqueue APPLY before the port closes.
        WaitOrCancel(ApplySettleDelay, cancellationToken);
    }

    /// <summary>
    /// Briefly opens <paramref name="portName"/>, sends the SCPI sequence that
    /// takes the device's WiFi module back out of bridge mode after a firmware
    /// update, then closes the port.
    /// </summary>
    /// <param name="portName">The serial port name (e.g. <c>COM5</c>, <c>/dev/cu.usbmodem1</c>).</param>
    /// <param name="cancellationToken">Cancellation token observed between port-open, each command write, and each inter-command delay.</param>
    /// <exception cref="ArgumentNullException"><paramref name="portName"/> is null.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static void Deactivate(string portName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(portName);
        cancellationToken.ThrowIfCancellationRequested();

        using var transport = new SerialStreamTransport(portName);
        transport.Connect();

        // Allow firmware to recognise DTR before sending commands.
        WaitOrCancel(DtrSettleDelay, cancellationToken);

        // Turn off transparent mode so the port goes back to the SCPI console.
        WriteCommand(transport, ScpiMessageProducer.SetUsbTransparencyMode(0));
        WaitOrCancel(InterCommandDelay, cancellationToken);

        // Trigger the WiFi manager REINIT out of bridge-mode state machine.
        WriteCommand(transport, ScpiMessageProducer.ApplyNetworkLan);

        // Give firmware time to enqueue APPLY before the port closes.
        WaitOrCancel(ApplySettleDelay, cancellationToken);
    }

    private static void WriteCommand(SerialStreamTransport transport, IOutboundMessage<string> message)
    {
        var bytes = message.GetBytes();
        var stream = transport.Stream;
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    private static void WaitOrCancel(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (cancellationToken.CanBeCanceled)
        {
            if (cancellationToken.WaitHandle.WaitOne(delay))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        else
        {
            Thread.Sleep(delay);
        }
    }
}
