using System.Collections.Generic;

#nullable enable

namespace Daqifi.Core.Device.SdCard
{
    /// <summary>
    /// Thrown when an SD card operation fails because no SD card is installed in the device.
    /// Detected from the firmware's <c>"No SD Card Detected"</c> response.
    /// </summary>
    public class SdCardNotPresentException : SdCardOperationException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SdCardNotPresentException"/> class.
        /// </summary>
        public SdCardNotPresentException(
            IReadOnlyList<string> rawDeviceResponse,
            string? lastScpiError = null)
            : base(
                "No SD card is installed in the device.",
                rawDeviceResponse,
                lastScpiError)
        {
        }
    }
}
