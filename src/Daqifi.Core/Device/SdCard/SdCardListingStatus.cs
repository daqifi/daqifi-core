#nullable enable

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// How a <c>SYSTem:STORage:SD:LISt?</c> reply ended.
/// </summary>
/// <remarks>
/// The device appends an end-of-listing marker so a host can tell a finished
/// reply from a truncated one (firmware #794). Before that, a listing simply
/// stopped: a complete one and one cut short by a read timeout, a buffer
/// boundary or a stall-abort were byte-identical, so a client could never
/// prove a file was ABSENT rather than merely unreached.
/// </remarks>
public enum SdCardListingStatus
{
    /// <summary>
    /// No marker was present. Either the firmware predates #794, or the reply
    /// was cut short — including the abort case, which deliberately sends no
    /// marker because the peer had stopped draining. NOT an error, and NOT a
    /// clean bill of health: the listing may be missing entries, so absence
    /// of a file must not be reported as fact.
    /// </summary>
    Unterminated = 0,

    /// <summary>The device walked the whole tree and emitted every entry.</summary>
    Complete,

    /// <summary>
    /// The walk finished but skipped entries — the directory-depth cap, a
    /// subdirectory it could not read or open, a path or entry that did not
    /// fit. The files listed are real; the list is not all of them.
    /// </summary>
    Incomplete,

    /// <summary>
    /// Nothing could be listed at all — the directory would not open, or the
    /// path was too long. An empty result here means "I could not look", not
    /// "there is nothing there".
    /// </summary>
    Failed,
}
