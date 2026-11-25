using System;

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Thrown when an SD card operation that only works over a USB/serial connection is requested
/// over another transport. Nothing has been sent to the device.
/// </summary>
/// <remarks>
/// Starting a log is the one such operation: it turns the LAN interface off to give the SD card
/// the shared SPI bus, which over a network connection would drop the very link the command
/// arrived on. No firmware version lifts that, which is why this is not a
/// <see cref="FeatureNotSupportedException"/>.
/// </remarks>
public class SdCardRequiresUsbException : SdCardOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SdCardRequiresUsbException"/> class.
    /// </summary>
    /// <param name="message">Which operation needs USB, and why.</param>
    public SdCardRequiresUsbException(string message)
        : base(message, Array.Empty<string>())
    {
    }
}
