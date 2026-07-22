using Daqifi.Core.Device;
using Daqifi.Core.Device.Discovery;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Registry behavior, including the cross-transport duplicate-detection cases ported from the
/// desktop app's <c>DuplicateDeviceDetectionTests</c> (the reference implementation this type
/// replaces).
/// </summary>
public class DaqifiDeviceRegistryTests
{
    #region Helpers

    /// <summary>
    /// A device that reports itself connected without touching any transport — the no-transport
    /// constructor short-circuits <see cref="DaqifiDevice.Connect"/> to a status change.
    /// </summary>
    private static DaqifiDevice ConnectedDevice(string name = "device", string? serialNumber = null, string? macAddress = null)
    {
        var device = new DaqifiDevice(name);
        if (serialNumber != null)
        {
            device.Metadata.SerialNumber = serialNumber;
        }
        if (macAddress != null)
        {
            device.Metadata.MacAddress = macAddress;
        }

        device.Connect();
        return device;
    }

    private static DeviceInfo UsbInfo(string serialNumber = "", string portName = "COM3", string? locationKey = null) => new()
    {
        Name = "usb-device",
        ConnectionType = ConnectionType.Serial,
        SerialNumber = serialNumber,
        PortName = portName,
        LocationKey = locationKey
    };

    private static DeviceInfo WifiInfo(string serialNumber = "", string? macAddress = null) => new()
    {
        Name = "wifi-device",
        ConnectionType = ConnectionType.WiFi,
        SerialNumber = serialNumber,
        MacAddress = macAddress,
        IPAddress = System.Net.IPAddress.Parse("192.168.1.50"),
        Port = 9760
    };

    /// <summary>
    /// Builds a registry whose connect step is a stub, so the duplicate-policy paths can be
    /// exercised without hardware. The stub hands out the queued devices in order.
    /// </summary>
    private static DaqifiDeviceRegistry RegistryReturning(List<int> connectCalls, params DaqifiDevice[] devices)
    {
        var index = 0;
        return new DaqifiDeviceRegistry((_, _, _) =>
        {
            var next = devices[index++];
            connectCalls.Add(index);
            return Task.FromResult(next);
        });
    }

    #endregion

    #region Duplicate detection (ported from desktop's DuplicateDeviceDetectionTests)

    [Fact]
    public async Task ConnectAsync_DuplicateSerialNumber_KeepsExistingAndRejectsNew()
    {
        const string serialNumber = "DAQ-12345";
        var connects = new List<int>();
        var overUsb = ConnectedDevice("usb", serialNumber);
        var overWifi = ConnectedDevice("wifi", serialNumber);
        using var registry = RegistryReturning(connects, overUsb, overWifi);

        await registry.ConnectAsync(UsbInfo(serialNumber));
        var result = await registry.ConnectAsync(WifiInfo(serialNumber));

        Assert.Equal(DeviceRegistrationOutcome.DuplicateRejected, result.Outcome);
        Assert.True(result.WasDuplicate);
        Assert.False(result.IsRegistered);
        Assert.Same(overUsb, result.Device);
        Assert.Equal(1, registry.Count);
        // The duplicate was caught from discovery metadata, so no second connection was opened.
        Assert.Single(connects);
        Assert.True(overUsb.IsConnected);
    }

    [Fact]
    public async Task ConnectAsync_DifferentSerialNumbers_RegistersBoth()
    {
        var connects = new List<int>();
        using var registry = RegistryReturning(
            connects,
            ConnectedDevice("usb", "DAQ-12345"),
            ConnectedDevice("wifi", "DAQ-67890"));

        var first = await registry.ConnectAsync(UsbInfo("DAQ-12345"));
        var second = await registry.ConnectAsync(WifiInfo("DAQ-67890"));

        Assert.Equal(DeviceRegistrationOutcome.Registered, first.Outcome);
        Assert.Equal(DeviceRegistrationOutcome.Registered, second.Outcome);
        Assert.Equal(2, registry.Count);
    }

