namespace Daqifi.Core.Device;

/// <summary>
/// Represents the capabilities and features available on a DAQiFi device.
/// </summary>
public class DeviceCapabilities
{
    /// <summary>
    /// Gets or sets a value indicating whether the device supports streaming data.
    /// </summary>
    public bool SupportsStreaming { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the device has an SD card slot.
    /// </summary>
    public bool HasSdCard { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the device supports WiFi connectivity.
    /// </summary>
    public bool HasWiFi { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the device supports USB connectivity.
    /// </summary>
    public bool HasUsb { get; set; }

    /// <summary>
    /// Gets or sets the number of analog input channels available.
    /// </summary>
    public int AnalogInputChannels { get; set; }

    /// <summary>
    /// Gets or sets the number of analog output channels available.
    /// </summary>
    public int AnalogOutputChannels { get; set; }

    /// <summary>
    /// Gets or sets the number of digital I/O channels available.
    /// </summary>
    public int DigitalChannels { get; set; }

    /// <summary>
    /// Gets or sets the maximum sampling rate in Hz.
    /// </summary>
    public int MaxSamplingRate { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceCapabilities"/> class with default values.
    /// </summary>
    public DeviceCapabilities()
    {
        SupportsStreaming = true;
        HasSdCard = false;
        HasWiFi = false;
        HasUsb = false;
        AnalogInputChannels = 0;
        AnalogOutputChannels = 0;
        DigitalChannels = 0;
        MaxSamplingRate = 1000;
    }

    /// <summary>
    /// Creates device capabilities based on the device type.
    /// </summary>
    /// <param name="deviceType">The type of the device.</param>
    /// <returns>A <see cref="DeviceCapabilities"/> instance configured for the specified device type.</returns>
    public static DeviceCapabilities FromDeviceType(DeviceType deviceType)
    {
        return deviceType switch
        {
            DeviceType.Nyquist1 => new DeviceCapabilities
            {
                SupportsStreaming = true,
                HasSdCard = true,
                HasWiFi = true,
                HasUsb = true,
                MaxSamplingRate = 1000
            },
            DeviceType.Nyquist2 => new DeviceCapabilities
            {
                SupportsStreaming = true,
                HasSdCard = true,
                HasWiFi = true,
                HasUsb = true,
                MaxSamplingRate = 1000
            },
            DeviceType.Nyquist3 => new DeviceCapabilities
            {
                SupportsStreaming = true,
                HasSdCard = true,
                HasWiFi = true,
                HasUsb = true,
                MaxSamplingRate = 1000
            },
            _ => new DeviceCapabilities()
        };
    }
}
