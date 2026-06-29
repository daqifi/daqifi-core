using System.IO;
using Daqifi.Core.Communication.Transport;

namespace Daqifi.Core.Tests.Communication.Transport;

public class HidLibraryTransportTests
{
    [Fact]
    public void Constructor_DefaultReadTimeout_IsTenSeconds()
    {
        var platform = new FakeHidPlatform([]);
        using var transport = new HidLibraryTransport(platform);

        Assert.Equal(TimeSpan.FromSeconds(10), transport.ReadTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), transport.WriteTimeout);
    }

    [Fact]
    public async Task ConnectAsync_WithMatchingDevice_OpensAndStoresMetadata()
    {
        var device = new FakeHidTransportDevice(0x04D8, 0x003C, "path-a", "SN-A");
        var platform = new FakeHidPlatform([device]);

        using var transport = new HidLibraryTransport(platform);

        await transport.ConnectAsync(0x04D8, 0x003C);

        Assert.True(transport.IsConnected);
        Assert.Equal(0x04D8, transport.VendorId);
        Assert.Equal(0x003C, transport.ProductId);
        Assert.Equal("SN-A", transport.SerialNumber);
        Assert.Equal("path-a", transport.DevicePath);
        Assert.Equal(1, device.OpenCount);
    }

    [Fact]
    public async Task ConnectAsync_ByDefault_OpensDeviceShared()
    {
        var device = new FakeHidTransportDevice(0x04D8, 0x003C, "path-a", "SN-A");
        var platform = new FakeHidPlatform([device]);

        using var transport = new HidLibraryTransport(platform);

        await transport.ConnectAsync(0x04D8, 0x003C);

        // ExclusiveAccess defaults false so non-bootloader HID consumers keep a shared open.
        Assert.False(transport.ExclusiveAccess);
        Assert.False(device.LastOpenExclusive);
    }

    [Fact]
    public async Task ConnectAsync_WhenExclusiveAccessSet_OpensDeviceExclusively()
    {
        var device = new FakeHidTransportDevice(0x04D8, 0x003C, "path-a", "SN-A");
        var platform = new FakeHidPlatform([device]);

        using var transport = new HidLibraryTransport(platform) { ExclusiveAccess = true };

        await transport.ConnectAsync(0x04D8, 0x003C);

        // A2: the bootloader flash must hold the HID handle exclusively. The flag is threaded to the
        // device open so the stray-write guard (and discovery lockout) is actually requested.
        Assert.True(device.LastOpenExclusive);
    }

    [Fact]
    public async Task ConnectAsync_WithSerialFilter_SelectsMatchingDevice()
    {
        var first = new FakeHidTransportDevice(0x04D8, 0x003C, "path-a", "SN-A");
        var second = new FakeHidTransportDevice(0x04D8, 0x003C, "path-b", "SN-B");
        var platform = new FakeHidPlatform([first, second]);

        using var transport = new HidLibraryTransport(platform);

        await transport.ConnectAsync(0x04D8, 0x003C, serialNumber: "SN-B");

        Assert.True(transport.IsConnected);
        Assert.Equal("SN-B", transport.SerialNumber);
        Assert.Equal("path-b", transport.DevicePath);
        Assert.Equal(0, first.OpenCount);
        Assert.Equal(1, second.OpenCount);
    }

    [Fact]
    public async Task ConnectAsync_WhenAlreadyConnected_DoesNotOpenAnotherDevice()
    {
        var first = new FakeHidTransportDevice(0x04D8, 0x003C, "path-a", "SN-A");
        var second = new FakeHidTransportDevice(0x04D8, 0x003C, "path-b", "SN-B");
        var platform = new FakeHidPlatform([first, second]);

        using var transport = new HidLibraryTransport(platform);
        await transport.ConnectAsync(0x04D8, 0x003C);
        await transport.ConnectAsync(0x04D8, 0x003C);

        Assert.Equal(1, platform.EnumerateCalls);
        Assert.Equal(1, first.OpenCount);
        Assert.Equal(0, second.OpenCount);
    }

    [Fact]
    public async Task ConnectAsync_WhenNoMatchingDevice_ThrowsIOException()
    {
        var platform = new FakeHidPlatform([]);
        using var transport = new HidLibraryTransport(platform);

        await Assert.ThrowsAsync<IOException>(() => transport.ConnectAsync(0x04D8, 0x003C));
    }

    [Fact]
    public async Task ConnectByPathAsync_WithMatchingPath_OpensThatExactDevice()
    {
        // Two identical bootloaders (same VID/PID, no serial); only the path tells them apart.
        var deviceA = new FakeHidTransportDevice(0x04D8, 0x003C, "path-a", string.Empty);
        var deviceB = new FakeHidTransportDevice(0x04D8, 0x003C, "path-b", string.Empty);
        var platform = new FakeHidPlatform([deviceA, deviceB]);

        using var transport = new HidLibraryTransport(platform);

        await transport.ConnectByPathAsync("path-b");

        Assert.True(transport.IsConnected);
        Assert.Equal("path-b", transport.DevicePath);
        Assert.Equal(0, deviceA.OpenCount);
        Assert.Equal(1, deviceB.OpenCount);
    }

    [Fact]
    public async Task ConnectByPathAsync_MatchesPathCaseInsensitively()
    {
        // Windows HID device paths are case-insensitive; casing can vary across enumerations.
        var device = new FakeHidTransportDevice(0x04D8, 0x003C, "PATH-A", string.Empty);
        var platform = new FakeHidPlatform([device]);
        using var transport = new HidLibraryTransport(platform);

        await transport.ConnectByPathAsync("path-a");

        Assert.True(transport.IsConnected);
        Assert.Equal(1, device.OpenCount);
    }

    [Fact]
    public async Task ConnectByPathAsync_WhenNoDeviceMatchesPath_ThrowsIOException()
    {
        var device = new FakeHidTransportDevice(0x04D8, 0x003C, "path-a", "SN-A");
        var platform = new FakeHidPlatform([device]);
        using var transport = new HidLibraryTransport(platform);

        await Assert.ThrowsAsync<IOException>(() => transport.ConnectByPathAsync("path-z"));
        Assert.False(transport.IsConnected);
        Assert.Equal(0, device.OpenCount);
    }

    [Fact]
    public async Task ConnectByPathAsync_WithEmptyPath_ThrowsArgumentException()
    {
        var platform = new FakeHidPlatform([]);
        using var transport = new HidLibraryTransport(platform);

        await Assert.ThrowsAsync<ArgumentException>(() => transport.ConnectByPathAsync("  "));
    }

    [Fact]
    public async Task WriteAsync_WhenNotConnected_ThrowsInvalidOperationException()
    {
        var platform = new FakeHidPlatform([]);
        using var transport = new HidLibraryTransport(platform);

        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.WriteAsync([0x01]));
    }

    [Fact]
    public async Task WriteAsync_WhenWriteFails_ThrowsIOException()
    {
        var device = new FakeHidTransportDevice(0x04D8, 0x003C, "path-a", "SN-A")
        {
            NextWriteResult = false
        };

        var platform = new FakeHidPlatform([device]);
        using var transport = new HidLibraryTransport(platform);
        await transport.ConnectAsync(0x04D8, 0x003C);

        await Assert.ThrowsAsync<IOException>(() => transport.WriteAsync([0xAB, 0xCD]));
    }

    [Fact]
    public async Task WriteAsync_UsesConfiguredWriteTimeout()
    {
        var device = new FakeHidTransportDevice(0x04D8, 0x003C, "path-a", "SN-A");
        var platform = new FakeHidPlatform([device]);
        using var transport = new HidLibraryTransport(platform)
        {
            WriteTimeout = TimeSpan.FromMilliseconds(321)
        };

        await transport.ConnectAsync(0x04D8, 0x003C);
        await transport.WriteAsync([0xAB, 0xCD]);

        Assert.Equal(321, device.LastWriteTimeoutMs);
    }

    [Fact]
    public async Task ReadAsync_WhenTimedOut_ThrowsTimeoutException()
    {
        var device = new FakeHidTransportDevice(0x04D8, 0x003C, "path-a", "SN-A")
        {
            NextReadResult = HidTransportReadResult.TimedOut(Array.Empty<byte>())
        };

        var platform = new FakeHidPlatform([device]);
        using var transport = new HidLibraryTransport(platform);
        await transport.ConnectAsync(0x04D8, 0x003C);

        await Assert.ThrowsAsync<TimeoutException>(() => transport.ReadAsync());
    }

    [Fact]
    public async Task ReadAsync_WhenReadFails_ThrowsIOException()
    {
        var device = new FakeHidTransportDevice(0x04D8, 0x003C, "path-a", "SN-A")
        {
            NextReadResult = HidTransportReadResult.Error(Array.Empty<byte>(), "Read failed")
        };

        var platform = new FakeHidPlatform([device]);
        using var transport = new HidLibraryTransport(platform);
        await transport.ConnectAsync(0x04D8, 0x003C);

        await Assert.ThrowsAsync<IOException>(() => transport.ReadAsync());
    }

    [Fact]
    public async Task ReadAsync_WithTimeoutOverride_UsesProvidedTimeout()
    {
        var device = new FakeHidTransportDevice(0x04D8, 0x003C, "path-a", "SN-A")
        {
            NextReadResult = HidTransportReadResult.Success([0xAA])
        };

        var platform = new FakeHidPlatform([device]);
        using var transport = new HidLibraryTransport(platform);
        await transport.ConnectAsync(0x04D8, 0x003C);

        var data = await transport.ReadAsync(TimeSpan.FromMilliseconds(250));

        Assert.Equal(new byte[] { 0xAA }, data);
        Assert.Equal(250, device.LastReadTimeoutMs);
    }

    [Fact]
    public async Task DisconnectAsync_ClosesDeviceAndClearsMetadata()
    {
        var device = new FakeHidTransportDevice(0x04D8, 0x003C, "path-a", "SN-A");
        var platform = new FakeHidPlatform([device]);

        using var transport = new HidLibraryTransport(platform);
        await transport.ConnectAsync(0x04D8, 0x003C);

        await transport.DisconnectAsync();

        Assert.False(transport.IsConnected);
        Assert.Null(transport.VendorId);
        Assert.Null(transport.ProductId);
        Assert.Null(transport.SerialNumber);
        Assert.Null(transport.DevicePath);
        Assert.Equal(1, device.CloseCount);
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
        public FakeHidTransportDevice(int vendorId, int productId, string devicePath, string serialNumber)
        {
            VendorId = vendorId;
            ProductId = productId;
            DevicePath = devicePath;
            SerialNumber = serialNumber;
            ProductName = "DAQiFi Bootloader";
        }

        public int VendorId { get; }
        public int ProductId { get; }
        public string DevicePath { get; }
        public string? SerialNumber { get; }
        public string? ProductName { get; }
        public bool IsConnected { get; private set; }

        public int OpenCount { get; private set; }
        public int CloseCount { get; private set; }
        public bool LastOpenExclusive { get; private set; }
        public int? LastWriteTimeoutMs { get; private set; }
        public int? LastReadTimeoutMs { get; private set; }

        public bool NextWriteResult { get; set; } = true;
        public HidTransportReadResult NextReadResult { get; set; }
            = HidTransportReadResult.Success(Array.Empty<byte>());

        public void Open(bool exclusive)
        {
            OpenCount++;
            LastOpenExclusive = exclusive;
            IsConnected = true;
        }

        public void Close()
        {
            CloseCount++;
            IsConnected = false;
        }

        public bool Write(byte[] data, int timeoutMs)
        {
            LastWriteTimeoutMs = timeoutMs;
            return NextWriteResult;
        }

        public Task<bool> WriteAsync(byte[] data, int timeoutMs)
        {
            LastWriteTimeoutMs = timeoutMs;
            return Task.FromResult(NextWriteResult);
        }

        public HidTransportReadResult Read(int timeoutMs)
        {
            LastReadTimeoutMs = timeoutMs;
            return NextReadResult;
        }

        public Task<HidTransportReadResult> ReadAsync(int timeoutMs)
        {
            LastReadTimeoutMs = timeoutMs;
            return Task.FromResult(NextReadResult);
        }
    }
}
