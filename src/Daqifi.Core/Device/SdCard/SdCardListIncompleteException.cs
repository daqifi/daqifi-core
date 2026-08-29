using System.Collections.Generic;

#nullable enable

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Thrown when an SD card directory listing did not arrive in full: the device either never
/// answered the <c>SYSTem:STORage:SD:LISt?</c> query, or stopped answering part-way through it.
/// </summary>
/// <remarks>
/// <para>
/// The firmware emits no end-of-listing marker and writes nothing at all for an empty directory,
/// so "no bytes received" is byte-for-byte identical to a healthy empty card on the wire. Core
/// therefore appends a <c>SYSTem:ERRor?</c> query to the same text exchange and uses its reply as
/// a terminator: the transport is ordered, so a terminator reply proves both that the device is
/// answering and that everything it had to say about the listing arrived first. This exception
/// is raised when that terminator never came back — which previously surfaced as a healthy-looking
/// "empty SD card" (closes #396).
/// </para>
/// <para>
/// A caller seeing this should treat the listing as unknown, not empty. Typical causes are a
/// silently dropped link, a device that is wedged or powered down, or congestion severe enough
/// to push the reply past the response window. Retrying once the link is known good is
/// reasonable; rendering "0 files" is not.
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
