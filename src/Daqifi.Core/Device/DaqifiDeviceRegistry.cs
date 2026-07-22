using Daqifi.Core.Device.Discovery;

namespace Daqifi.Core.Device;

/// <summary>
/// Thread-safe registry of connected DAQiFi devices. Tracks the live device set, recognizes the
/// same physical unit reached over two transports at once (the classic "already connected via USB,
/// now discovered over WiFi" case), and owns the lifetime of everything it holds.
/// </summary>
/// <remarks>
/// <para>
/// Two distinct concepts run through this API:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Key</b> — the handle a caller looks a device up by (<see cref="TryGet"/>,
/// <see cref="Remove(string)"/>). It defaults to <see cref="DeviceIdentity.Key"/>, but a consumer
/// that already has its own device ids (an MCP server, a UI list) can supply one. Keys are unique;
/// registering under a key that is already taken is treated as a duplicate.
/// </description></item>
/// <item><description>
/// <b>Identity</b> — the <see cref="DeviceIdentity"/> fingerprint of the physical unit, used for
/// duplicate detection across transports. Two registrations may never share an identity.
/// </description></item>
/// </list>
/// <para>
/// <b>Ownership.</b> The registry disconnects and disposes every device it removes — including a
/// device it rejects as a duplicate and stale registrations it prunes. Once a device is passed to
/// <see cref="ConnectAsync"/> or <see cref="Register"/>, do not dispose it yourself.
/// </para>
/// <para>
/// <b>Liveness.</b> Registrations whose device stops reporting <see cref="DaqifiDevice.IsConnected"/>
/// are pruned before every registration attempt (and by <see cref="PruneDisconnected"/> on demand).
/// Pruning is only as good as the transport's drop detection, which is a real limit today: the
/// serial transport reports the OS handle's state, and that stays open after a USB device is
/// physically unplugged, so such a device keeps reporting <c>IsConnected</c> and is not pruned.
/// Devices you disconnect yourself, and drops a transport does report, are pruned as described.
/// The registry does not subscribe to device status events or reconnect on its own; automatic
/// reconnect is issue #379.
/// </para>
/// <para>
/// <b>Concurrency.</b> All members are safe to call from any thread. Reads snapshot the live set,
/// and the duplicate policy is always invoked without the internal lock held, so a policy that
/// blocks on a user prompt never blocks other threads. Because the set can change while the policy
/// is deciding, a registration handed back as a duplicate is revalidated afterwards: it is in the
/// live set at the moment it is returned, and if it went away the check is re-resolved against the
/// current set instead. Like any handle, it can still be removed by another thread later. Two
/// concurrent <see cref="ConnectAsync"/> calls for the <em>same</em> physical device may both open
/// a connection — the loser is detected after connecting and disposed — so a consumer that wants a
/// single connect attempt should serialize its own calls (see issue #342).
/// </para>
/// </remarks>
public sealed class DaqifiDeviceRegistry : IDisposable
{
    #region Private Types

    /// <summary>
    /// Opens a connection for <see cref="ConnectAsync"/>. Exists as a seam so the duplicate-policy
    /// paths can be tested without hardware; production always uses
    /// <see cref="DaqifiDeviceFactory.ConnectFromDeviceInfoAsync"/>.
    /// </summary>
    internal delegate Task<DaqifiDevice> DeviceConnector(
        IDeviceInfo deviceInfo,
        DeviceConnectionOptions? options,
        CancellationToken cancellationToken);

    #endregion

    #region Private Fields

    private readonly Dictionary<string, DeviceRegistration> _devices = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private readonly DeviceConnector _connector;

    private int _mintedKeyCounter;
    private volatile bool _disposed;

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new, empty registry.
    /// </summary>
    public DaqifiDeviceRegistry()
        : this(null)
    {
    }

    /// <summary>
    /// Initializes a new, empty registry with a custom connector (test seam).
    /// </summary>
    /// <param name="connector">
    /// The connector <see cref="ConnectAsync"/> opens connections with, or <c>null</c> for the
    /// default factory connect.
    /// </param>
    internal DaqifiDeviceRegistry(DeviceConnector? connector)
    {
        _connector = connector ?? ((info, options, cancellationToken) =>
            DaqifiDeviceFactory.ConnectFromDeviceInfoAsync(info, options, cancellationToken));
    }

