namespace Daqifi.Core.Device
{
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
        /// SD-card file transfer (<c>SYSTem:STORage:SD:LIST?</c> / <c>:GET</c> / <c>:DELete</c>)
        /// routed over a WiFi/TCP connection instead of USB. Before firmware v3.7.0 the SD card
        /// and the WiFi module contended for the shared SPI bus, so SD file operations were
        /// USB-only; firmware <c>#598/#599</c> route the SD reply to the requesting interface,
        /// making SD-over-WiFi safe. First released firmware <b>v3.7.0</b>; requires SD hardware.
        /// Over USB these operations are available on all SD-capable firmware and are not gated.
        /// </summary>
        SdFileTransferOverWifi
    }
}
