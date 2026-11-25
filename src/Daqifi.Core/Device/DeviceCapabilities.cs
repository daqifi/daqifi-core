namespace Daqifi.Core.Device;

/// <summary>
/// Represents the capabilities and features available on a DAQiFi device. Immutable: use a
/// <c>with</c> expression to derive a changed copy.
/// </summary>
public sealed record DeviceCapabilities
{
    /// <summary>
    /// Gets a value indicating whether the device supports streaming data.
    /// </summary>
    public bool SupportsStreaming { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether the device has an SD card slot.
    /// </summary>
    public bool HasSdCard { get; init; }

    /// <summary>
    /// Gets a value indicating whether the device supports WiFi connectivity.
    /// </summary>
    public bool HasWifi { get; init; }

    /// <summary>
    /// Gets a value indicating whether the device's WiFi connectivity is provided by a
    /// discrete WINC1500 module (as opposed to, e.g., a future SoC-integrated WiFi part). Lets
    /// consumers ask Core whether WINC-specific handling (power-on-before-probe, the WINC flash
    /// tool, bridge-mode activation) applies to a device instead of pattern-matching
    /// <see cref="DeviceType"/> themselves. Distinct from <see cref="HasWifi"/>, which only
    /// indicates WiFi connectivity in general.
    /// </summary>
    public bool HasWincWifiModule { get; init; }

    /// <summary>
    /// Gets a value indicating whether the device supports USB connectivity.
    /// </summary>
    public bool HasUsb { get; init; }

    /// <summary>
    /// Gets the number of analog input channels available.
    /// </summary>
    public int AnalogInputChannels { get; init; }

    /// <summary>
    /// Gets the number of analog output channels available.
    /// </summary>
    public int AnalogOutputChannels { get; init; }

    /// <summary>
    /// Gets the number of digital I/O channels available.
    /// </summary>
    public int DigitalChannels { get; init; }

    /// <summary>
    /// Gets the maximum sampling rate in Hz.
    /// </summary>
    public int MaxSamplingRate { get; init; } = 1000;

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
                HasWifi = true,
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
                HasWifi = true,
                HasWincWifiModule = true,
                HasUsb = true,
                MaxSamplingRate = 16000
            },
            DeviceType.Nyquist3 => new DeviceCapabilities
            {
                SupportsStreaming = true,
                HasSdCard = true,
                HasWifi = true,
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
}
