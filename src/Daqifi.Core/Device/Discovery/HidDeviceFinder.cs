using Daqifi.Core.Communication.Transport;

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Discovers DAQiFi devices in USB HID bootloader mode.
/// </summary>
public class HidDeviceFinder : DeviceFinderBase
{
    private readonly IHidDeviceEnumerator _hidDeviceEnumerator;
    private readonly IUsbLocationProvider _usbLocationProvider;

    /// <summary>
    /// Initializes a new finder that filters to DAQiFi bootloader VID/PID by default.
    /// </summary>
    public HidDeviceFinder()
        : this(new HidLibraryDeviceEnumerator())
    {
    }

    internal HidDeviceFinder(IHidDeviceEnumerator hidDeviceEnumerator)
        : this(hidDeviceEnumerator, usbLocationProvider: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the HidDeviceFinder class with an
    /// explicit USB location provider — primarily for tests that mock the
    /// platform-specific WMI lookup.
    /// </summary>
    /// <param name="hidDeviceEnumerator">Enumerator used to list HID devices.</param>
    /// <param name="usbLocationProvider">
    /// Provider used to resolve a discovered device's USB physical-location
    /// key. When null, a platform-default provider is used (Windows → WMI,
    /// others → no-op fallback).
    /// </param>
    internal HidDeviceFinder(IHidDeviceEnumerator hidDeviceEnumerator, IUsbLocationProvider? usbLocationProvider)
    {
        _hidDeviceEnumerator = hidDeviceEnumerator
            ?? throw new ArgumentNullException(nameof(hidDeviceEnumerator));
        _usbLocationProvider = usbLocationProvider
            ?? UsbLocationProviderFactory.CreateForCurrentPlatform();

        VendorIdFilter = DaqifiUsbIds.VendorId;
        ProductIdFilter = DaqifiUsbIds.BootloaderProductId;
    }

    /// <summary>
    /// Gets or sets the vendor ID filter used by <see cref="DeviceFinderBase.DiscoverAsync"/>.
    /// </summary>
    public int? VendorIdFilter { get; set; }

    /// <summary>
    /// Gets or sets the product ID filter used by <see cref="DeviceFinderBase.DiscoverAsync"/>.
    /// </summary>
    public int? ProductIdFilter { get; set; }

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<IDeviceInfo>> RunPassAsync(CancellationToken passToken)
    {
        var hidDevices = await _hidDeviceEnumerator
            .EnumerateAsync(VendorIdFilter, ProductIdFilter, passToken)
            .ConfigureAwait(false);

        var discoveredDevices = new List<IDeviceInfo>(hidDevices.Count);

        foreach (var hidDevice in hidDevices)
        {
            if (passToken.IsCancellationRequested)
            {
                MarkPassPartial();
                break;
            }

            string? locationKey;
            try
            {
                locationKey = _usbLocationProvider.GetLocationKey(hidDevice.DevicePath);
            }
            catch
            {
                // A misbehaving custom IUsbLocationProvider must never abort the whole
                // enumeration — location is enrichment metadata, not identification.
                // Mirrors SerialDeviceFinder's handling of a throwing provider.
                locationKey = null;
            }

            // A location lookup can be slow (WMI), so the pass may have ended during it.
            if (passToken.IsCancellationRequested)
            {
                MarkPassPartial();
                break;
            }

            var deviceInfo = new DeviceInfo
            {
                Name = string.IsNullOrWhiteSpace(hidDevice.ProductName)
                    ? "DAQiFi Bootloader"
                    : hidDevice.ProductName,
                SerialNumber = hidDevice.SerialNumber ?? string.Empty,
                FirmwareVersion = string.Empty,
                ConnectionType = ConnectionType.Hid,
                Type = DeviceType.Unknown,
                DevicePath = hidDevice.DevicePath,
                LocationKey = locationKey
            };

            discoveredDevices.Add(deviceInfo);
            OnDeviceDiscovered(deviceInfo);
        }

        return discoveredDevices;
    }
}
