using System.IO;

namespace Daqifi.Core.Communication.Transport;

/// <summary>
/// HID transport implementation backed by HidSharp.
/// </summary>
public sealed class HidLibraryTransport : IHidTransport
{
    private static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultWriteTimeout = TimeSpan.FromSeconds(10);

    private readonly IHidPlatform _hidPlatform;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    private IHidTransportDevice? _connectedDevice;
    private bool _disposed;
    private TimeSpan _readTimeout = DefaultReadTimeout;
    private TimeSpan _writeTimeout = DefaultWriteTimeout;

    /// <summary>
    /// Initializes a new transport instance backed by the platform-appropriate HID
    /// adapter (HidSharp on Windows/Linux, native IOKit on macOS).
    /// </summary>
    public HidLibraryTransport()
        : this(HidPlatformFactory.CreateForCurrentPlatform())
    {
    }

    internal HidLibraryTransport(IHidPlatform hidPlatform)
    {
        _hidPlatform = hidPlatform ?? throw new ArgumentNullException(nameof(hidPlatform));
    }

    /// <inheritdoc />
    public bool IsConnected => !_disposed && _connectedDevice?.IsConnected == true;

    /// <inheritdoc />
    public int? VendorId { get; private set; }

    /// <inheritdoc />
    public int? ProductId { get; private set; }

    /// <inheritdoc />
    public string? SerialNumber { get; private set; }

    /// <inheritdoc />
    public string? DevicePath { get; private set; }

    /// <inheritdoc />
    public TimeSpan ReadTimeout
    {
        get => _readTimeout;
        set
        {
            ValidateTimeout(value, nameof(value), "Read timeout must be greater than zero.");
            _readTimeout = value;
        }
    }

    /// <inheritdoc />
    public TimeSpan WriteTimeout
    {
        get => _writeTimeout;
        set
        {
            ValidateTimeout(value, nameof(value), "Write timeout must be greater than zero.");
            _writeTimeout = value;
        }
    }

    /// <summary>
    /// Gets or sets whether <see cref="ConnectAsync"/> opens the device exclusively (Windows
    /// <c>dwShareMode=0</c>; macOS IOKit seize) so no other user-mode opener can open or write to it
    /// while this transport holds the handle. Defaults to <c>false</c> (shared). Set to <c>true</c>
    /// for a PIC32 bootloader flash: the bootloader's CRC check is disabled, so an exclusive handle
    /// guards against a stray frame from another opener being mis-parsed as an ERASE and locks the
    /// discovery loop out of the device for the flash. Best-effort — a refused exclusive open falls
    /// back to a shared open so a flash that works today is never regressed.
    /// </summary>
    public bool ExclusiveAccess { get; set; }

