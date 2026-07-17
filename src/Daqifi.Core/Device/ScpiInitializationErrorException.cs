using System;
using System.Collections.Generic;

#nullable enable

namespace Daqifi.Core.Device
{
    /// <summary>
    /// Represents a SCPI error returned by the device during <see cref="DaqifiDevice.InitializeAsync"/>
    /// (including the streaming-device USB stream-interface step). Thrown after the init sequence's
    /// built-in retry has been exhausted, so consumers can classify this specific, often-transient
    /// failure by type instead of matching on the exception message.
    /// </summary>
    /// <remarks>
    /// A common trigger is the firmware persisting the last-used stream interface across sessions:
    /// a device previously left streaming over WiFi can reject <c>SYSTem:STReam:INTerface 0</c>
    /// (or another init-sequence command) on the very next USB connect, within the tight
    /// response window. See issue #310.
    /// </remarks>
    public class ScpiInitializationErrorException : Exception
    {
        /// <summary>
        /// Gets the raw response lines that were captured from the device when the error
        /// was classified. Useful for diagnostics and for surfacing actionable detail in
        /// higher-level UIs.
        /// </summary>
        public IReadOnlyList<string> RawDeviceResponse { get; }

        /// <summary>
        /// Gets the last SCPI error line observed in the response (e.g.
        /// <c>**ERROR: -200, "Execution error"</c>), or <c>null</c> if none was present.
        /// </summary>
        public string? LastScpiError { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="ScpiInitializationErrorException"/> class.
        /// </summary>
        public ScpiInitializationErrorException(
            string message,
            IReadOnlyList<string> rawDeviceResponse,
            string? lastScpiError = null,
            Exception? innerException = null)
            : base(message, innerException)
        {
            RawDeviceResponse = rawDeviceResponse ?? Array.Empty<string>();
            LastScpiError = lastScpiError;
        }
    }
}
