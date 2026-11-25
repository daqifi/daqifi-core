using System;
using System.Collections.Generic;

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Represents an error returned by the device while performing an SD card operation.
/// Carries the raw response lines so callers can surface the underlying device output
/// to users or logs without having to re-parse the wire data.
/// </summary>
public class SdCardOperationException : DaqifiDeviceException
{
    /// <summary>
    /// Gets the last SCPI error line observed in the response (e.g.
    /// <c>**ERROR: -200, "Execution error"</c>), or <c>null</c> if none was present.
    /// </summary>
    public string? LastScpiError { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SdCardOperationException"/> class.
    /// </summary>
    public SdCardOperationException(
        string message,
        IReadOnlyList<string> rawDeviceResponse,
        string? lastScpiError = null,
        Exception? innerException = null)
        : base(message, rawDeviceResponse, innerException)
    {
        LastScpiError = lastScpiError;
    }
}
