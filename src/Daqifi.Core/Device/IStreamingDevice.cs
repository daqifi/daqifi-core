namespace Daqifi.Core.Device
{
    /// <summary>
    /// Represents a device that supports data streaming.
    /// </summary>
    public interface IStreamingDevice : IDevice
    {
        /// <summary>
        /// Gets or sets the streaming frequency in Hz.
        /// </summary>
        int StreamingFrequency { get; set; }

        /// <summary>
        /// Gets a value indicating whether the device is currently streaming data.
        /// </summary>
        bool IsStreaming { get; }

        /// <summary>
        /// Starts streaming data from the device.
        /// </summary>
        void StartStreaming();

        /// <summary>
        /// Stops streaming data from the device.
        /// </summary>
        void StopStreaming();
    }
} 