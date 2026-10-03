using System.Collections.Generic;

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Thrown when an SD card directory listing cannot be trusted as complete: the device never
/// answered the <c>SYSTem:STORage:SD:LISt?</c> query, stopped answering part-way through it, or
/// answered and reported the walk as incomplete or failed.
/// </summary>
/// <remarks>
/// <para>
/// An empty directory produces no file lines, so "no listing bytes" is byte-for-byte identical
/// to a healthy empty card on the wire. Core therefore appends a <c>SYSTem:ERRor?</c> query to
/// the same text exchange and uses its reply as a terminator: the transport is ordered, so a
/// terminator reply proves both that the device is answering and that everything it had to say
/// about the listing arrived first. This exception is raised when that terminator never came
/// back — which previously surfaced as a healthy-looking "empty SD card" (closes #396).
/// </para>
/// <para>
/// It is also raised when the terminator did come back and the device's own end-of-listing
/// marker (<c>__END_OF_LIST__</c>, firmware #794) is anything but <c>OK</c>. <c>FAILED</c> means
/// the directory could not be opened. <c>INCOMPLETE</c>, and any other status word, means the
/// walk skipped entries — <see cref="SdCardFileListParser.GetListingStatus"/> treats an
/// unrecognized word as incomplete. A missing marker on a reply the terminator already closed
/// is the pre-#794 firmware and is not this exception: the exchange completed, and the device
/// simply cannot say whether the walk finished.
/// </para>
/// <para>
/// A caller seeing this should treat the listing as unknown, not empty. Typical causes of a
/// missing terminator are a silently dropped link, a device that is wedged or powered down, or
/// congestion severe enough to push the reply past the response window. Retrying once the link
/// is known good is reasonable; rendering "0 files" is not.
/// </para>
/// </remarks>
public class SdCardListIncompleteException : SdCardOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SdCardListIncompleteException"/> class.
    /// </summary>
    /// <param name="rawDeviceResponse">The raw response lines captured from the device, if any.</param>
    public SdCardListIncompleteException(IReadOnlyList<string> rawDeviceResponse)
        : base(
            "The SD card directory listing did not complete: the device did not finish "
            + "answering the file-list query, so the listing may be missing entries or be "
            + "absent entirely. This is not an empty SD card — check the connection and retry.",
            rawDeviceResponse)
    {
    }
}
