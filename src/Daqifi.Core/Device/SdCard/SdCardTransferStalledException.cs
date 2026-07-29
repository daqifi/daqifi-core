using System;
using System.Globalization;

#nullable enable

namespace Daqifi.Core.Device.SdCard
{
    /// <summary>
    /// Thrown when an SD card file download stops making progress before the
    /// <c>__END_OF_FILE__</c> marker arrives — the transport went quiet, was closed, or the
    /// overall transfer deadline elapsed. <see cref="Reason"/> says which, so a caller can treat
    /// a stall as an expected, retryable condition rather than a defect.
    /// </summary>
    public class SdCardTransferStalledException : SdCardOperationException
    {
        /// <summary>
        /// Gets the name of the file whose transfer stalled.
        /// </summary>
        public string FileName { get; }

        /// <summary>
        /// Gets the number of file bytes received before the transfer stalled. A non-zero value
        /// means the transfer had started and the received prefix is a partial file.
        /// </summary>
        public long BytesReceived { get; }

        /// <summary>
        /// Gets the reason the transfer stalled.
        /// </summary>
        public SdCardTransferStallReason Reason { get; }

        /// <summary>
        /// Gets the overall transfer deadline that elapsed, when
        /// <see cref="Reason"/> is <see cref="SdCardTransferStallReason.TransferTimeout"/>;
        /// otherwise <c>null</c>.
        /// </summary>
        public TimeSpan? Timeout { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="SdCardTransferStalledException"/> class.
        /// </summary>
        /// <param name="fileName">The name of the file whose transfer stalled.</param>
        /// <param name="bytesReceived">File bytes received before the stall.</param>
        /// <param name="reason">Why the transfer stalled.</param>
        /// <param name="timeout">
        /// The elapsed transfer deadline, for
        /// <see cref="SdCardTransferStallReason.TransferTimeout"/>.
        /// </param>
        /// <param name="innerException">Optional inner exception.</param>
        public SdCardTransferStalledException(
            string fileName,
            long bytesReceived,
            SdCardTransferStallReason reason,
            TimeSpan? timeout = null,
            Exception? innerException = null)
            : base(
                BuildMessage(fileName, bytesReceived, reason, timeout),
                Array.Empty<string>(),
                lastScpiError: null,
                innerException: innerException)
        {
            FileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
            BytesReceived = bytesReceived;
            Reason = reason;
            Timeout = timeout;
        }

        private static string BuildMessage(
            string fileName,
            long bytesReceived,
            SdCardTransferStallReason reason,
            TimeSpan? timeout)
        {
            var received = string.Format(
                CultureInfo.InvariantCulture,
                "Received {0} bytes before the transfer stalled.",
                bytesReceived);

            return reason switch
            {
                SdCardTransferStallReason.TransportClosed =>
                    $"The transport stream for SD card file '{fileName}' closed before the EOF marker " +
                    $"arrived. {received}",
                SdCardTransferStallReason.TransferTimeout =>
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "SD card file download of '{0}' timed out after {1:F0} seconds without receiving " +
                        "the EOF marker. {2}",
                        fileName,
                        (timeout ?? TimeSpan.Zero).TotalSeconds,
                        received),
                _ =>
                    $"The device stopped sending data for SD card file '{fileName}' before the EOF marker " +
                    $"arrived. {received} The transport is still open, so this is a stalled read rather " +
                    "than a closed stream; retry the download."
            };
        }
    }
}
