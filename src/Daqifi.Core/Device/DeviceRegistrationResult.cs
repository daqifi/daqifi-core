namespace Daqifi.Core.Device;

/// <summary>
/// How a <see cref="DaqifiDeviceRegistry"/> registration attempt ended.
/// </summary>
public enum DeviceRegistrationOutcome
{
    /// <summary>
    /// The device was added to the live set.
    /// </summary>
    Registered,

    /// <summary>
    /// The device was added to the live set after a duplicate registration was dropped, because
    /// the duplicate policy returned <see cref="DuplicateDeviceAction.SwitchToNew"/>.
    /// </summary>
    ReplacedExisting,

    /// <summary>
    /// The device was rejected as a duplicate of one already in the live set, because the
    /// duplicate policy returned <see cref="DuplicateDeviceAction.KeepExisting"/> (the default).
    /// The existing registration is reported in <see cref="DeviceRegistrationResult.Registration"/>.
    /// </summary>
    DuplicateRejected,

    /// <summary>
    /// The registration was abandoned because the duplicate policy returned
    /// <see cref="DuplicateDeviceAction.Cancel"/>.
    /// </summary>
    Canceled
}

/// <summary>
/// The result of a <see cref="DaqifiDeviceRegistry"/> registration attempt.
/// </summary>
public sealed class DeviceRegistrationResult
{
    internal DeviceRegistrationResult(DeviceRegistrationOutcome outcome, DeviceRegistration? registration)
    {
        Outcome = outcome;
        Registration = registration;
    }

    /// <summary>
    /// Gets how the attempt ended.
    /// </summary>
    public DeviceRegistrationOutcome Outcome { get; }

    /// <summary>
    /// Gets the registration the caller should now use, or <c>null</c> when the attempt was
    /// canceled. On <see cref="DeviceRegistrationOutcome.DuplicateRejected"/> this is the existing
    /// registration that won — the live connection to the same physical unit — not the rejected
    /// device, which the registry has already disposed.
    /// </summary>
    public DeviceRegistration? Registration { get; }

    /// <summary>
    /// Gets the device the caller should now use, or <c>null</c> when the attempt was canceled.
    /// Shorthand for <see cref="DeviceRegistration.Device"/> on <see cref="Registration"/>.
    /// </summary>
    public DaqifiDevice? Device => Registration?.Device;

    /// <summary>
    /// Gets the key <see cref="Registration"/> is filed under, or <c>null</c> when the attempt was
    /// canceled.
    /// </summary>
    public string? Key => Registration?.Key;

    /// <summary>
    /// Gets a value indicating whether the attempt added the device that was passed in, as opposed
    /// to rejecting it in favor of an existing registration or canceling.
    /// </summary>
    public bool IsRegistered =>
        Outcome is DeviceRegistrationOutcome.Registered or DeviceRegistrationOutcome.ReplacedExisting;

    /// <summary>
    /// Gets a value indicating whether a duplicate of the device was already in the live set,
    /// whichever connection ended up winning.
    /// </summary>
    public bool WasDuplicate =>
        Outcome is DeviceRegistrationOutcome.ReplacedExisting
            or DeviceRegistrationOutcome.DuplicateRejected
            or DeviceRegistrationOutcome.Canceled;
}
