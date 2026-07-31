namespace Daqifi.Core.Device;

/// <summary>
/// Reports that automatic reconnection exhausted every allowed attempt without restoring the
/// session (issue #379).
/// </summary>
/// <remarks>
/// This is never thrown — nobody is on the other end of a background reconnect loop to catch it.
/// It exists so that giving up arrives on <see cref="DaqifiDevice.ErrorOccurred"/> as a typed
/// failure carrying the attempt count, with whatever ended the final attempt as its
/// <see cref="Exception.InnerException"/>.
/// </remarks>
public class DeviceReconnectFailedException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceReconnectFailedException"/> class.
    /// </summary>
    /// <param name="deviceName">The device that could not be reconnected.</param>
    /// <param name="attemptsMade">How many attempts were made before giving up.</param>
    /// <param name="innerException">The failure that ended the final attempt, if there was one.</param>
    public DeviceReconnectFailedException(string deviceName, int attemptsMade, Exception? innerException = null)
        : base(
            $"Device '{deviceName}' could not be reconnected after {attemptsMade} attempt(s).",
            innerException)
    {
        DeviceName = deviceName;
        AttemptsMade = attemptsMade;
    }

    /// <summary>Gets the name of the device that could not be reconnected.</summary>
    public string DeviceName { get; }

    /// <summary>Gets how many reconnect attempts were made before giving up.</summary>
    public int AttemptsMade { get; }
}
