namespace Daqifi.Core.Communication.Transport;

/// <summary>
/// Default HID enumerator implementation backed by the HidSharp package.
/// </summary>
public sealed class HidLibraryDeviceEnumerator : IHidDeviceEnumerator
{
    private readonly IHidPlatform _hidPlatform;

    /// <summary>
    /// Initializes a new instance backed by the platform-appropriate HID adapter
    /// (HidSharp on Windows/Linux, native IOKit on macOS).
    /// </summary>
    public HidLibraryDeviceEnumerator()
        : this(HidPlatformFactory.CreateForCurrentPlatform())
    {
    }

    internal HidLibraryDeviceEnumerator(IHidPlatform hidPlatform)
    {
        _hidPlatform = hidPlatform ?? throw new ArgumentNullException(nameof(hidPlatform));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<HidDeviceInfo>> EnumerateAsync(
        int? vendorId = null,
        int? productId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // HID enumeration is synchronous; return a completed task to keep
            // the interface async without adding extra thread-pool hops.
            var enumerated = _hidPlatform.EnumerateDevices();
            try
            {
                var devices = enumerated
                    .Where(device => !vendorId.HasValue || device.VendorId == vendorId.Value)
                    .Where(device => !productId.HasValue || device.ProductId == productId.Value)
                    .OrderBy(device => device.DevicePath, StringComparer.Ordinal)
                    .Select(device => new HidDeviceInfo(
                        device.VendorId,
                        device.ProductId,
                        device.DevicePath,
                        device.SerialNumber,
                        device.ProductName))
                    .ToList();

                return Task.FromResult<IReadOnlyList<HidDeviceInfo>>(devices);
            }
            finally
            {
                // Only metadata (HidDeviceInfo) is returned, so release every
                // enumerated device handle now. The macOS backend retains a native
                // IOKit ref per device; this releases it deterministically instead
                // of leaving it for finalization. No-op for the HidSharp backend.
                foreach (var device in enumerated)
                {
                    (device as IDisposable)?.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Failed to enumerate HID devices{DescribeFilter(vendorId, productId)}.",
                ex);
        }
    }

    private static string DescribeFilter(int? vendorId, int? productId)
    {
        if (vendorId.HasValue && productId.HasValue)
        {
            return $" for VID=0x{vendorId.Value:X4}, PID=0x{productId.Value:X4}";
        }

        if (vendorId.HasValue)
        {
            return $" for VID=0x{vendorId.Value:X4}";
        }

        if (productId.HasValue)
        {
            return $" for PID=0x{productId.Value:X4}";
        }

        return string.Empty;
    }
}
