using System.IO;

namespace Daqifi.Core.Communication.Transport;

/// <summary>
/// HID transport implementation backed by HidLibrary.
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
    /// Initializes a new transport instance backed by the default HidLibrary adapter.
    /// </summary>
    public HidLibraryTransport()
        : this(new HidLibraryPlatform())
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

        cancellationToken.ThrowIfCancellationRequested();

        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected)
            {
                return;
            }

            var serialFilter = NormalizeSerialFilter(serialNumber);
            var device = _hidPlatform.EnumerateDevices()
                .Where(candidate => candidate.VendorId == vendorId)
                .Where(candidate => candidate.ProductId == productId)
                .Where(candidate => serialFilter == null ||
                    string.Equals(candidate.SerialNumber, serialFilter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(candidate => candidate.DevicePath, StringComparer.Ordinal)
                .FirstOrDefault();

            if (device == null)
            {
                var target = serialFilter == null
                    ? $"VID=0x{vendorId:X4}, PID=0x{productId:X4}"
                    : $"VID=0x{vendorId:X4}, PID=0x{productId:X4}, Serial={serialFilter}";

                throw new IOException($"No HID device found for {target}.");
            }

            try
            {
                device.Open();
            }
            catch (Exception ex)
            {
                throw new IOException("Failed to open HID device.", ex);
            }

            if (!device.IsConnected)
            {
                device.Close();
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
    public void Connect(int vendorId, int productId, string? serialNumber = null)
    {
        ConnectAsync(vendorId, productId, serialNumber, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
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
