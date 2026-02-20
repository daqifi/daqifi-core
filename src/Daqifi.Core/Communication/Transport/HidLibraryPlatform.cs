using System.IO;
using HidSharp;

namespace Daqifi.Core.Communication.Transport;

internal interface IHidPlatform
{
    IReadOnlyList<IHidTransportDevice> EnumerateDevices();
}

internal interface IHidTransportDevice
{
    int VendorId { get; }
    int ProductId { get; }
    string DevicePath { get; }
    string? SerialNumber { get; }
    string? ProductName { get; }
    bool IsConnected { get; }

    void Open();
    void Close();
    bool Write(byte[] data, int timeoutMs);
    Task<bool> WriteAsync(byte[] data, int timeoutMs);
    HidTransportReadResult Read(int timeoutMs);
    Task<HidTransportReadResult> ReadAsync(int timeoutMs);
}

internal enum HidTransportReadStatus
{
    Success,
    TimedOut,
    Error
}

internal readonly record struct HidTransportReadResult(
    HidTransportReadStatus Status,
    byte[] Data,
    string? ErrorMessage = null)
{
    public static HidTransportReadResult Success(byte[] data)
        => new(HidTransportReadStatus.Success, data);

    public static HidTransportReadResult TimedOut(byte[] data)
        => new(HidTransportReadStatus.TimedOut, data, "HID read timed out.");

    public static HidTransportReadResult Error(byte[] data, string message)
        => new(HidTransportReadStatus.Error, data, message);
}

internal sealed class HidLibraryPlatform : IHidPlatform
{
    public IReadOnlyList<IHidTransportDevice> EnumerateDevices()
    {
        try
        {
            return DeviceList.Local.GetHidDevices()
                .Select(device => (IHidTransportDevice)new HidLibraryTransportDevice(device))
                .ToList();
        }
        catch (Exception ex) when (
            ex is DllNotFoundException ||
            ex is PlatformNotSupportedException ||
            ex is EntryPointNotFoundException ||
            ex is BadImageFormatException ||
            ex is TypeInitializationException)
        {
            throw new InvalidOperationException(
                "HID backend is unavailable. USB HID enumeration could not be initialized in this process.",
                ex);
        }
    }
}

internal sealed class HidLibraryTransportDevice : IHidTransportDevice
{
    private readonly HidDevice _device;
    private HidStream? _stream;

    public HidLibraryTransportDevice(HidDevice device)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        VendorId = _device.VendorID;
        ProductId = _device.ProductID;
        DevicePath = _device.DevicePath;
        SerialNumber = ReadSerialNumber(_device);
        ProductName = ReadProductName(_device);
    }

    public int VendorId { get; }
    public int ProductId { get; }
    public string DevicePath { get; }
    public string? SerialNumber { get; }
    public string? ProductName { get; }
    public bool IsConnected
    {
        get
        {
            var stream = _stream;
            if (stream == null)
            {
                return false;
            }

            try
            {
                return stream.CanRead && stream.CanWrite;
            }
            catch (Exception ex) when (
                ex is ObjectDisposedException ||
                ex is IOException)
            {
                return false;
            }
        }
    }

    public void Open()
    {
        if (_stream != null)
        {
            return;
        }

        if (!_device.TryOpen(out var stream) || stream == null)
        {
            throw new IOException("Failed to open HID device.");
        }

        _stream = stream;
    }

    public void Close()
    {
        var stream = _stream;
        _stream = null;
        stream?.Dispose();
    }

    public bool Write(byte[] data, int timeoutMs)
    {
        ArgumentNullException.ThrowIfNull(data);
        var stream = GetOpenStream();
        stream.WriteTimeout = timeoutMs;
        var payload = FormatOutputReport(data);

        try
        {
            stream.Write(payload);
            return true;
        }
        catch (Exception ex) when (
            ex is TimeoutException ||
            ex is IOException ||
            ex is ObjectDisposedException)
        {
            return false;
        }
    }

    public Task<bool> WriteAsync(byte[] data, int timeoutMs)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Task.FromResult(Write(data, timeoutMs));
    }

    public HidTransportReadResult Read(int timeoutMs)
    {
        var stream = GetOpenStream();
        stream.ReadTimeout = timeoutMs;

        try
        {
            var data = stream.Read() ?? Array.Empty<byte>();
            if (data.Length == 0)
            {
                return HidTransportReadResult.TimedOut(data);
            }

            var payload = ExtractInputPayload(data);
            if (payload.Length == 0)
            {
                return HidTransportReadResult.TimedOut(payload);
            }

            return HidTransportReadResult.Success(payload);
        }
        catch (TimeoutException)
        {
            return HidTransportReadResult.TimedOut(Array.Empty<byte>());
        }
        catch (Exception ex) when (
            ex is IOException ||
            ex is ObjectDisposedException)
        {
            return HidTransportReadResult.Error(
                Array.Empty<byte>(),
                ex.Message);
        }
    }

    public Task<HidTransportReadResult> ReadAsync(int timeoutMs)
    {
        return Task.FromResult(Read(timeoutMs));
    }

    private static string? ReadSerialNumber(HidDevice device)
    {
        try
        {
            return NormalizeHidString(device.GetSerialNumber());
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadProductName(HidDevice device)
    {
        try
        {
            return NormalizeHidString(device.GetProductName());
        }
        catch
        {
            return null;
        }
    }

    private HidStream GetOpenStream()
    {
        if (_stream == null)
        {
            throw new InvalidOperationException("HID device stream is not open.");
        }

        return _stream;
    }

    private static string? NormalizeHidString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().TrimEnd('\0');
    }

    private byte[] FormatOutputReport(byte[] payload)
    {
        var reportLength = _device.GetMaxOutputReportLength();
        if (reportLength <= 0)
        {
            return payload;
        }

        // Keep compatibility with the previous transport behavior:
        // callers provide protocol payload only; prepend report ID byte and pad.
        var formatted = new byte[reportLength];
        var bytesToCopy = Math.Min(payload.Length, reportLength - 1);
        Array.Copy(payload, 0, formatted, 1, bytesToCopy);
        return formatted;
    }

    private static byte[] ExtractInputPayload(byte[] report)
    {
        if (report.Length <= 1)
        {
            return Array.Empty<byte>();
        }

        // Report ID is the first byte for HID APIs; protocol payload starts after it.
        var payload = new byte[report.Length - 1];
        Array.Copy(report, 1, payload, 0, payload.Length);
        return payload;
    }
}
