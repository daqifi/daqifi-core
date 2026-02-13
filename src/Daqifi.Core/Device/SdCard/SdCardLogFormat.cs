namespace Daqifi.Core.Device.SdCard
{
    /// <summary>
    /// Specifies the logging format used when writing data to the SD card.
    /// </summary>
    /// <remarks>
    /// Corresponds to the <c>SYSTem:STReam:FORmat</c> SCPI command values.
    /// </remarks>
    public enum SdCardLogFormat
    {
        /// <summary>
        /// Binary Protocol Buffer format (.bin). Default format for compact, efficient storage.
        /// </summary>
        Protobuf = 0,

        /// <summary>
        /// JSON text format (.json). Human-readable format for easy inspection.
        /// </summary>
        Json = 1,

        /// <summary>
        /// CSV text format (.csv). Comma-separated values for easy import into spreadsheet tools.
        /// </summary>
        Csv = 2,
    }
}
