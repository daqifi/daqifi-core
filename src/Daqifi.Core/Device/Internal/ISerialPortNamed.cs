namespace Daqifi.Core.Device.Internal;

/// <summary>
/// A device that can name the operating-system serial port it talks through.
/// </summary>
/// <remarks>
/// <para>
/// This exists for the one thing a display name cannot do: hand an external program a port it
/// can open. The WiFi module flash tool is launched with <c>/p &lt;port&gt;</c>, and that
/// argument has to be a real port name such as <c>COM7</c> or <c>/dev/cu.usbmodem1101</c>.
/// <see cref="IDevice.Name"/> is a label — its default is <c>"DAQiFi Device"</c> — so reading
/// the port off it handed the tool a label and the tool failed to open the port (issue #842).
/// </para>
/// <para>
/// <see cref="DaqifiDevice"/> implements this explicitly, so it widens nothing on the public
/// API; that is the same arrangement <see cref="ITextExchangeHost"/> and
/// <see cref="IDeviceOperationHost"/> already use. A device on any other transport answers
/// <c>null</c>, which is a real answer: there is no serial port, and the caller must say so
/// rather than guess.
/// </para>
/// </remarks>
internal interface ISerialPortNamed
{
    /// <summary>
    /// The serial port this device is connected through, or <c>null</c> when it is not on a
    /// serial transport.
    /// </summary>
    string? SerialPortName { get; }
}
