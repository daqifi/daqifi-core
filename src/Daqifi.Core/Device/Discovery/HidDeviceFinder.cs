using Daqifi.Core.Communication.Transport;

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Discovers DAQiFi devices in USB HID bootloader mode.
/// </summary>
public class HidDeviceFinder : IDeviceFinder, IDisposable
{
    /// <summary>
    /// Default DAQiFi bootloader vendor ID.
    /// </summary>
    public const int DefaultVendorId = 0x04D8;

    /// <summary>
    /// Default DAQiFi bootloader product ID.
    /// </summary>
    public const int DefaultProductId = 0x003C;

    private readonly IHidDeviceEnumerator _hidDeviceEnumerator;
    private readonly SemaphoreSlim _discoverySemaphore = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Initializes a new finder that filters to DAQiFi bootloader VID/PID by default.
    /// </summary>
    public HidDeviceFinder()
        : this(new HidLibraryDeviceEnumerator(), DefaultVendorId, DefaultProductId)
    {
    }

    internal HidDeviceFinder(
        IHidDeviceEnumerator hidDeviceEnumerator,
        int? vendorIdFilter = DefaultVendorId,
        int? productIdFilter = DefaultProductId)
    {
        _hidDeviceEnumerator = hidDeviceEnumerator
            ?? throw new ArgumentNullException(nameof(hidDeviceEnumerator));

        VendorIdFilter = vendorIdFilter;
        ProductIdFilter = productIdFilter;
    }

    /// <summary>
    /// Gets or sets the vendor ID filter used by <see cref="DiscoverAsync(CancellationToken)"/>.
    /// </summary>
    public int? VendorIdFilter { get; set; }

    /// <summary>
    /// Gets or sets the product ID filter used by <see cref="DiscoverAsync(CancellationToken)"/>.
    /// </summary>
    public int? ProductIdFilter { get; set; }

    /// <inheritdoc />
    public event EventHandler<DeviceDiscoveredEventArgs>? DeviceDiscovered;

    /// <inheritdoc />
    public event EventHandler? DiscoveryCompleted;

    /// <inheritdoc />
    public Task<IEnumerable<IDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        return DiscoverAsync(VendorIdFilter, ProductIdFilter, cancellationToken);
    }

    /// <summary>
    /// Discovers HID devices asynchronously with explicit optional VID/PID filtering.
    /// </summary>
    /// <param name="vendorIdFilter">Optional vendor ID filter.</param>
    /// <param name="productIdFilter">Optional product ID filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Discovered HID devices mapped to <see cref="IDeviceInfo"/>.</returns>
    public async Task<IEnumerable<IDeviceInfo>> DiscoverAsync(
        int? vendorIdFilter,
        int? productIdFilter,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _discoverySemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var hidDevices = await _hidDeviceEnumerator
                .EnumerateAsync(vendorIdFilter, productIdFilter, cancellationToken)
                .ConfigureAwait(false);

            var discoveredDevices = new List<IDeviceInfo>(hidDevices.Count);

            foreach (var hidDevice in hidDevices)
            {
                var deviceInfo = new DeviceInfo
                {
                    Name = string.IsNullOrWhiteSpace(hidDevice.ProductName)
                        ? "DAQiFi Bootloader"
                        : hidDevice.ProductName,
                    SerialNumber = hidDevice.SerialNumber ?? string.Empty,
                    FirmwareVersion = string.Empty,
                    ConnectionType = ConnectionType.Hid,
                    Type = DeviceType.Unknown,
                    DevicePath = hidDevice.DevicePath
                };

                discoveredDevices.Add(deviceInfo);
                OnDeviceDiscovered(deviceInfo);
            }

            OnDiscoveryCompleted();
            return discoveredDevices;
        }
        finally
        {
            _discoverySemaphore.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IEnumerable<IDeviceInfo>> DiscoverAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await DiscoverAsync(cts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Raises the <see cref="DeviceDiscovered"/> event.
    /// </summary>
    /// <param name="deviceInfo">The discovered device metadata.</param>
    protected virtual void OnDeviceDiscovered(IDeviceInfo deviceInfo)
    {
        DeviceDiscovered?.Invoke(this, new DeviceDiscoveredEventArgs(deviceInfo));
    }

    /// <summary>
    /// Raises the <see cref="DiscoveryCompleted"/> event.
    /// </summary>
    protected virtual void OnDiscoveryCompleted()
    {
        DiscoveryCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(HidDeviceFinder));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _discoverySemaphore.Dispose();
        _disposed = true;
    }
}
