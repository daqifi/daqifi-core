using System.Collections.Generic;

#nullable enable

namespace Daqifi.Core.Device.SdCard
{
    /// <summary>
    /// Thrown when an SD card operation cannot proceed because the SD card is busy
    /// (for example, the device is actively logging to it).
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
    }
}
