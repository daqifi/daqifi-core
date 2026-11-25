using System;
using System.Collections.Generic;

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Thrown when an SD card operation cannot proceed because the SD card is busy
/// (for example, the device is actively logging to it, or an earlier download still holds the
/// transport). The message says which when it is not a log.
/// </summary>
public class SdCardBusyException : SdCardOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SdCardBusyException"/> class.
    /// </summary>
    public SdCardBusyException(
        IReadOnlyList<string> rawDeviceResponse,
        string? lastScpiError = null)
        : base(
            "The SD card is busy and cannot complete the requested operation.",
            rawDeviceResponse,
            lastScpiError)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SdCardBusyException"/> class for a refusal
    /// made without asking the device, with a message that says what is holding the card.
    /// </summary>
    /// <param name="message">What is holding the card, and what clears it.</param>
    public SdCardBusyException(string message)
        : base(message, Array.Empty<string>())
    {
    }
}
