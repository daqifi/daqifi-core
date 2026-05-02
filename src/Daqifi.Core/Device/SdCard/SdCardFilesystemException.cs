using System.Collections.Generic;

#nullable enable

namespace Daqifi.Core.Device.SdCard
{
    /// <summary>
    /// Thrown when the device's SD card filesystem cannot satisfy a request — for
    /// example, the directory cannot be opened because the card is unformatted,
    /// the filesystem is corrupt, or the path does not exist.
    /// Detected from the firmware's <c>"[Error:N]Failed to open directory ..."</c>
    /// response.
    /// </summary>
    public class SdCardFilesystemException : SdCardOperationException
    {
        /// <summary>
        /// Gets the raw filesystem error line emitted by the device firmware,
        /// for example <c>"[Error:3]Failed to open directory /Daqifi"</c>.
        /// </summary>
        public string DeviceMessage { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="SdCardFilesystemException"/> class.
        /// </summary>
        public SdCardFilesystemException(
            IReadOnlyList<string> rawDeviceResponse,
            string? lastScpiError,
            string deviceMessage)
            : base(
                BuildMessage(deviceMessage),
                rawDeviceResponse,
                lastScpiError)
        {
            DeviceMessage = deviceMessage;
        }

        private static string BuildMessage(string deviceMessage)
        {
            return string.IsNullOrWhiteSpace(deviceMessage)
                ? "The SD card filesystem reported an error."
                : $"The SD card filesystem reported an error: {deviceMessage}";
        }
    }
}
