using System;
using System.Collections.Generic;

#nullable enable

namespace Daqifi.Core.Device.Diagnostics
{
    /// <summary>
    /// Represents a failure while performing a device diagnostics operation — for example, the
    /// device returned a SCPI error or a response that could not be parsed into the expected
    /// structured type. Carries the raw response lines so callers can surface or log the underlying
    /// device output without re-parsing the wire data.
    /// </summary>
    public class DeviceDiagnosticsException : Exception
    {
        /// <summary>
        /// Gets the raw response lines captured from the device when the failure was detected.
        /// </summary>
        public IReadOnlyList<string> RawDeviceResponse { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="DeviceDiagnosticsException"/> class.
        /// </summary>
        public DeviceDiagnosticsException(
            string message,
            IReadOnlyList<string> rawDeviceResponse,
            Exception? innerException = null)
            : base(message, innerException)
        {
            RawDeviceResponse = rawDeviceResponse ?? Array.Empty<string>();
        }
    }
}
