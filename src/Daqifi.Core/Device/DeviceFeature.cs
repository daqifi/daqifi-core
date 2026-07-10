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
        /// SD card storage capacity query (<c>SYSTem:STORage:SD:SPACe?</c>). Firmware-gated:
        /// requires firmware &gt;= v3.4.6b1, at or below the <see cref="DaqifiDevice.MinSupportedFirmware"/> floor.
        /// </summary>
        SdStorageQuery,

        /// <summary>
        /// Capability document query (<c>CONFigure:CAPabilities:JSON?</c> /
        /// <c>:APIVersion?</c>). Firmware-gated: requires firmware &gt;= v3.5.0.
        /// </summary>
        CapabilityDocument
    }
}
