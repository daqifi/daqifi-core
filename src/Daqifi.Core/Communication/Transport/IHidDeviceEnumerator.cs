namespace Daqifi.Core.Communication.Transport;

/// <summary>
/// Enumerates HID devices for discovery and bootloader connection workflows.
/// </summary>
public interface IHidDeviceEnumerator
{
    /// <summary>
    /// Enumerates HID devices, optionally filtered by vendor/product identifiers.
    /// </summary>
    /// <param name="vendorId">Optional vendor ID filter.</param>
    /// <param name="productId">Optional product ID filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of discovered HID devices.</returns>
    Task<IReadOnlyList<HidDeviceInfo>> EnumerateAsync(
        int? vendorId = null,
        int? productId = null,
        CancellationToken cancellationToken = default);
}