    [Fact]
    public async Task ConnectAsync_DevicesWithoutIdentity_NeverMatchEachOther()
    {
        var connects = new List<int>();
        using var registry = RegistryReturning(connects, ConnectedDevice("first"), ConnectedDevice("second"));

        var first = await registry.ConnectAsync(UsbInfo(serialNumber: "", portName: "COM3"));
        var second = await registry.ConnectAsync(UsbInfo(serialNumber: "", portName: "COM4"));

        Assert.Equal(DeviceRegistrationOutcome.Registered, first.Outcome);
        Assert.Equal(DeviceRegistrationOutcome.Registered, second.Outcome);
        Assert.Equal(2, registry.Count);
        Assert.NotEqual(first.Key, second.Key);
    }

    [Fact]
    public async Task ConnectAsync_DuplicatePolicy_IsInvokedWithBothTransports()
    {
        const string serialNumber = "DAQ-12345";
        var connects = new List<int>();
        var checks = new List<DuplicateDeviceCheck>();
        using var registry = RegistryReturning(
            connects,
            ConnectedDevice("usb", serialNumber),
            ConnectedDevice("wifi", serialNumber));
        registry.DuplicatePolicy = check =>
        {
            checks.Add(check);
            return DuplicateDeviceAction.KeepExisting;
        };

        await registry.ConnectAsync(UsbInfo(serialNumber));
        await registry.ConnectAsync(WifiInfo(serialNumber));

        var check = Assert.Single(checks);
        Assert.Equal(DuplicateCheckPhase.BeforeConnect, check.Phase);
        Assert.Equal(ConnectionType.Serial, check.ExistingConnectionType);
        Assert.Equal(ConnectionType.WiFi, check.NewConnectionType);
        Assert.Null(check.NewDevice);
        Assert.Equal(serialNumber, check.NewIdentity.SerialNumber);
        Assert.Equal(1, registry.Count);
    }

    #endregion

    #region Duplicate detection: identity fallbacks and phases