    #endregion

    #region Public Surface

    /// <summary>
    /// Gets or sets the policy consulted when a device being registered turns out to be a
    /// duplicate of one already in the live set. When <c>null</c> (the default) the existing
    /// connection is kept and the new one is rejected — the safe choice that needs no callback.
    /// </summary>
    /// <remarks>
    /// Invoked without the registry lock held, so it may block on a user prompt. It is called for
    /// both the pre-connect and post-connect checks; use <see cref="DuplicateDeviceCheck.Phase"/>
    /// to tell them apart. An exception thrown by the policy propagates to the caller, and the
    /// device being registered is disposed.
    /// </remarks>
    public Func<DuplicateDeviceCheck, DuplicateDeviceAction>? DuplicatePolicy { get; set; }

    /// <summary>
    /// Occurs after a device has been added to the live set.
    /// </summary>
    /// <remarks>
    /// Raised without the registry lock held. An exception thrown by a subscriber is swallowed so
    /// one faulty handler cannot fail the registration or suppress sibling notifications.
    /// </remarks>
    public event EventHandler<DeviceRegisteredEventArgs>? DeviceAdded;

    /// <summary>
    /// Occurs after a device has been removed from the live set, disconnected, and disposed.
    /// Not raised by <see cref="Dispose"/>, which tears the whole registry down.
    /// </summary>
    /// <remarks>
    /// Raised without the registry lock held. An exception thrown by a subscriber is swallowed so
    /// one faulty handler cannot suppress sibling notifications.
    /// </remarks>
    public event EventHandler<DeviceRemovedEventArgs>? DeviceRemoved;

    /// <summary>
    /// Gets a point-in-time snapshot of the live set. The returned list is a copy and is safe to
    /// enumerate from any thread.
    /// </summary>
    public IReadOnlyList<DeviceRegistration> Devices
    {
        get
        {
            lock (_lock)
            {
                return new List<DeviceRegistration>(_devices.Values);
            }
        }
    }

