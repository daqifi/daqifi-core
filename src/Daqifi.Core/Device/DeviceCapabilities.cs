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
    /// Gets or sets a value indicating whether the device's WiFi connectivity is provided by a
    /// discrete WINC1500 module (as opposed to, e.g., a future SoC-integrated WiFi part). Lets
    /// consumers ask Core whether WINC-specific handling (power-on-before-probe, the WINC flash
    /// tool, bridge-mode activation) applies to a device instead of pattern-matching
    /// <see cref="DeviceType"/> themselves. Distinct from <see cref="HasWiFi"/>, which only
    /// indicates WiFi connectivity in general.
    /// </summary>
    public bool HasWincWifiModule { get; set; }

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
        HasWincWifiModule = false;
        HasUsb = false;
        AnalogInputChannels = 0;
        AnalogOutputChannels = 0;
        DigitalChannels = 0;
        MaxSamplingRate = 1000;
    }

    /// <summary>
    /// Creates the capabilities a board is known to have, before the device has described itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the bootstrap for <see cref="DeviceMetadata.Capabilities"/>: it answers as soon as
    /// the part number is known, and it is the floor under every field the device's own
    /// capability document does not state. A connected device overlays the document on top of it,
    /// so read capabilities from <c>IStreamingDevice.Metadata.Capabilities</c>, not from this
    /// method, whenever a device is attached.
    /// </para>
    /// <para>
    /// The values come from two sources. The Nyquist1 row is pinned to a capability document read
    /// from a real board (<c>DeviceCapabilitiesTests</c> fails if the two disagree). The Nyquist3
    /// row is read from the firmware's board configuration (<c>NQ3BoardConfig.c</c>), not from a
    /// connected board. The firmware has no Nyquist2 build configuration, so the Nyquist2 channel
    /// counts are left at zero: the device reports them, and zero means "not stated here", not
    /// "none". The sampling-rate ceiling is the firmware's own advertised ISR ceiling for the
    /// board variant: 22 kHz for Nyquist1, 16 kHz for Nyquist2 and Nyquist3.
    /// </para>
    /// </remarks>
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
                HasWincWifiModule = true,
                HasUsb = true,
                AnalogInputChannels = 16,
                AnalogOutputChannels = 0,
                DigitalChannels = 16,
                MaxSamplingRate = 22000
            },
            DeviceType.Nyquist2 => new DeviceCapabilities
            {
                SupportsStreaming = true,
                HasSdCard = true,
                HasWiFi = true,
                HasWincWifiModule = true,
                HasUsb = true,
                MaxSamplingRate = 16000
            },
            DeviceType.Nyquist3 => new DeviceCapabilities
            {
                SupportsStreaming = true,
                HasSdCard = true,
                HasWiFi = true,
                HasWincWifiModule = true,
                HasUsb = true,
                AnalogInputChannels = 8,
                AnalogOutputChannels = 8,
                DigitalChannels = 16,
                MaxSamplingRate = 16000
            },
            _ => new DeviceCapabilities()
        };
    }

    /// <summary>
    /// Creates a deep copy of this <see cref="DeviceCapabilities"/> instance.
    /// </summary>
    /// <returns>A new <see cref="DeviceCapabilities"/> instance with the same field values.</returns>
    public DeviceCapabilities Clone()
    {
        return new DeviceCapabilities
        {
            SupportsStreaming = SupportsStreaming,
            HasSdCard = HasSdCard,
            HasWiFi = HasWiFi,
            HasWincWifiModule = HasWincWifiModule,
            HasUsb = HasUsb,
            AnalogInputChannels = AnalogInputChannels,
            AnalogOutputChannels = AnalogOutputChannels,
            DigitalChannels = DigitalChannels,
            MaxSamplingRate = MaxSamplingRate
        };
    }
}
