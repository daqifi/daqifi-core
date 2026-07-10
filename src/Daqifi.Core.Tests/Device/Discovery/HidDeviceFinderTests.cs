using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device.Discovery;

namespace Daqifi.Core.Tests.Device.Discovery;

public class HidDeviceFinderTests
{
    [Fact]
    public async Task DiscoverAsync_UsesDefaultBootloaderFilters()
    {
        var enumerator = new FakeHidDeviceEnumerator([
            new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN1", "DAQiFi Bootloader")
        ]);

        using var finder = new HidDeviceFinder(enumerator);

        var devices = (await finder.DiscoverAsync()).ToList();

        Assert.Equal(HidDeviceFinder.DefaultVendorId, enumerator.LastVendorId);
        Assert.Equal(HidDeviceFinder.DefaultProductId, enumerator.LastProductId);
        Assert.Single(devices);

        var device = Assert.IsType<DeviceInfo>(devices[0]);
        Assert.Equal(ConnectionType.Hid, device.ConnectionType);
        Assert.Equal("DAQiFi Bootloader", device.Name);
        Assert.Equal("SN1", device.SerialNumber);
        Assert.Equal("path-1", device.DevicePath);
    }

    [Fact]
    public async Task DiscoverAsync_WithCustomFilters_UsesProvidedFilterValues()
    {
        var enumerator = new FakeHidDeviceEnumerator();
        using var finder = new HidDeviceFinder(enumerator);

        finder.VendorIdFilter = 0x1234;
        finder.ProductIdFilter = 0x9999;

        await finder.DiscoverAsync();

        Assert.Equal(0x1234, enumerator.LastVendorId);
        Assert.Equal(0x9999, enumerator.LastProductId);
    }

    [Fact]
    public async Task DiscoverAsync_RaisesDeviceDiscovered_ForEachDevice()
    {
        var enumerator = new FakeHidDeviceEnumerator([
            new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN1", "Bootloader A"),
            new HidDeviceInfo(0x04D8, 0x003C, "path-2", "SN2", "Bootloader B")
        ]);

        using var finder = new HidDeviceFinder(enumerator);
        var discovered = new List<IDeviceInfo>();
        finder.DeviceDiscovered += (_, args) => discovered.Add(args.DeviceInfo);

        await finder.DiscoverAsync();

        Assert.Equal(2, discovered.Count);
        Assert.Equal("path-1", discovered[0].DevicePath);
        Assert.Equal("path-2", discovered[1].DevicePath);
    }

    [Fact]
    public async Task DiscoverAsync_RaisesDiscoveryCompletedEvent()
    {
        var enumerator = new FakeHidDeviceEnumerator();
        using var finder = new HidDeviceFinder(enumerator);

        var eventRaised = false;
        finder.DiscoveryCompleted += (_, _) => eventRaised = true;

        await finder.DiscoverAsync(TimeSpan.FromMilliseconds(100));

        Assert.True(eventRaised);
    }

    [Fact]
    public async Task DiscoverAsync_PopulatesLocationKey_FromInjectedProvider()
    {
        var enumerator = new FakeHidDeviceEnumerator([
            new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN1", "DAQiFi Bootloader")
        ]);
        var locationProvider = new RecordingUsbLocationProvider(
            path => path == "path-1" ? "Port_#0001.Hub_#0001" : null);

        using var finder = new HidDeviceFinder(enumerator, locationProvider);

        var devices = (await finder.DiscoverAsync()).ToList();

        var device = Assert.IsType<DeviceInfo>(devices[0]);
        Assert.Equal("Port_#0001.Hub_#0001", device.LocationKey);
        Assert.Contains("path-1", locationProvider.Requests);
    }

    [Fact]
    public async Task DiscoverAsync_WithUnresolvableDevicePath_LocationKeyIsNull()
    {
        // The default (no explicit provider) constructor resolves the platform provider, which
        // on any platform returns null for a path this malformed — proving discovery never
        // throws when location resolution can't classify the device.
        var enumerator = new FakeHidDeviceEnumerator([
            new HidDeviceInfo(0x04D8, 0x003C, "not-a-real-device-interface-path", "SN1", "DAQiFi Bootloader")
        ]);

        using var finder = new HidDeviceFinder(enumerator);

        var devices = (await finder.DiscoverAsync()).ToList();

        Assert.Null(devices[0].LocationKey);
    }

    [Fact]
    public async Task DiscoverAsync_WithThrowingLocationProvider_DoesNotAbortDiscovery()
    {
        // A misbehaving custom IUsbLocationProvider must never take down the whole enumeration —
        // location is enrichment metadata, not identification. Mirrors
        // SerialDeviceFinder's DiscoverAsync_WithThrowingDescriptorProvider_DoesNotAbortDiscovery.
        var enumerator = new FakeHidDeviceEnumerator([
            new HidDeviceInfo(0x04D8, 0x003C, "path-1", "SN1", "Bootloader A"),
            new HidDeviceInfo(0x04D8, 0x003C, "path-2", "SN2", "Bootloader B")
        ]);
        var throwingProvider = new RecordingUsbLocationProvider(
            path => path == "path-1" ? throw new InvalidOperationException("simulated provider failure") : "loc-2");

        using var finder = new HidDeviceFinder(enumerator, throwingProvider);

        var devices = (await finder.DiscoverAsync()).ToList();

        Assert.Equal(2, devices.Count);
        Assert.Null(devices[0].LocationKey);
        Assert.Equal("loc-2", devices[1].LocationKey);
    }

    [Fact]
    public void HidDeviceFinder_Dispose_DoesNotThrow()
    {
        var enumerator = new FakeHidDeviceEnumerator();
        var finder = new HidDeviceFinder(enumerator);

        finder.Dispose();
    }

    [Fact]
    public async Task HidDeviceFinder_AfterDispose_ThrowsObjectDisposedException()
    {
        var enumerator = new FakeHidDeviceEnumerator();
        var finder = new HidDeviceFinder(enumerator);
        finder.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => finder.DiscoverAsync());
    }

    private sealed class FakeHidDeviceEnumerator : IHidDeviceEnumerator
    {
        private readonly IReadOnlyList<HidDeviceInfo> _devices;

        public FakeHidDeviceEnumerator(IReadOnlyList<HidDeviceInfo>? devices = null)
        {
            _devices = devices ?? Array.Empty<HidDeviceInfo>();
        }

        public int? LastVendorId { get; private set; }
        public int? LastProductId { get; private set; }

        public Task<IReadOnlyList<HidDeviceInfo>> EnumerateAsync(
            int? vendorId = null,
            int? productId = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            LastVendorId = vendorId;
            LastProductId = productId;
            return Task.FromResult(_devices);
        }
    }

    private sealed class RecordingUsbLocationProvider : IUsbLocationProvider
    {
        private readonly Func<string, string?> _resolver;

        public RecordingUsbLocationProvider(Func<string, string?> resolver) => _resolver = resolver;

        public List<string> Requests { get; } = [];

        public string? GetLocationKey(string portNameOrDevicePath)
        {
            Requests.Add(portNameOrDevicePath);
            return _resolver(portNameOrDevicePath);
        }
    }
}