    /// <summary>
    /// Gets the number of devices currently in the live set.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _devices.Count;
            }
        }
    }

    /// <summary>
    /// Connects to a discovered device and adds it to the live set, running the duplicate check
    /// twice: once from <paramref name="deviceInfo"/> before connecting, and again from the
    /// device's <see cref="DaqifiDevice.Metadata"/> afterwards — the serial number of a serial-port
    /// device is often only known once it has answered its first status message.
    /// </summary>
    /// <param name="deviceInfo">The device information from discovery.</param>
    /// <param name="key">
    /// The key to file the device under. When <c>null</c>, <see cref="DeviceIdentity.Key"/> is used
    /// (or a minted unique key when the device reports no identity at all).
    /// </param>
    /// <param name="options">Optional connection options passed through to the connect step.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the connection.</param>
    /// <returns>
    /// The outcome. On <see cref="DeviceRegistrationOutcome.DuplicateRejected"/> the result carries
    /// the existing registration — the live connection to the same unit — and the device just
    /// connected has already been disposed.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="deviceInfo"/> is <c>null</c>.</exception>
    /// <exception cref="ObjectDisposedException">The registry has been disposed.</exception>
    public async Task<DeviceRegistrationResult> ConnectAsync(
        IDeviceInfo deviceInfo,
        string? key = null,
        DeviceConnectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deviceInfo);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var discoveryIdentity = DeviceIdentity.FromDiscovery(deviceInfo);

        // Pre-connect check. Rejecting here is free: nothing has been opened yet. The check that
        // actually guards the live set runs after connecting, atomically with the add.
        DeviceRegistration? approvedReplacement = null;

        while (true)
        {
            DeviceRegistration? existing;
            List<DeviceRegistration>? stale;

            lock (_lock)
            {
                ThrowIfDisposed();
                stale = PruneLocked();
                existing = FindDuplicateLocked(key, discoveryIdentity);
            }

            CompleteRemoval(stale, DeviceRemovalReason.Disconnected);

            if (existing == null)
            {
                break;
            }

            var action = InvokePolicy(new DuplicateDeviceCheck(
                existing,
                discoveryIdentity,
                deviceInfo,
                newDevice: null,
                deviceInfo.ConnectionType,
                DuplicateCheckPhase.BeforeConnect));

            if (action == DuplicateDeviceAction.Cancel)
            {
                return new DeviceRegistrationResult(DeviceRegistrationOutcome.Canceled, null);
            }

            if (action == DuplicateDeviceAction.SwitchToNew)
            {
                // Record the decision but keep the existing connection until the new one is
                // actually open. Dropping it first would leave the caller with nothing when the
                // new transport fails to connect — switching a healthy USB session to a WiFi
                // address that turns out not to answer must not cost them the session they had.
                // Registration drops it once the replacement is live, without asking the policy a
                // second time. If it vanishes in the meantime the reference simply matches nothing
                // there, and the new device is registered on its own merits.
                approvedReplacement = existing;
                break;
            }

            // KeepExisting. Only hand that registration back if it is still the live one: the
            // policy ran without the lock and is documented as free to block on a user prompt, so
            // another thread (or the policy itself) may have removed or replaced it meanwhile, and
            // returning it then would deliver a disposed handle as "the live connection". When it
            // has gone, re-resolve against the current set instead — which either finds a
            // different duplicate to ask about or clears the way to connect.
            if (IsStillRegistered(existing))
            {
                return new DeviceRegistrationResult(DeviceRegistrationOutcome.DuplicateRejected, existing);
            }
        }

        // Re-check immediately before opening anything. The policy above runs without the lock and
        // may block indefinitely on a user prompt, so a registry disposed during that window would
        // otherwise still reach the connector and start fresh transport activity on the way down.
        // This narrows that window to a few instructions rather than closing it: a Dispose landing
        // between here and the connector, or during the connect itself, still lets the connection
        // open, and RegisterCore then throws and disposes it. Guaranteeing no transport activity
        // outlives the registry needs in-flight tracking on Dispose, which is out of scope here.
        lock (_lock)
        {
            ThrowIfDisposed();
        }

        var device = await _connector(deviceInfo, options, cancellationToken).ConfigureAwait(false);

        return RegisterCore(device, deviceInfo, key, approvedReplacement);
    }

    /// <summary>
    /// Adds an already-connected device to the live set, running the duplicate check against its
    /// <see cref="DaqifiDevice.Metadata"/>. Use this when the connection was opened elsewhere (for
    /// example by <see cref="DaqifiDeviceFactory"/> directly); <see cref="ConnectAsync"/> is the
    /// one-call path.
    /// </summary>
    /// <param name="device">The connected device. The registry takes ownership of it.</param>
    /// <param name="deviceInfo">
    /// The discovery metadata the device was connected from, if available. Supplying it lets the
    /// registry report the device's transport and keeps discovery-only discriminators (the USB
    /// location key, and the MAC address on a device whose metadata has not filled one in yet).
    /// </param>
    /// <param name="key">
    /// The key to file the device under. When <c>null</c>, <see cref="DeviceIdentity.Key"/> is used
    /// (or a minted unique key when the device reports no identity at all).
    /// </param>
    /// <returns>
    /// The outcome, in the same shape <see cref="ConnectAsync"/> returns. Registering a device
    /// that is already in the live set is an idempotent no-op that reports
    /// <see cref="DeviceRegistrationOutcome.Registered"/> with its existing registration.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="device"/> is not connected.</exception>
    /// <exception cref="ObjectDisposedException">The registry has been disposed.</exception>
    public DeviceRegistrationResult Register(
        DaqifiDevice device,
        IDeviceInfo? deviceInfo = null,
        string? key = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ThrowIfDisposed();

        // Validate before taking ownership, so a caller mistake never costs them their device.
        if (!device.IsConnected)
        {
            throw new ArgumentException(
                "Only a connected device can be registered. Connect the device first.", nameof(device));
        }

        return RegisterCore(device, deviceInfo, key, approvedReplacement: null);
    }

    /// <summary>
    /// Looks up a registration by key.
    /// </summary>
    /// <param name="key">The key the device was filed under.</param>
    /// <param name="registration">The registration, or <c>null</c> when the key is unknown.</param>
    /// <returns><c>true</c> when a registration was found.</returns>
    /// <remarks>
    /// A lookup never prunes, so a device that dropped since it was registered is still returned;
    /// check <see cref="DaqifiDevice.IsConnected"/> if that matters to the caller.
    /// </remarks>
    public bool TryGet(string key, out DeviceRegistration? registration)
    {
        if (string.IsNullOrEmpty(key))
        {
            registration = null;
            return false;
        }

        lock (_lock)
        {
            return _devices.TryGetValue(key, out registration);
        }
    }

    /// <summary>
    /// Looks up a device by key. Shorthand for <see cref="TryGet"/> followed by
    /// <see cref="DeviceRegistration.Device"/>.
    /// </summary>
    /// <param name="key">The key the device was filed under.</param>
    /// <param name="device">The device, or <c>null</c> when the key is unknown.</param>
    /// <returns><c>true</c> when a device was found.</returns>
    public bool TryGetDevice(string key, out DaqifiDevice? device)
    {
        var found = TryGet(key, out var registration);
        device = registration?.Device;
        return found;
    }

    /// <summary>
    /// Finds the registration whose device is the same physical unit as
    /// <paramref name="identity"/> — the duplicate-detection primitive, exposed for consumers that
    /// want to check before opening a connection of their own.
    /// </summary>
    /// <param name="identity">The identity to match against.</param>
    /// <returns>The matching registration, or <c>null</c> when the unit is not in the live set.</returns>
    public DeviceRegistration? Find(DeviceIdentity identity)
    {
        if (identity is null || identity.IsEmpty)
        {
            return null;
        }

        lock (_lock)
        {
            return FindByIdentityLocked(identity);
        }
    }

    /// <summary>
    /// Removes a device from the live set, disconnecting and disposing it.
    /// </summary>
    /// <param name="key">The key the device was filed under.</param>
    /// <returns><c>true</c> when a device was removed.</returns>
    public bool Remove(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        DeviceRegistration? removed;
        lock (_lock)
        {
            if (!_devices.Remove(key, out removed))
            {
                return false;
            }
        }

        CompleteRemoval(new List<DeviceRegistration> { removed! }, DeviceRemovalReason.Removed);
        return true;
    }

    /// <summary>
    /// Removes a device from the live set by reference, disconnecting and disposing it.
    /// </summary>
    /// <param name="device">The device to remove.</param>
    /// <returns><c>true</c> when the device was in the live set and was removed.</returns>
    public bool Remove(DaqifiDevice device)
    {
        if (device is null)
        {
            return false;
        }

        DeviceRegistration? removed = null;
        lock (_lock)
        {
            foreach (var registration in _devices.Values)
            {
                if (ReferenceEquals(registration.Device, device))
                {
                    removed = registration;
                    break;
                }
            }

            if (removed == null)
            {
                return false;
            }

            _devices.Remove(removed.Key);
        }

        CompleteRemoval(new List<DeviceRegistration> { removed }, DeviceRemovalReason.Removed);
        return true;
    }

    /// <summary>
    /// Removes every registration whose device is no longer connected, disposing each one. Called
    /// automatically before each registration attempt; exposed for consumers that poll the live set.
    /// </summary>
    /// <returns>The number of stale registrations removed.</returns>
    public int PruneDisconnected()
    {
        List<DeviceRegistration>? stale;
        lock (_lock)
        {
            stale = PruneLocked();
        }

        CompleteRemoval(stale, DeviceRemovalReason.Disconnected);
        return stale?.Count ?? 0;
    }

    /// <summary>
    /// Removes every device from the live set, disconnecting and disposing each one.
    /// </summary>
    public void Clear()
    {
        List<DeviceRegistration> removed;
        lock (_lock)
        {
            if (_devices.Count == 0)
            {
                return;
            }

            removed = new List<DeviceRegistration>(_devices.Values);
            _devices.Clear();
        }

        CompleteRemoval(removed, DeviceRemovalReason.Cleared);
    }

    /// <summary>
    /// Disconnects and disposes every device in the live set and marks the registry unusable.
    /// <see cref="DeviceRemoved"/> is not raised: the registry itself is going away, so there is no
    /// live set left to keep in sync.
    /// </summary>
    /// <remarks>
    /// Does not wait for work already in flight. A <see cref="ConnectAsync"/> that has already
    /// reached its connector finishes opening the connection, and is then rejected and disposed by
    /// the registration step — so nothing leaks, but the transport activity does complete. Callers
    /// that need connect attempts to stop promptly should cancel the token they passed in.
    /// </remarks>
    public void Dispose()
    {
        List<DeviceRegistration> removed;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            removed = new List<DeviceRegistration>(_devices.Values);
            _devices.Clear();
        }

        foreach (var registration in removed)
        {
            DisposeDevice(registration.Device);
        }
    }

    #endregion

    #region Registration

    /// <summary>
    /// Runs the post-connect duplicate check and adds the device, taking ownership of it: on any
    /// outcome other than a successful add — including a policy callback that throws — the device
    /// is disconnected and disposed.
    /// </summary>
    /// <param name="device">The connected device being registered.</param>
    /// <param name="deviceInfo">The discovery metadata, if available.</param>
    /// <param name="key">The requested key, or <c>null</c> to derive one.</param>
    /// <param name="approvedReplacement">
    /// A registration the pre-connect check already got approval to replace. Finding it again here
    /// is not a fresh duplicate: it is dropped without re-consulting the policy, so a consumer that
    /// prompts its user is asked once per connection attempt rather than twice.
    /// </param>
    private DeviceRegistrationResult RegisterCore(
        DaqifiDevice device,
        IDeviceInfo? deviceInfo,
        string? key,
        DeviceRegistration? approvedReplacement)
    {
        var replacedExisting = false;

        try
        {
            // Metadata is authoritative once connected, but it never carries the USB location key
            // and may not have filled in a MAC address yet, so fall back to what discovery saw.
            var identity = DeviceIdentity
                .FromMetadata(device.Metadata, deviceInfo?.LocationKey)
                .MergeWith(deviceInfo != null ? DeviceIdentity.FromDiscovery(deviceInfo) : null);

            var connectionType = deviceInfo?.ConnectionType ?? ConnectionType.Unknown;

            while (true)
            {
                DeviceRegistration? existing;
                DeviceRegistration? added = null;
                DeviceRegistration? alreadyRegistered = null;
                List<DeviceRegistration>? stale;

                lock (_lock)
                {
                    ThrowIfDisposed();
                    stale = PruneLocked();
                    existing = FindDuplicateLocked(key, identity, device);

                    if (existing == null)
                    {
                        // Resolving the key and adding under the same lock as the duplicate check
                        // is what makes concurrent registrations safe: no second thread can slip a
                        // duplicate in between the two steps.
                        added = new DeviceRegistration(ResolveKeyLocked(key, identity), device, identity, deviceInfo);
                        _devices.Add(added.Key, added);
                    }
                    else if (ReferenceEquals(existing.Device, device))
                    {
                        // This exact device is already in the live set. Re-registering it is a
                        // no-op — emphatically not a duplicate to dispose, which would leave the
                        // registry holding a dead handle.
                        alreadyRegistered = existing;
                    }
                }

                CompleteRemoval(stale, DeviceRemovalReason.Disconnected);

                if (alreadyRegistered != null)
                {
                    return new DeviceRegistrationResult(
                        DeviceRegistrationOutcome.Registered, alreadyRegistered);
                }

                if (added != null)
                {
                    RaiseDeviceAdded(added);
                    return new DeviceRegistrationResult(
                        replacedExisting
                            ? DeviceRegistrationOutcome.ReplacedExisting
                            : DeviceRegistrationOutcome.Registered,
                        added);
                }

                // A registration the caller already approved replacing before we connected is not
                // a fresh duplicate — dropping it now is finishing that decision, not making one.
                var action = ReferenceEquals(existing, approvedReplacement)
                    ? DuplicateDeviceAction.SwitchToNew
                    : InvokePolicy(new DuplicateDeviceCheck(
                        existing!,
                        identity,
                        deviceInfo,
                        device,
                        connectionType,
                        DuplicateCheckPhase.AfterConnect));

                switch (action)
                {
                    case DuplicateDeviceAction.SwitchToNew:
                        RemoveRegistration(existing!, DeviceRemovalReason.Replaced);
                        replacedExisting = true;
                        continue;

                    case DuplicateDeviceAction.Cancel:
                        DisposeDevice(device);
                        return new DeviceRegistrationResult(DeviceRegistrationOutcome.Canceled, null);

                    default:
                        // KeepExisting. Same revalidation as the pre-connect path: the policy ran
                        // without the lock, so only hand back a registration that is still the
                        // live one. If it has gone, loop rather than disposing the device we just
                        // connected — re-resolving may now find no duplicate at all, in which case
                        // this device is what the caller should end up with.
                        if (!IsStillRegistered(existing!))
                        {
                            continue;
                        }

                        DisposeDevice(device);
                        return new DeviceRegistrationResult(
                            DeviceRegistrationOutcome.DuplicateRejected, existing);
                }
            }
        }
        catch
        {
            // Ownership already transferred, so the device is ours to clean up.
            DisposeDevice(device);
            throw;
        }
    }

    /// <summary>
    /// Finds the registration a device being registered would collide with: the entry already
    /// filed under the requested key, the entry holding this very device instance, or one
    /// describing the same physical unit. Caller must hold <see cref="_lock"/>.
    /// </summary>
    private DeviceRegistration? FindDuplicateLocked(string? key, DeviceIdentity identity, DaqifiDevice? device = null)
    {
        if (!string.IsNullOrEmpty(key) && _devices.TryGetValue(key!, out var byKey))
        {
            return byKey;
        }

        if (device != null)
        {
            foreach (var registration in _devices.Values)
            {
                if (ReferenceEquals(registration.Device, device))
                {
                    return registration;
                }
            }
        }

        return FindByIdentityLocked(identity);
    }

    /// <summary>
    /// Finds the registration describing the same physical unit as <paramref name="identity"/>.
    /// Caller must hold <see cref="_lock"/>.
    /// </summary>
    private DeviceRegistration? FindByIdentityLocked(DeviceIdentity identity)
    {
        if (identity.IsEmpty)
        {
            return null;
        }

        foreach (var registration in _devices.Values)
        {
            if (registration.Identity.Matches(identity))
            {
                return registration;
            }
        }

        return null;
    }

    /// <summary>
    /// Picks the key a new registration is filed under. A caller-supplied key is used as-is (the
    /// duplicate check has already proven it free); otherwise the identity key is used, falling
    /// back to a minted unique key so devices that report no identity never collide. Caller must
    /// hold <see cref="_lock"/>.
    /// </summary>
    private string ResolveKeyLocked(string? requestedKey, DeviceIdentity identity)
    {
        if (!string.IsNullOrEmpty(requestedKey))
        {
            return requestedKey!;
        }

        if (!identity.IsEmpty && !_devices.ContainsKey(identity.Key))
        {
            return identity.Key;
        }

        string minted;
        do
        {
            minted = "device-" + (++_mintedKeyCounter);
        }
        while (_devices.ContainsKey(minted));

        return minted;
    }

    /// <summary>
    /// Removes every registration whose device is no longer connected and returns them, or
    /// <c>null</c> when there were none. Disposal and notification happen outside the lock, in
    /// <see cref="CompleteRemoval"/>. Caller must hold <see cref="_lock"/>.
    /// </summary>
    private List<DeviceRegistration>? PruneLocked()
    {
        List<DeviceRegistration>? stale = null;

        foreach (var registration in _devices.Values)
        {
            if (!registration.Device.IsConnected)
            {
                (stale ??= new List<DeviceRegistration>()).Add(registration);
            }
        }

        if (stale != null)
        {
            foreach (var registration in stale)
            {
                _devices.Remove(registration.Key);
            }
        }

        return stale;
    }

    /// <summary>
    /// Removes one registration and disposes its device.
    /// </summary>
    private void RemoveRegistration(DeviceRegistration registration, DeviceRemovalReason reason)
    {
        lock (_lock)
        {
            // Only remove the exact entry we resolved: a concurrent operation may already have
            // replaced it with a different device under the same key.
            if (!_devices.TryGetValue(registration.Key, out var current) || !ReferenceEquals(current, registration))
            {
                return;
            }

            _devices.Remove(registration.Key);
        }

        CompleteRemoval(new List<DeviceRegistration> { registration }, reason);
    }

    /// <summary>
    /// Disposes the devices of already-removed registrations and announces them. Always called
    /// without the lock held: disconnecting touches the transport, and subscribers must be free to
    /// call back into the registry.
    /// </summary>
    private void CompleteRemoval(List<DeviceRegistration>? removed, DeviceRemovalReason reason)
    {
        if (removed == null || removed.Count == 0)
        {
            return;
        }

        foreach (var registration in removed)
        {
            DisposeDevice(registration.Device);
            RaiseDeviceRemoved(registration, reason);
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Reports whether a registration captured earlier is still the entry filed under its key.
    /// Used to revalidate a registration across the duplicate-policy call, which runs without the
    /// lock held and may block indefinitely, so the live set can change underneath it.
    /// </summary>
    /// <param name="registration">The registration captured before the policy ran.</param>
    /// <returns><c>true</c> when that exact instance is still in the live set.</returns>
    private bool IsStillRegistered(DeviceRegistration registration)
    {
        lock (_lock)
        {
            return _devices.TryGetValue(registration.Key, out var current)
                && ReferenceEquals(current, registration);
        }
    }

    /// <summary>
    /// Asks the duplicate policy what to do, defaulting to keeping the existing connection when no
    /// policy is set or an out-of-range action is returned.
    /// </summary>
    private DuplicateDeviceAction InvokePolicy(DuplicateDeviceCheck check)
    {
        var policy = DuplicatePolicy;
        if (policy == null)
        {
            return DuplicateDeviceAction.KeepExisting;
        }

        var action = policy(check);
        return Enum.IsDefined(action) ? action : DuplicateDeviceAction.KeepExisting;
    }

    /// <summary>
    /// Disconnects and disposes a device the registry owns. Disconnect failures are ignored: the
    /// transport is being torn down either way, and disposal must still run.
    /// </summary>
    private static void DisposeDevice(DaqifiDevice device)
    {
        try
        {
            device.Disconnect();
        }
        catch
        {
            // Best effort — Dispose below tears the transport down regardless.
        }

        try
        {
            device.Dispose();
        }
        catch
        {
            // Best effort — a device that fails to dispose must not fail the registry operation.
        }
    }

    /// <summary>
    /// Raises <see cref="DeviceAdded"/>, swallowing subscriber exceptions so a faulty handler
    /// cannot fail a registration that has already happened.
    /// </summary>
    private void RaiseDeviceAdded(DeviceRegistration registration)
    {
        try
        {
            DeviceAdded?.Invoke(this, new DeviceRegisteredEventArgs(registration));
        }
        catch
        {
            // Intentionally swallowed: the device is registered either way.
        }
    }

    /// <summary>
    /// Raises <see cref="DeviceRemoved"/>, swallowing subscriber exceptions so a faulty handler
    /// cannot suppress sibling notifications.
    /// </summary>
    private void RaiseDeviceRemoved(DeviceRegistration registration, DeviceRemovalReason reason)
    {
        try
        {
            DeviceRemoved?.Invoke(this, new DeviceRemovedEventArgs(registration, reason));
        }
        catch
        {
            // Intentionally swallowed: the device is already removed and disposed.
        }
    }

    /// <summary>
    /// Throws <see cref="ObjectDisposedException"/> if this registry has been disposed.
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DaqifiDeviceRegistry));
        }
    }

    #endregion
}
