using System;
using System.Collections.Generic;

#nullable enable

namespace Daqifi.Core.Device.Diagnostics
{
    /// <summary>
    /// Thrown when a diagnostics reply arrived with non-text bytes welded into it, so the values it
    /// carries are incomplete and must not be reported as a result (issue #537).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cause is asking a streaming device for its counters. The diagnostics queries deliberately
    /// do not stop the stream, but the firmware keeps emitting protobuf frames throughout the
    /// exchange, and whatever was in flight when the reply began lands on the front of its first
    /// line. The tolerant key/value parser then drops that line as unrecognised, which is what used
    /// to turn a mangled reply into a healthy-looking one with a counter quietly missing.
    /// </para>
    /// <para>
    /// The remedy is to stop streaming and query again — a retry while the stream is still running
    /// hits the same interference, so Core does not attempt one on the caller's behalf.
    /// <see cref="DeviceDiagnosticsException.RawDeviceResponse"/> carries the reply exactly as it
    /// arrived, mangled line included, for callers that want to log or salvage it.
    /// </para>
    /// </remarks>
    public sealed class DeviceDiagnosticsCorruptedResponseException : DeviceDiagnosticsException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="DeviceDiagnosticsCorruptedResponseException"/> class.
        /// </summary>
        public DeviceDiagnosticsCorruptedResponseException(
            string message,
            IReadOnlyList<string> rawDeviceResponse,
            Exception? innerException = null)
            : base(message, rawDeviceResponse, innerException)
        {
        }
    }
}
