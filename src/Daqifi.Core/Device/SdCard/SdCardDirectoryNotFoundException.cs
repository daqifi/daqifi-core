using System.Collections.Generic;

#nullable enable

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Thrown when the device could not open the requested SD directory because it
/// is not on the card — as opposed to a corrupt filesystem or an I/O fault,
/// which surface as the plain <see cref="SdCardFilesystemException"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is a normal state, not a fault. A freshly formatted card has no log
/// directory until the first capture writes one, so a client that renders every
/// filesystem error the same way shows the user a scary message for "you have
/// not logged anything yet". Callers that want that distinction catch this
/// type; callers that do not still catch
/// <see cref="SdCardFilesystemException"/> and behave exactly as before.
/// </para>
/// <para>
/// Detected from the numeric code in the firmware's
/// <c>"[Error:N]Failed to open directory [path]"</c> line. <c>N</c> is a
/// Harmony <c>SYS_FS_ERROR</c>, whose "the path is not there" members are
/// <c>SYS_FS_ERROR_NO_FILE = 4</c> and <c>SYS_FS_ERROR_NO_PATH = 5</c>
/// (sequential from <c>SYS_FS_ERROR_OK = 0</c>; see the firmware's
/// <c>config/default/system/fs/sys_fs.h</c>). Coupling to an enum's ordinals is
/// not lovely, but the alternative is guessing from prose, and the failure mode
/// if Harmony ever renumbers is a slightly less specific exception type rather
/// than a wrong answer: an unrecognised code still throws
/// <see cref="SdCardFilesystemException"/>.
/// </para>
/// <para>
/// Reachable only on firmware carrying daqifi-nyquist-firmware#798. Before it,
/// listing a directory that did not exist <em>created</em> it and reported it
/// empty, so this condition could not be observed at all.
/// </para>
/// </remarks>
public class SdCardDirectoryNotFoundException : SdCardFilesystemException
{
    /// <summary>
    /// The directory the device could not find, as it appeared in the device's
    /// message, or <c>null</c> when the message carried no bracketed path.
    /// </summary>
    public string? DirectoryPath { get; }

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="SdCardDirectoryNotFoundException"/> class.
    /// </summary>
    public SdCardDirectoryNotFoundException(
        IReadOnlyList<string> rawDeviceResponse,
        string? lastScpiError,
        string deviceMessage,
        string? directoryPath)
        : base(rawDeviceResponse, lastScpiError, deviceMessage)
    {
        DirectoryPath = directoryPath;
    }
}
