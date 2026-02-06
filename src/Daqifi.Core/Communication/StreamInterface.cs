namespace Daqifi.Core.Communication;

/// <summary>
/// Specifies the target interface for data streaming.
/// </summary>
/// <remarks>
/// Controls where the device sends streaming data. Values correspond to firmware
/// SCPI command <c>SYSTem:STReam:INTerface</c> parameter values.
/// </remarks>
public enum StreamInterface
{
    /// <summary>
    /// Stream data over USB.
    /// </summary>
    Usb = 0,

    /// <summary>
    /// Stream data over WiFi.
    /// </summary>
    WiFi = 1,

    /// <summary>
    /// Stream data to the SD card.
    /// </summary>
    SdCard = 2,

    /// <summary>
    /// Stream data to all interfaces simultaneously.
    /// </summary>
    All = 3
}
