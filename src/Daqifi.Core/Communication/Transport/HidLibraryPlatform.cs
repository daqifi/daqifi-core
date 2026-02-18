using System.Text;
using HidLibrary;

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
            return HidDevices.Enumerate()
                .Select(device => (IHidTransportDevice)new HidLibraryTransportDevice(device))
                .ToList();
        }
        catch (Exception ex) when (
            ex is DllNotFoundException ||
            ex is PlatformNotSupportedException ||
            ex is EntryPointNotFoundException)
        {
            return Array.Empty<IHidTransportDevice>();
        }
    }
}

internal sealed class HidLibraryTransportDevice : IHidTransportDevice
{
    private readonly IHidDevice _device;

    public HidLibraryTransportDevice(IHidDevice device)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        VendorId = _device.Attributes.VendorId;
        ProductId = _device.Attributes.ProductId;
        DevicePath = _device.DevicePath;
        SerialNumber = ReadSerialNumber(_device);
        ProductName = ReadProductName(_device);
    }

    public int VendorId { get; }
    public int ProductId { get; }
    public string DevicePath { get; }
    public string? SerialNumber { get; }
    public string? ProductName { get; }
    public bool IsConnected => _device.IsConnected;

    public void Open()
    {
        _device.OpenDevice();
    }

    public void Close()
    {
        _device.CloseDevice();
    }

    public bool Write(byte[] data, int timeoutMs)
    {
        ArgumentNullException.ThrowIfNull(data);
        return _device.Write(data, timeoutMs);
    }

    public Task<bool> WriteAsync(byte[] data, int timeoutMs)
    {
        ArgumentNullException.ThrowIfNull(data);
        return _device.WriteAsync(data, timeoutMs);
    }

    public HidTransportReadResult Read(int timeoutMs)
    {
        var data = _device.Read(timeoutMs);
        return MapReadResult(data);
    }

    public async Task<HidTransportReadResult> ReadAsync(int timeoutMs)
    {
        var data = await _device.ReadAsync(timeoutMs).ConfigureAwait(false);
        return MapReadResult(data);
    }

    private static HidTransportReadResult MapReadResult(HidDeviceData deviceData)
    {
        var payload = deviceData.Data ?? Array.Empty<byte>();

        return deviceData.Status switch
        {
            HidDeviceData.ReadStatus.Success => HidTransportReadResult.Success(payload),
            HidDeviceData.ReadStatus.WaitTimedOut => HidTransportReadResult.TimedOut(payload),
            _ => HidTransportReadResult.Error(
                payload,
                $"HID read failed with status '{deviceData.Status}'.")
        };
    }

    private static string? ReadSerialNumber(IHidDevice device)
    {
        try
        {
            if (!device.ReadSerialNumber(out var data))
            {
                return null;
            }

            return DecodeHidString(data);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadProductName(IHidDevice device)
    {
        try
        {
            if (!device.ReadProduct(out var data))
            {
                return null;
            }

            return DecodeHidString(data);
        }
        catch
        {
            return null;
        }
    }

    private static string? DecodeHidString(byte[]? rawData)
    {
        if (rawData == null || rawData.Length == 0)
        {
            return null;
        }

        var unicode = Encoding.Unicode.GetString(rawData).TrimEnd('\0').Trim();
        if (!string.IsNullOrWhiteSpace(unicode))
        {
            return unicode;
        }

        var ascii = Encoding.ASCII.GetString(rawData).TrimEnd('\0').Trim();
        return string.IsNullOrWhiteSpace(ascii) ? null : ascii;
    }
}