    [Fact]
    public async Task ConnectAsync_SerialMatch_IsCaseInsensitive()
    {
        var connects = new List<int>();
        using var registry = RegistryReturning(
            connects,
            ConnectedDevice("usb", "DAQ-12345"),
            ConnectedDevice("wifi", "daq-12345"));

        await registry.ConnectAsync(UsbInfo("DAQ-12345"));
        var result = await registry.ConnectAsync(WifiInfo("daq-12345"));

        Assert.Equal(DeviceRegistrationOutcome.DuplicateRejected, result.Outcome);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public async Task ConnectAsync_MatchesOnMac_WhenSerialIsBlank()
    {
        var connects = new List<int>();
        using var registry = RegistryReturning(
            connects,
            ConnectedDevice("first", macAddress: "AA-BB-CC-DD-EE-FF"),
            ConnectedDevice("second", macAddress: "aa:bb:cc:dd:ee:ff"));

        await registry.ConnectAsync(WifiInfo(macAddress: "AA-BB-CC-DD-EE-FF"));
        var result = await registry.ConnectAsync(WifiInfo(macAddress: "aa:bb:cc:dd:ee:ff"));

        Assert.Equal(DeviceRegistrationOutcome.DuplicateRejected, result.Outcome);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public async Task ConnectAsync_MatchesOnLocationKey_WhenSerialAndMacAreBlank()
    {
        var connects = new List<int>();
        using var registry = RegistryReturning(connects, ConnectedDevice("first"), ConnectedDevice("second"));

        await registry.ConnectAsync(UsbInfo(portName: "COM3", locationKey: "Port_#0001.Hub_#0001"));
        var result = await registry.ConnectAsync(UsbInfo(portName: "COM7", locationKey: "Port_#0001.Hub_#0001"));

        Assert.Equal(DeviceRegistrationOutcome.DuplicateRejected, result.Outcome);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public async Task ConnectAsync_SerialOnlyKnownAfterConnect_IsCaughtByThePostConnectCheck()
    {
        const string serialNumber = "DAQ-12345";
        var connects = new List<int>();
        var phases = new List<DuplicateCheckPhase>();
        var overWifi = ConnectedDevice("wifi", serialNumber);
        var overUsb = ConnectedDevice("usb", serialNumber);
        using var registry = RegistryReturning(connects, overUsb);
        registry.Register(overWifi, WifiInfo(serialNumber));
        registry.DuplicatePolicy = check =>
        {
            phases.Add(check.Phase);
            return DuplicateDeviceAction.KeepExisting;
        };

        // Serial-port discovery could not read a serial number, so the duplicate only becomes
        // visible once the device has answered its first status message.
        var result = await registry.ConnectAsync(UsbInfo(serialNumber: "", portName: "COM3"));

        Assert.Equal(DeviceRegistrationOutcome.DuplicateRejected, result.Outcome);
        Assert.Same(overWifi, result.Device);
        Assert.Equal(1, registry.Count);
        Assert.Single(connects);
        Assert.Equal(DuplicateCheckPhase.AfterConnect, Assert.Single(phases));
        // The connection opened only to discover it was redundant is disposed by the registry.
        Assert.False(overUsb.IsConnected);
    }

    [Fact]
    public async Task ConnectAsync_PostConnectCheck_ReportsTheConnectedDeviceToThePolicy()
    {
        const string serialNumber = "DAQ-12345";
        var connects = new List<int>();
        var newDevice = ConnectedDevice("usb", serialNumber);
        using var registry = RegistryReturning(connects, newDevice);
        registry.Register(ConnectedDevice("wifi", serialNumber), WifiInfo(serialNumber));
        DuplicateDeviceCheck? seen = null;
        registry.DuplicatePolicy = check =>
        {
            seen = check;
            return DuplicateDeviceAction.KeepExisting;
        };

        await registry.ConnectAsync(UsbInfo(serialNumber: "", portName: "COM3"));

        Assert.NotNull(seen);
        Assert.Same(newDevice, seen!.NewDevice);
        Assert.Equal(ConnectionType.Serial, seen.NewConnectionType);
        Assert.Equal(serialNumber, seen.NewIdentity.SerialNumber);
    }

    #endregion

    #region Duplicate policy actions

    [Fact]
    public async Task ConnectAsync_SwitchToNew_ReplacesTheExistingConnection()
    {
        const string serialNumber = "DAQ-12345";
        var connects = new List<int>();
        var overUsb = ConnectedDevice("usb", serialNumber);
        var overWifi = ConnectedDevice("wifi", serialNumber);
        var removals = new List<DeviceRemovedEventArgs>();
        using var registry = RegistryReturning(connects, overUsb, overWifi);
        registry.DeviceRemoved += (_, e) => removals.Add(e);
        registry.DuplicatePolicy = _ => DuplicateDeviceAction.SwitchToNew;

        await registry.ConnectAsync(UsbInfo(serialNumber));
        var result = await registry.ConnectAsync(WifiInfo(serialNumber));

        Assert.Equal(DeviceRegistrationOutcome.ReplacedExisting, result.Outcome);
        Assert.True(result.IsRegistered);
        Assert.Same(overWifi, result.Device);
        Assert.Equal(1, registry.Count);
        Assert.False(overUsb.IsConnected);

        var removal = Assert.Single(removals);
        Assert.Equal(DeviceRemovalReason.Replaced, removal.Reason);
        Assert.Same(overUsb, removal.Registration.Device);
    }

    [Fact]
    public async Task ConnectAsync_SwitchToNew_WhenTheNewConnectionFails_KeepsTheExistingOne()
    {
        const string serialNumber = "DAQ-12345";
        var overUsb = ConnectedDevice("usb", serialNumber);
        var attempt = 0;
        using var registry = new DaqifiDeviceRegistry((_, _, _) =>
        {
            attempt++;
            return attempt == 1
                ? Task.FromResult(overUsb)
                : Task.FromException<DaqifiDevice>(
                    new TimeoutException("TCP connect to 192.0.2.1:9760 timed out after 5s."));
        });
        var removals = new List<DeviceRemovedEventArgs>();
        registry.DeviceRemoved += (_, e) => removals.Add(e);

        await registry.ConnectAsync(UsbInfo(serialNumber));
        registry.DuplicatePolicy = _ => DuplicateDeviceAction.SwitchToNew;

        // The replacement transport refuses the connection — the real case that caught this: a
        // device that answers UDP discovery but is not listening on its TCP data port. Switching
        // to it must not cost the caller the healthy USB session they already had.
        await Assert.ThrowsAsync<TimeoutException>(() => registry.ConnectAsync(WifiInfo(serialNumber)));

        Assert.Equal(1, registry.Count);
        Assert.True(overUsb.IsConnected);
        Assert.Same(overUsb, Assert.Single(registry.Devices).Device);
        Assert.Empty(removals);
    }

    [Fact]
    public async Task ConnectAsync_SwitchToNew_DropsTheOldConnectionOnlyAfterTheNewOneIsOpen()
    {
        const string serialNumber = "DAQ-12345";
        var overUsb = ConnectedDevice("usb", serialNumber);
        var overWifi = ConnectedDevice("wifi", serialNumber);
        var policyCalls = new List<DuplicateCheckPhase>();
        var connectedWhileOldStillRegistered = false;
        var connects = new List<int>();
        using var registry = RegistryReturning(connects, overUsb, overWifi);

        await registry.ConnectAsync(UsbInfo(serialNumber));

        registry.DuplicatePolicy = check =>
        {
            policyCalls.Add(check.Phase);
            return DuplicateDeviceAction.SwitchToNew;
        };
        registry.DeviceRemoved += (_, _) =>
        {
            // At the moment the old registration is dropped, the replacement must already be live.
            connectedWhileOldStillRegistered = overWifi.IsConnected;
        };

        var result = await registry.ConnectAsync(WifiInfo(serialNumber));

        Assert.Equal(DeviceRegistrationOutcome.ReplacedExisting, result.Outcome);
        Assert.True(connectedWhileOldStillRegistered);
        Assert.False(overUsb.IsConnected);
        Assert.Equal(1, registry.Count);
        // Asked once, pre-connect — a consumer prompting its user must not see two dialogs.
        Assert.Equal(DuplicateCheckPhase.BeforeConnect, Assert.Single(policyCalls));
    }

    [Fact]
    public async Task ConnectAsync_Cancel_LeavesTheExistingConnectionAndRegistersNothing()
    {
        const string serialNumber = "DAQ-12345";
        var connects = new List<int>();
        var overUsb = ConnectedDevice("usb", serialNumber);
        using var registry = RegistryReturning(connects, overUsb, ConnectedDevice("wifi", serialNumber));
        registry.DuplicatePolicy = _ => DuplicateDeviceAction.Cancel;

        await registry.ConnectAsync(UsbInfo(serialNumber));
        var result = await registry.ConnectAsync(WifiInfo(serialNumber));

        Assert.Equal(DeviceRegistrationOutcome.Canceled, result.Outcome);
        Assert.Null(result.Registration);
        Assert.Null(result.Device);
        Assert.Null(result.Key);
        Assert.Equal(1, registry.Count);
        Assert.True(overUsb.IsConnected);
    }

    [Fact]
    public void Register_PolicyReturnsUndefinedAction_FallsBackToKeepingTheExistingDevice()
    {
        const string serialNumber = "DAQ-12345";
        using var registry = new DaqifiDeviceRegistry();
        var existing = ConnectedDevice("usb", serialNumber);
        var duplicate = ConnectedDevice("wifi", serialNumber);
        registry.Register(existing);
        registry.DuplicatePolicy = _ => (DuplicateDeviceAction)42;

        var result = registry.Register(duplicate);

        Assert.Equal(DeviceRegistrationOutcome.DuplicateRejected, result.Outcome);
        Assert.Same(existing, result.Device);
        Assert.False(duplicate.IsConnected);
    }

    [Fact]
    public void Register_PolicyThrows_PropagatesAndStillDisposesTheDevice()
    {
        const string serialNumber = "DAQ-12345";
        using var registry = new DaqifiDeviceRegistry();
        registry.Register(ConnectedDevice("usb", serialNumber));
        var duplicate = ConnectedDevice("wifi", serialNumber);
        registry.DuplicatePolicy = _ => throw new InvalidOperationException("policy failed");

        Assert.Throws<InvalidOperationException>(() => registry.Register(duplicate));

        Assert.Equal(1, registry.Count);
        Assert.False(duplicate.IsConnected);
    }

    #endregion

    #region Registration and keys

    [Fact]
    public void Register_ConnectedDevice_IsFiledUnderItsIdentityKey()
    {
        using var registry = new DaqifiDeviceRegistry();
        var device = ConnectedDevice("usb", "DAQ-12345");

        var result = registry.Register(device, UsbInfo("DAQ-12345"));

        Assert.Equal(DeviceRegistrationOutcome.Registered, result.Outcome);
        Assert.Equal("sn:daq-12345", result.Key);
        Assert.True(registry.TryGetDevice("sn:daq-12345", out var found));
        Assert.Same(device, found);
        Assert.Equal(ConnectionType.Serial, result.Registration!.ConnectionType);
    }

    [Fact]
    public void Register_CallerSuppliedKey_IsUsedForLookups()
    {
        using var registry = new DaqifiDeviceRegistry();
        var device = ConnectedDevice("usb", "DAQ-12345");

        var result = registry.Register(device, UsbInfo("DAQ-12345"), key: "serial:COM3");

        Assert.Equal("serial:COM3", result.Key);
        Assert.True(registry.TryGet("serial:COM3", out var registration));
        Assert.Same(device, registration!.Device);
    }

    [Fact]
    public void Register_SameKeyTwice_IsTreatedAsADuplicate()
    {
        using var registry = new DaqifiDeviceRegistry();
        var first = ConnectedDevice("first");
        var second = ConnectedDevice("second");
        registry.Register(first, key: "my-device");

        var result = registry.Register(second, key: "my-device");

        Assert.Equal(DeviceRegistrationOutcome.DuplicateRejected, result.Outcome);
        Assert.Same(first, result.Device);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Register_DeviceWithoutIdentity_GetsAMintedUniqueKey()
    {
        using var registry = new DaqifiDeviceRegistry();

        var first = registry.Register(ConnectedDevice("first"));
        var second = registry.Register(ConnectedDevice("second"));

        Assert.Equal(2, registry.Count);
        Assert.NotEqual(first.Key, second.Key);
        Assert.All(new[] { first.Key, second.Key }, key => Assert.False(string.IsNullOrEmpty(key)));
    }

    [Fact]
    public void Register_UsesDiscoveryIdentityWhenMetadataHasNotFilledItInYet()
    {
        using var registry = new DaqifiDeviceRegistry();

        var result = registry.Register(ConnectedDevice("usb"), UsbInfo("DAQ-12345", locationKey: "Port_#0001"));

        Assert.Equal("DAQ-12345", result.Registration!.Identity.SerialNumber);
        Assert.Equal("Port_#0001", result.Registration.Identity.LocationKey);
    }

    [Fact]
    public void Register_SameDeviceInstanceTwice_IsANoOpAndKeepsItAlive()
    {
        using var registry = new DaqifiDeviceRegistry();
        var device = ConnectedDevice("usb", "DAQ-12345");
        var first = registry.Register(device);

        var again = registry.Register(device);

        // The device must not be treated as a duplicate of itself: disposing it would leave the
        // registry holding a dead handle.
        Assert.Equal(DeviceRegistrationOutcome.Registered, again.Outcome);
        Assert.Same(first.Registration, again.Registration);
        Assert.Equal(1, registry.Count);
        Assert.True(device.IsConnected);
    }

    [Fact]
    public void Register_SameDeviceInstanceUnderAnotherKey_ReturnsTheOriginalRegistration()
    {
        using var registry = new DaqifiDeviceRegistry();
        var device = ConnectedDevice("anonymous");
        var first = registry.Register(device, key: "first-key");

        var again = registry.Register(device, key: "second-key");

        Assert.Equal("first-key", again.Key);
        Assert.Same(first.Registration, again.Registration);
        Assert.Equal(1, registry.Count);
        Assert.True(device.IsConnected);
    }

    [Fact]
    public void Register_NullDevice_Throws()
    {
        using var registry = new DaqifiDeviceRegistry();

        Assert.Throws<ArgumentNullException>(() => registry.Register(null!));
    }

    [Fact]
    public void Register_DisconnectedDevice_ThrowsWithoutTakingOwnership()
    {
        using var registry = new DaqifiDeviceRegistry();
        var device = new DaqifiDevice("never-connected");

        Assert.Throws<ArgumentException>(() => registry.Register(device));
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task ConnectAsync_NullDeviceInfo_Throws()
    {
        using var registry = new DaqifiDeviceRegistry();

        await Assert.ThrowsAsync<ArgumentNullException>(() => registry.ConnectAsync(null!));
    }

    #endregion

    #region Lookups

    [Fact]
    public void TryGet_UnknownOrBlankKey_ReturnsFalse()
    {
        using var registry = new DaqifiDeviceRegistry();

        Assert.False(registry.TryGet("nope", out var missing));
        Assert.Null(missing);
        Assert.False(registry.TryGetDevice("", out var blank));
        Assert.Null(blank);
    }

    [Fact]
    public void Find_MatchesByIdentity_AndIgnoresEmptyIdentities()
    {
        using var registry = new DaqifiDeviceRegistry();
        var device = ConnectedDevice("usb", "DAQ-12345");
        registry.Register(device);

        Assert.Same(device, registry.Find(DeviceIdentity.Create("daq-12345"))!.Device);
        Assert.Null(registry.Find(DeviceIdentity.Create("DAQ-99999")));
        Assert.Null(registry.Find(DeviceIdentity.Empty));
    }

    [Fact]
    public void Devices_ReturnsAnIndependentSnapshot()
    {
        using var registry = new DaqifiDeviceRegistry();
        registry.Register(ConnectedDevice("first", "DAQ-1"));

        var snapshot = registry.Devices;
        registry.Register(ConnectedDevice("second", "DAQ-2"));

        Assert.Single(snapshot);
        Assert.Equal(2, registry.Count);
    }

    #endregion

    #region Removal, pruning, and disposal

    [Fact]
    public void Remove_ByKey_DisconnectsDisposesAndAnnounces()
    {
        using var registry = new DaqifiDeviceRegistry();
        var device = ConnectedDevice("usb", "DAQ-12345");
        var removals = new List<DeviceRemovedEventArgs>();
        registry.DeviceRemoved += (_, e) => removals.Add(e);
        var key = registry.Register(device).Key!;

        Assert.True(registry.Remove(key));

        Assert.Equal(0, registry.Count);
        Assert.False(device.IsConnected);
        Assert.Equal(DeviceRemovalReason.Removed, Assert.Single(removals).Reason);
        Assert.False(registry.Remove(key));
    }

    [Fact]
    public void Remove_ByDevice_RemovesOnlyThatInstance()
    {
        using var registry = new DaqifiDeviceRegistry();
        var first = ConnectedDevice("first", "DAQ-1");
        var second = ConnectedDevice("second", "DAQ-2");
        registry.Register(first);
        registry.Register(second);

        Assert.True(registry.Remove(first));

        Assert.Equal(1, registry.Count);
        Assert.False(first.IsConnected);
        Assert.True(second.IsConnected);
        Assert.False(registry.Remove(ConnectedDevice("unregistered")));
    }

    [Fact]
    public void PruneDisconnected_DropsStaleRegistrations()
    {
        using var registry = new DaqifiDeviceRegistry();
        var dropped = ConnectedDevice("dropped", "DAQ-1");
        var live = ConnectedDevice("live", "DAQ-2");
        registry.Register(dropped);
        registry.Register(live);
        var removals = new List<DeviceRemovedEventArgs>();
        registry.DeviceRemoved += (_, e) => removals.Add(e);

        // The device dropped off the bus behind the registry's back.
        dropped.Disconnect();

        Assert.Equal(1, registry.PruneDisconnected());
        Assert.Equal(1, registry.Count);
        Assert.Equal(DeviceRemovalReason.Disconnected, Assert.Single(removals).Reason);
        Assert.Equal(0, registry.PruneDisconnected());
    }

    [Fact]
    public async Task ConnectAsync_StaleRegistrationUnderTheSameKey_IsPrunedAndReconnected()
    {
        var connects = new List<int>();
        var dropped = ConnectedDevice("dropped", "DAQ-12345");
        var reconnected = ConnectedDevice("reconnected", "DAQ-12345");
        using var registry = RegistryReturning(connects, dropped, reconnected);

        await registry.ConnectAsync(UsbInfo("DAQ-12345"), key: "serial:COM3");
        dropped.Disconnect();
        var result = await registry.ConnectAsync(UsbInfo("DAQ-12345"), key: "serial:COM3");

        Assert.Equal(DeviceRegistrationOutcome.Registered, result.Outcome);
        Assert.Same(reconnected, result.Device);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Clear_RemovesAndDisposesEverything()
    {
        using var registry = new DaqifiDeviceRegistry();
        var first = ConnectedDevice("first", "DAQ-1");
        var second = ConnectedDevice("second", "DAQ-2");
        registry.Register(first);
        registry.Register(second);
        var removals = new List<DeviceRemovedEventArgs>();
        registry.DeviceRemoved += (_, e) => removals.Add(e);

        registry.Clear();

        Assert.Equal(0, registry.Count);
        Assert.False(first.IsConnected);
        Assert.False(second.IsConnected);
        Assert.Equal(2, removals.Count);
        Assert.All(removals, r => Assert.Equal(DeviceRemovalReason.Cleared, r.Reason));
    }

    [Fact]
    public void Dispose_DisposesDevicesWithoutAnnouncingRemovals()
    {
        var registry = new DaqifiDeviceRegistry();
        var device = ConnectedDevice("usb", "DAQ-12345");
        registry.Register(device);
        var removals = 0;
        registry.DeviceRemoved += (_, _) => removals++;

        registry.Dispose();
        registry.Dispose();

        Assert.False(device.IsConnected);
        Assert.Equal(0, removals);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public async Task DisposedRegistry_RejectsFurtherRegistrations()
    {
        var registry = new DaqifiDeviceRegistry();
        registry.Dispose();

        Assert.Throws<ObjectDisposedException>(() => registry.Register(ConnectedDevice("usb", "DAQ-1")));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => registry.ConnectAsync(UsbInfo("DAQ-1")));
    }

    #endregion

    #region Events

    [Fact]
    public void DeviceAdded_IsRaisedWithTheRegistration()
    {
        using var registry = new DaqifiDeviceRegistry();
        var device = ConnectedDevice("usb", "DAQ-12345");
        var added = new List<DeviceRegisteredEventArgs>();
        registry.DeviceAdded += (_, e) => added.Add(e);

        registry.Register(device, UsbInfo("DAQ-12345"));

        var registration = Assert.Single(added).Registration;
        Assert.Same(device, registration.Device);
        Assert.Equal("DAQ-12345", registration.Identity.SerialNumber);
    }

    [Fact]
    public void FaultyEventSubscriber_DoesNotFailTheRegistryOperation()
    {
        using var registry = new DaqifiDeviceRegistry();
        registry.DeviceAdded += (_, _) => throw new InvalidOperationException("subscriber blew up");
        registry.DeviceRemoved += (_, _) => throw new InvalidOperationException("subscriber blew up");
        var device = ConnectedDevice("usb", "DAQ-12345");

        var key = registry.Register(device).Key!;
        Assert.Equal(1, registry.Count);

        Assert.True(registry.Remove(key));
        Assert.Equal(0, registry.Count);
    }

    #endregion

    #region Concurrency

    [Fact]
    public async Task Register_IsSafeUnderConcurrency()
    {
        const int deviceCount = 50;
        using var registry = new DaqifiDeviceRegistry();
        var devices = Enumerable.Range(0, deviceCount)
            .Select(i => ConnectedDevice($"device-{i}", $"DAQ-{i}"))
            .ToList();

        await Task.WhenAll(devices.Select(d => Task.Run(() => registry.Register(d))));

        Assert.Equal(deviceCount, registry.Count);
        Assert.Equal(deviceCount, registry.Devices.Select(r => r.Key).Distinct().Count());
    }

    [Fact]
    public async Task Register_ConcurrentDuplicates_LeaveExactlyOneRegistration()
    {
        const string serialNumber = "DAQ-12345";
        using var registry = new DaqifiDeviceRegistry();
        var devices = Enumerable.Range(0, 20)
            .Select(i => ConnectedDevice($"device-{i}", serialNumber))
            .ToList();

        var results = await Task.WhenAll(devices.Select(d => Task.Run(() => registry.Register(d))));

        Assert.Equal(1, registry.Count);
        Assert.Single(results, r => r.Outcome == DeviceRegistrationOutcome.Registered);
        Assert.Equal(devices.Count - 1, results.Count(r => r.Outcome == DeviceRegistrationOutcome.DuplicateRejected));
        Assert.Single(devices, d => d.IsConnected);
    }

    #endregion
}