    /// <inheritdoc />
    public async Task ConnectAsync(
        int vendorId,
        int productId,
        string? serialNumber = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateUsbIdentifier(vendorId, nameof(vendorId));
        ValidateUsbIdentifier(productId, nameof(productId));

        var serialFilter = NormalizeSerialFilter(serialNumber);
        var target = serialFilter == null
            ? $"VID=0x{vendorId:X4}, PID=0x{productId:X4}"
            : $"VID=0x{vendorId:X4}, PID=0x{productId:X4}, Serial={serialFilter}";

        await ConnectMatchingDeviceAsync(
            candidate => candidate.VendorId == vendorId
                && candidate.ProductId == productId
                && (serialFilter == null || string.Equals(
                    candidate.SerialNumber, serialFilter, StringComparison.OrdinalIgnoreCase)),
            target,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Connect(int vendorId, int productId, string? serialNumber = null)
    {
        ConnectAsync(vendorId, productId, serialNumber, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    /// <inheritdoc />
    public async Task ConnectByPathAsync(string devicePath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(devicePath))
        {
            throw new ArgumentException("Device path cannot be null or empty.", nameof(devicePath));
        }

        await ConnectMatchingDeviceAsync(
            candidate => string.Equals(candidate.DevicePath, devicePath, StringComparison.Ordinal),
            $"Path={devicePath}",
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void ConnectByPath(string devicePath)
    {
        ConnectByPathAsync(devicePath, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    // Shared connect path: enumerate, pick the single device matching <paramref name="matches"/>
    // (deterministically ordered by DevicePath), release the rest, and open it. ConnectAsync targets a
    // VID/PID(+serial); ConnectByPathAsync targets one exact device path among several identical ones.
    private async Task ConnectMatchingDeviceAsync(
        Func<IHidTransportDevice, bool> matches,
        string targetDescription,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected)
            {
                return;
            }

            var candidates = _hidPlatform.EnumerateDevices();
            var device = candidates
                .Where(matches)
                .OrderBy(candidate => candidate.DevicePath, StringComparer.Ordinal)
                .FirstOrDefault();

            // Release every enumerated device we are not keeping. The macOS backend
            // retains a native IOKit ref per device; disposing the unused candidates
            // frees them now instead of at finalization. No-op for HidSharp devices.
            foreach (var candidate in candidates)
            {
                if (!ReferenceEquals(candidate, device))
                {
                    (candidate as IDisposable)?.Dispose();
                }
            }

            if (device == null)
            {
                throw new IOException($"No HID device found for {targetDescription}.");
            }

            try
            {
                device.Open(ExclusiveAccess);
            }
            catch (Exception ex)
            {
                (device as IDisposable)?.Dispose();
                throw new IOException("Failed to open HID device.", ex);
            }

            if (!device.IsConnected)
            {
                device.Close();
                (device as IDisposable)?.Dispose();
                throw new IOException("HID device did not report a connected state after open.");
            }

            _connectedDevice = device;
            VendorId = device.VendorId;
            ProductId = device.ProductId;
            SerialNumber = device.SerialNumber;
            DevicePath = device.DevicePath;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task WriteAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0)
        {
            throw new ArgumentException("Data cannot be empty.", nameof(data));
        }

        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var device = GetConnectedDevice();
            var timeoutMs = ToTimeoutMilliseconds(WriteTimeout);
            var successful = await device.WriteAsync(data, timeoutMs)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!successful)
            {
                throw new IOException("HID write failed.");
            }
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <inheritdoc />
    public void Write(byte[] data)
    {
        WriteAsync(data, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    /// <inheritdoc />
    public async Task<byte[]> ReadAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var effectiveTimeout = timeout ?? ReadTimeout;
        if (effectiveTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be greater than zero.");
        }

        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var device = GetConnectedDevice();
            var timeoutMs = ToTimeoutMilliseconds(effectiveTimeout);
            var result = await device.ReadAsync(timeoutMs)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            return result.Status switch
            {
                HidTransportReadStatus.Success => result.Data,
                HidTransportReadStatus.TimedOut => throw new TimeoutException(
                    $"Timed out waiting for HID data after {effectiveTimeout.TotalMilliseconds:F0} ms."),
                _ => throw new IOException(result.ErrorMessage ?? "HID read failed.")
            };
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <inheritdoc />
    public byte[] Read(TimeSpan? timeout = null)
    {
        return ReadAsync(timeout, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    /// <inheritdoc />
    public async Task DisconnectAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _connectionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_connectedDevice == null)
            {
                return;
            }

            Exception? closeException = null;
            await _ioLock.WaitAsync().ConfigureAwait(false);
            try
            {
                _connectedDevice.Close();
            }
            catch (Exception ex)
            {
                closeException = ex;
            }
            finally
            {
                _ioLock.Release();
            }

            (_connectedDevice as IDisposable)?.Dispose();
            ClearConnectionState();

            if (closeException != null)
            {
                throw new IOException("Failed to close HID device.", closeException);
            }
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <inheritdoc />
    public void Disconnect()
    {
        DisconnectAsync().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var device = _connectedDevice;
        ClearConnectionState();

        try
        {
            device?.Close();
        }
        catch
        {
            // Ignore disconnection errors during disposal.
        }

        (device as IDisposable)?.Dispose();

        _connectionLock.Dispose();
        _ioLock.Dispose();
    }

    private static string? NormalizeSerialFilter(string? serialNumber)
    {
        if (string.IsNullOrWhiteSpace(serialNumber))
        {
            return null;
        }

        return serialNumber.Trim();
    }

    private static void ValidateUsbIdentifier(int identifier, string parameterName)
    {
        if (identifier < 0 || identifier > 0xFFFF)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                identifier,
                "USB identifiers must be in the range 0..65535.");
        }
    }

    private static int ToTimeoutMilliseconds(TimeSpan timeout)
    {
        ValidateTimeout(timeout, nameof(timeout), "Timeout must be greater than zero.");

        if (timeout.TotalMilliseconds > int.MaxValue)
        {
            return int.MaxValue;
        }

        return (int)Math.Ceiling(timeout.TotalMilliseconds);
    }

    private static void ValidateTimeout(TimeSpan timeout, string parameterName, string message)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, message);
        }
    }

    private IHidTransportDevice GetConnectedDevice()
    {
        if (_connectedDevice == null || !_connectedDevice.IsConnected)
        {
            throw new InvalidOperationException("HID transport is not connected.");
        }

        return _connectedDevice;
    }

    private void ClearConnectionState()
    {
        _connectedDevice = null;
        VendorId = null;
        ProductId = null;
        SerialNumber = null;
        DevicePath = null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(HidLibraryTransport));
        }
    }
}
