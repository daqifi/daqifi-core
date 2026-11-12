using System;
using System.Collections.Generic;
using System.Globalization;

namespace Daqifi.Core.Device.SdCard;

/// <summary>
/// Thrown when the device could not open the SD card file a download asked for because the name
/// does not lead to a file: nothing by that name is on the card, or the device could not use the
/// name at all.
/// </summary>
/// <remarks>
/// <para>
/// On the wire this failure looks exactly like an empty file. The device ends the reply with
/// the same bare <c>__END_OF_FILE__</c> and sends no content. From firmware v3.9.0
/// (<see cref="DeviceFeature.SdGetOpenFailureError"/>) it also queues a SCPI error saying why,
/// and Core reads the error queue after an empty download to find it. Older firmware queues
/// nothing, so the same request still raises <see cref="SdCardEmptyTransferException"/> there.
/// </para>
/// <para>
/// <see cref="ScpiErrorCode"/> carries what the device said. <c>-256</c> ("File name not
/// found") means the name or its directory is not on the card. <c>-257</c> ("File name error")
/// means the device rejected the name itself. The device looks the name up in its configured
/// log directory, not in whatever directory a listing printed.
/// </para>
/// <para>
/// Retrying does not help. A storage fault (no card mounted, access denied, out of file
/// handles) is not reported with this type. It raises the base
/// <see cref="SdCardFilesystemException"/>, so a caller that catches that type gets both.
/// </para>
/// </remarks>
public class SdCardFileNotFoundException : SdCardFilesystemException
{
    /// <summary>
    /// Gets the name of the file the download asked for.
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// Gets the SCPI error code the device reported: <c>-256</c> for a name that is not on the
    /// card, <c>-257</c> for a name the device rejected.
    /// </summary>
    public int ScpiErrorCode { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SdCardFileNotFoundException"/> class.
    /// </summary>
    /// <param name="fileName">The name of the file the download asked for.</param>
    /// <param name="scpiErrorCode">The SCPI error code the device reported.</param>
    /// <param name="rawDeviceResponse">The error-queue entries read after the download.</param>
    /// <param name="lastScpiError">
    /// The error-queue entry that reported the failure, for example
    /// <c>-256,"File name not found"</c>.
    /// </param>
    public SdCardFileNotFoundException(
        string fileName,
        int scpiErrorCode,
        IReadOnlyList<string> rawDeviceResponse,
        string? lastScpiError)
        : base(rawDeviceResponse, lastScpiError, lastScpiError ?? string.Empty)
    {
        FileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
        ScpiErrorCode = scpiErrorCode;
    }

    /// <inheritdoc />
    public override string Message => string.Format(
        CultureInfo.InvariantCulture,
        "The device could not find SD card file '{0}' ({1}).",
        FileName,
        string.IsNullOrWhiteSpace(LastScpiError)
            ? ScpiErrorCode.ToString(CultureInfo.InvariantCulture)
            : LastScpiError);
}
