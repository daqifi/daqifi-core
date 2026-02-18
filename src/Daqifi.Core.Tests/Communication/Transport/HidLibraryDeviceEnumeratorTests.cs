using Daqifi.Core.Communication.Transport;

namespace Daqifi.Core.Tests.Communication.Transport;

public class HidLibraryDeviceEnumeratorTests
{
    [Fact]
    public async Task EnumerateAsync_WithoutFilters_ReturnsAllDevices()
    {
        var platform = new FakeHidPlatform(
        [
            new FakeHidTransportDevice(0x04D8, 0x003C, "path-a", "SN-A", "Bootloader A"),
            new FakeHidTransportDevice(0x1234, 0x5678, "path-b", "SN-B", "Other")
        ]);

        var enumerator = new HidLibraryDeviceEnumerator(platform);

        var devices = await enumerator.EnumerateAsync();

        Assert.Equal(2, devices.Count);
        Assert.Equal("path-a", devices[0].DevicePath);
        Assert.Equal("path-b", devices[1].DevicePath);
        Assert.Equal(1, platform.EnumerateCalls);
    }

    [Fact]
    public async Task EnumerateAsync_WithVendorFilter_ReturnsMatchingDevicesOnly()
    {
        var platform = new FakeHidPlatform(
        [
            new FakeHidTransportDevice(0x04D8, 0x003C, "path-a", "SN-A", "Bootloader A"),
            new FakeHidTransportDevice(0x1234, 0x003C, "path-b", "SN-B", "Other")
        ]);

        var enumerator = new HidLibraryDeviceEnumerator(platform);

        var devices = await enumerator.EnumerateAsync(vendorId: 0x04D8);

        Assert.Single(devices);
        Assert.Equal(0x04D8, devices[0].VendorId);
        Assert.Equal("path-a", devices[0].DevicePath);
    }

    [Fact]
    public async Task EnumerateAsync_WithProductFilter_ReturnsMatchingDevicesOnly()
    {
        var platform = new FakeHidPlatform(
        [
            new FakeHidTransportDevice(0x04D8, 0x003C, "path-a", "SN-A", "Bootloader A"),
            new FakeHidTransportDevice(0x04D8, 0x4444, "path-b", "SN-B", "Other")
        ]);

        var enumerator = new HidLibraryDeviceEnumerator(platform);

        var devices = await enumerator.EnumerateAsync(productId: 0x003C);

        Assert.Single(devices);
        Assert.Equal(0x003C, devices[0].ProductId);
        Assert.Equal("path-a", devices[0].DevicePath);
    }

    [Fact]
    public async Task EnumerateAsync_WithVendorAndProductFilters_ReturnsIntersection()
    {
        var platform = new FakeHidPlatform(
        [
            new FakeHidTransportDevice(0x04D8, 0x003C, "path-a", "SN-A", "Bootloader A"),
            new FakeHidTransportDevice(0x04D8, 0x4444, "path-b", "SN-B", "Other"),
            new FakeHidTransportDevice(0x1234, 0x003C, "path-c", "SN-C", "Other")
        ]);

        var enumerator = new HidLibraryDeviceEnumerator(platform);

        var devices = await enumerator.EnumerateAsync(vendorId: 0x04D8, productId: 0x003C);

        Assert.Single(devices);
        Assert.Equal("path-a", devices[0].DevicePath);
    }

    [Fact]
    public async Task EnumerateAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        var platform = new FakeHidPlatform([]);
        var enumerator = new HidLibraryDeviceEnumerator(platform);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => enumerator.EnumerateAsync(cancellationToken: cts.Token));
    }

    private sealed class FakeHidPlatform : IHidPlatform
    {
        private readonly IReadOnlyList<IHidTransportDevice> _devices;

        public FakeHidPlatform(IReadOnlyList<IHidTransportDevice> devices)
        {
            _devices = devices;
        }

        public int EnumerateCalls { get; private set; }

        public IReadOnlyList<IHidTransportDevice> EnumerateDevices()
        {
            EnumerateCalls++;
            return _devices;
        }
    }

    private sealed class FakeHidTransportDevice : IHidTransportDevice
    {
        public FakeHidTransportDevice(
            int vendorId,
            int productId,
            string devicePath,
            string? serialNumber,
            string? productName)
        {
            VendorId = vendorId;
            ProductId = productId;
            DevicePath = devicePath;
            SerialNumber = serialNumber;
            ProductName = productName;
        }

        public int VendorId { get; }
        public int ProductId { get; }
        public string DevicePath { get; }
        public string? SerialNumber { get; }
        public string? ProductName { get; }
        public bool IsConnected { get; private set; }

        public void Open() => IsConnected = true;

        public void Close() => IsConnected = false;

        public bool Write(byte[] data, int timeoutMs) => true;

        public Task<bool> WriteAsync(byte[] data, int timeoutMs) => Task.FromResult(true);

        public HidTransportReadResult Read(int timeoutMs)
            => HidTransportReadResult.Success(Array.Empty<byte>());

        public Task<HidTransportReadResult> ReadAsync(int timeoutMs)
            => Task.FromResult(HidTransportReadResult.Success(Array.Empty<byte>()));
    }
}
