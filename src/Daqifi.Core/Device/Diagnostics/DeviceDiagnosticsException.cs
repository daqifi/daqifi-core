using System;
using System.Collections.Generic;

namespace Daqifi.Core.Device.Diagnostics;

/// <summary>
/// Represents a failure while performing a device diagnostics operation — for example, the
/// device returned a SCPI error or a response that could not be parsed into the expected
/// structured type. Carries the raw response lines so callers can surface or log the underlying
/// device output without re-parsing the wire data.
/// </summary>
public class DeviceDiagnosticsException : DaqifiDeviceException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DeviceDiagnosticsException"/> class.
    /// </summary>
    public DeviceDiagnosticsException(
        string message,
        IReadOnlyList<string> rawDeviceResponse,
        Exception? innerException = null)
        : base(message, rawDeviceResponse, innerException)
    {
    }
}
