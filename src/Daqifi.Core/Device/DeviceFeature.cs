namespace Daqifi.Core.Device;

/// <summary>
/// Identifies a firmware- or hardware-gated capability that a device may or may not
/// support, carried by <see cref="FeatureNotSupportedException"/> (ADR 0001,
/// docs/adr/0001-firmware-feature-gating.md). New members are added only when
/// daqifi-core starts consuming a command that some supported device might lack.
/// </summary>
public enum DeviceFeature
{
    /// <summary>
    /// Analog output (<c>SOURce:VOLTage:LEVel</c> / <c>CONFigure:DAC:*</c>). Board-gated:
    /// Nyquist3 only.
    /// </summary>
    AnalogOutput,

    /// <summary>
    /// SD card storage capacity query (<c>SYSTem:STORage:SD:SPACe?</c>). Introduced in
    /// firmware v3.4.6b1 — below the <see cref="DaqifiDevice.MinSupportedFirmware"/> floor,
    /// so every supported device already has it. The typed-exception backstop only fires
    /// against below-floor devices, and reports <see cref="DaqifiDevice.MinSupportedFirmware"/>
    /// (not v3.4.6b1) as the required version, since that floor — not the command's original
    /// introduction version — is what daqifi-core actually guarantees.
    /// </summary>
    SdStorageQuery,

    /// <summary>
    /// Capability document query (<c>CONFigure:CAPabilities:JSON?</c> /
    /// <c>:APIVersion?</c>). Firmware-gated: requires firmware &gt;= v3.5.0.
    /// </summary>
    CapabilityDocument,

    /// <summary>
    /// SD-card access over a WiFi/TCP connection instead of USB — file transfer
    /// (<c>SYSTem:STORage:SD:LIST?</c> / <c>:GET</c> / <c>:DELete</c>) and the storage-space
    /// query (<c>SYSTem:STORage:SD:SPACe?</c>), all of which drive the SD card while WiFi is
    /// active. Before firmware v3.7.0 the SD card and the WiFi module contended for the shared
    /// SPI bus, so these were USB-only; firmware <c>#598/#599</c> disable the enable-level SPI
    /// mutex (the Harmony SPI driver arbitrates transactions) and route the SD reply to the
    /// requesting interface, making SD-over-WiFi safe. First released firmware <b>v3.7.0</b>;
    /// requires SD hardware. Over USB these operations are available on all SD-capable firmware
    /// and are not gated.
    /// </summary>
    SdFileTransferOverWifi,

    /// <summary>
    /// The end-of-listing marker on <c>SYSTem:STORage:SD:LISt?</c> (<c>__END_OF_LIST__</c>
    /// followed by <c>OK</c>, <c>INCOMPLETE</c> or <c>FAILED</c>). Firmware-gated: introduced by
    /// firmware <c>#794</c>/<c>#796</c> and first released in <b>v3.8.0</b>; requires SD
    /// hardware.
    /// </summary>
    /// <remarks>
    /// Unlike its siblings this gate does not decide whether to send a command — the listing
    /// query is sent either way — it decides how much a reply can be trusted. On firmware that
    /// emits the marker, a listing that arrives without one did not finish, so Core raises
    /// <see cref="SdCard.SdCardListIncompleteException"/> rather than reporting the entries it
    /// did get as the whole card (closes #887). On older firmware a missing marker is simply
    /// the norm and carries no such information.
    /// </remarks>
    SdListingEndMarker,

    /// <summary>
    /// A truthful PWM frequency floor in the capability document
    /// (<c>channels[].features.pwm.min_freq_hz</c>). Firmware-gated: the firmware computes it
    /// from the peripheral clock the unit actually runs at and refuses <c>DIO:PWM:FREQ</c>
    /// below it, both from firmware <c>#902</c>/<c>#1244</c>, first released in <b>v3.9.0</b>.
    /// </summary>
    /// <remarks>
    /// Like <see cref="SdListingEndMarker"/>, this does not decide whether to send a command; it
    /// decides whether a value the device reports can be believed. Earlier firmware advertises
    /// a fixed 1 Hz that the hardware cannot produce, so there Core keeps
    /// <see cref="DaqifiStreamingDevice.MinPwmFrequencyHz"/> as the floor. See
    /// <see cref="DaqifiStreamingDevice.PwmFrequencyFloorHz"/>.
    /// </remarks>
    PwmFrequencyFloor,

    /// <summary>
    /// Flashing both LEDs so the physical board can be picked out (<c>SYSTem:IDENtify</c> /
    /// <c>SYSTem:IDENtify?</c>). Firmware-gated: introduced by firmware <c>#1220</c> and first
    /// released in <b>v3.9.0</b>; every board has the LEDs.
    /// </summary>
    Identify,

    /// <summary>
    /// CRC-protected SD card writes (<c>SYSTem:STORage:SD:PROTect</c>), set and read by
    /// <see cref="SdCard.ISdCardOperations.SetSdCardCrcProtectionAsync"/> and
    /// <see cref="SdCard.ISdCardOperations.GetSdCardCrcProtectionAsync"/>. Firmware-gated:
    /// introduced by firmware <c>#1217</c>/<c>#1228</c> and first released in <b>v3.9.0</b>;
    /// requires SD hardware.
    /// </summary>
    SdCardCrcProtection
}
