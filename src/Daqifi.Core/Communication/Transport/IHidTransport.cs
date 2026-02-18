namespace Daqifi.Core.Communication.Transport;

/// <summary>
/// Transport abstraction for USB HID bootloader communication.
/// </summary>
public interface IHidTransport : IDisposable
{
    /// <summary>
    /// Gets a value indicating whether the HID transport is connected.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Gets the connected device vendor ID, or null when disconnected.
    /// </summary>
    int? VendorId { get; }

    /// <summary>
    /// Gets the connected device product ID, or null when disconnected.
    /// </summary>
    int? ProductId { get; }

    /// <summary>
    /// Gets the connected device serial number, when available.
    /// </summary>
    string? SerialNumber { get; }

    /// <summary>
    /// Gets the connected device path, when available.
    /// </summary>
    string? DevicePath { get; }

    /// <summary>
    /// Gets or sets the default read timeout used when <see cref="ReadAsync(TimeSpan?, CancellationToken)"/>
    /// is called without an explicit timeout.
    /// </summary>
    TimeSpan ReadTimeout { get; set; }

    /// <summary>
    /// Connects to a HID device by vendor/product identifier, optionally targeting a serial number.
    /// </summary>
    /// <param name="vendorId">Target USB vendor ID.</param>
    /// <param name="productId">Target USB product ID.</param>
    /// <param name="serialNumber">Optional serial number filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ConnectAsync(
        int vendorId,
        int productId,
        string? serialNumber = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Connects to a HID device synchronously.
    /// </summary>
    /// <param name="vendorId">Target USB vendor ID.</param>
    /// <param name="productId">Target USB product ID.</param>
    /// <param name="serialNumber">Optional serial number filter.</param>
    void Connect(int vendorId, int productId, string? serialNumber = null);

    /// <summary>
    /// Writes a HID report payload asynchronously.
    /// </summary>
    /// <param name="data">The payload bytes to write.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task WriteAsync(byte[] data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a HID report payload synchronously.
    /// </summary>
    /// <param name="data">The payload bytes to write.</param>
    void Write(byte[] data);

    /// <summary>
    /// Reads a HID report payload asynchronously.
    /// </summary>
    /// <param name="timeout">Optional timeout override for this read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The raw bytes from the HID read operation.</returns>
    Task<byte[]> ReadAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a HID report payload synchronously.
    /// </summary>
    /// <param name="timeout">Optional timeout override for this read.</param>
    /// <returns>The raw bytes from the HID read operation.</returns>
    byte[] Read(TimeSpan? timeout = null);

    /// <summary>
    /// Disconnects from the HID device asynchronously.
    /// </summary>
    Task DisconnectAsync();

    /// <summary>
    /// Disconnects from the HID device synchronously.
    /// </summary>
    void Disconnect();
}
