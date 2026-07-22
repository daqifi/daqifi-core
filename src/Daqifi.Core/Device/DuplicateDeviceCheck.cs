using Daqifi.Core.Device.Discovery;

namespace Daqifi.Core.Device;

/// <summary>
/// The action a <see cref="DaqifiDeviceRegistry.DuplicatePolicy"/> takes when the registry is asked
/// to register a device that is already in the live set under a different transport.
/// </summary>
public enum DuplicateDeviceAction
{
    /// <summary>
    /// Keep the already-registered connection and reject the new one. This is the default when no
    /// policy is supplied.
    /// </summary>
    KeepExisting,

    /// <summary>
    /// Drop the already-registered connection (disconnecting and disposing it) and continue with
    /// the new one. The existing connection is not dropped until the new one is actually open, so
    /// a replacement that fails to connect leaves the original in place rather than costing the
    /// caller both.
    /// </summary>
    SwitchToNew,

    /// <summary>
    /// Abandon the registration entirely, leaving the existing connection in place. Differs from
    /// <see cref="KeepExisting"/> only in the reported outcome: the caller asked to cancel rather
    /// than being handed the existing device.
    /// </summary>
    Cancel
}

/// <summary>
/// When a duplicate was detected, relative to the connection attempt.
/// </summary>
public enum DuplicateCheckPhase
{
    /// <summary>
    /// Detected from discovery metadata before the new connection was opened. Nothing has been
    /// connected yet, so rejecting costs nothing.
    /// </summary>
    BeforeConnect,

    /// <summary>
    /// Detected from device metadata after the new connection was opened — the case where the
    /// serial number only becomes known once the device answers its first status message.
    /// </summary>
    AfterConnect
}

/// <summary>
/// Describes a detected duplicate: the same physical unit reached over two transports at once
/// (typically USB and WiFi). Passed to <see cref="DaqifiDeviceRegistry.DuplicatePolicy"/> so a
/// consumer can decide which connection wins — for example by prompting the user.
/// </summary>
public sealed class DuplicateDeviceCheck
{
    internal DuplicateDeviceCheck(
        DeviceRegistration existing,
        DeviceIdentity newIdentity,
        IDeviceInfo? newDeviceInfo,
        DaqifiDevice? newDevice,
        ConnectionType newConnectionType,
        DuplicateCheckPhase phase)
    {
        Existing = existing;
        NewIdentity = newIdentity;
        NewDeviceInfo = newDeviceInfo;
        NewDevice = newDevice;
        NewConnectionType = newConnectionType;
        Phase = phase;
    }

    /// <summary>
    /// Gets the registration already in the live set.
    /// </summary>
    public DeviceRegistration Existing { get; }

    /// <summary>
    /// Gets the identity computed for the device being registered.
    /// </summary>
    public DeviceIdentity NewIdentity { get; }

    /// <summary>
    /// Gets the discovery metadata the new connection was made from, or <c>null</c> when an
    /// already-connected device was registered without it.
    /// </summary>
    public IDeviceInfo? NewDeviceInfo { get; }

    /// <summary>
    /// Gets the device being registered, or <c>null</c> when the duplicate was caught in
    /// <see cref="DuplicateCheckPhase.BeforeConnect"/> and nothing has been connected yet.
    /// </summary>
    public DaqifiDevice? NewDevice { get; }

    /// <summary>
    /// Gets the transport the new connection uses.
    /// </summary>
    public ConnectionType NewConnectionType { get; }

    /// <summary>
    /// Gets the transport the existing connection uses.
    /// </summary>
    public ConnectionType ExistingConnectionType => Existing.ConnectionType;

    /// <summary>
    /// Gets when the duplicate was detected, relative to the new connection being opened.
    /// </summary>
    public DuplicateCheckPhase Phase { get; }
}
