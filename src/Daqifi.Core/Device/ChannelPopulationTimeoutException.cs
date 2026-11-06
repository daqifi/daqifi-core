using System;
using System.Collections.Generic;

namespace Daqifi.Core.Device;

/// <summary>
/// Thrown when <see cref="DaqifiDevice.InitializeAsync"/> waited out its timeout without the
/// device reporting its channel configuration.
/// </summary>
/// <remarks>
/// <para>
/// Before it gives up, initialization asks the device two questions on the connection it
/// already has open, and the answers are carried here. They separate causes that used to share
/// one sentence: a device that is absent or still starting, and a device that answers every
/// text query but sends no status this connection can read (issue #911).
/// </para>
/// <para>
/// This derives from <see cref="TimeoutException"/>, which is what initialization threw before,
/// so existing handlers keep working.
/// </para>
/// </remarks>
public class ChannelPopulationTimeoutException : TimeoutException
{
    /// <summary>
    /// Gets the device's answer to <c>*IDN?</c>, asked after the wait expired, or <c>null</c>
    /// when the device did not answer it.
    /// </summary>
    public string? DeviceIdentity { get; }

    /// <summary>
    /// Gets the value the device reported for <c>SYSTem:STReam:INTerface?</c>, or <c>null</c>
    /// when it was not asked or did not answer.
    /// </summary>
    /// <remarks>
    /// 0 is USB, 1 is WiFi, 2 is the SD card. The device sends its output, status messages
    /// included, to whichever one this names — so a value that is not the connection in hand
    /// explains the missing status completely.
    /// </remarks>
    public int? StreamInterface { get; }

    /// <summary>
    /// Gets whether the device answered <c>*IDN?</c> after the wait expired. When this is
    /// <c>true</c>, the device is not unresponsive, whatever else is wrong.
    /// </summary>
    public bool IsDeviceResponding => DeviceIdentity != null;

    /// <summary>
    /// Gets the raw lines the two questions returned, for diagnostics.
    /// </summary>
    public IReadOnlyList<string> RawDeviceResponse { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelPopulationTimeoutException"/> class.
    /// </summary>
    public ChannelPopulationTimeoutException(
        string message,
        string? deviceIdentity = null,
        int? streamInterface = null,
        IReadOnlyList<string>? rawDeviceResponse = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        DeviceIdentity = deviceIdentity;
        StreamInterface = streamInterface;
        RawDeviceResponse = rawDeviceResponse ?? Array.Empty<string>();
    }
}
