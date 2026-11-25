using System;
using System.Collections.Generic;

namespace Daqifi.Core.Device;

/// <summary>
/// The common base of the exceptions that report that the device refused an operation, failed at
/// it, or cannot do it, so one <c>catch</c> covers all of them.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RawDeviceResponse"/> holds what the device said about the failure. Catch a derived
/// type instead where the difference matters, for example
/// <see cref="FeatureNotSupportedException"/> to ask for a firmware update.
/// </para>
/// <para>
/// A few failures keep a framework base class, so that existing handlers for it still match, and
/// are therefore not caught here: <see cref="DeviceNotConnectedException"/>
/// (<see cref="InvalidOperationException"/>), <see cref="ChannelPopulationTimeoutException"/>
/// (<see cref="TimeoutException"/>) and
/// <see cref="Communication.Transport.SerialPortConnectException"/>
/// (<see cref="System.IO.IOException"/>). They are about the connection rather than an answer from
/// the device. <see cref="Firmware.FirmwareUpdateException"/> is separate too: it covers the whole
/// update, including steps that never talk to the device.
/// </para>
/// </remarks>
public abstract class DaqifiDeviceException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DaqifiDeviceException"/> class.
    /// </summary>
    /// <param name="message">The message that describes the failure.</param>
    /// <param name="rawDeviceResponse">
    /// The lines the device sent about the failure, or <c>null</c> when it sent none.
    /// </param>
    /// <param name="innerException">The exception that caused this one, or <c>null</c>.</param>
    protected DaqifiDeviceException(
        string message,
        IReadOnlyList<string>? rawDeviceResponse = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        RawDeviceResponse = rawDeviceResponse ?? Array.Empty<string>();
    }

    /// <summary>
    /// Gets the raw response lines captured from the device when the failure was detected. Never
    /// <c>null</c>; empty when the device sent nothing readable, or when the failure was decided
    /// without asking it.
    /// </summary>
    public IReadOnlyList<string> RawDeviceResponse { get; }
}
