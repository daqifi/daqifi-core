namespace Daqifi.Core.Firmware;

/// <summary>
/// Thrown when a firmware operation was started without a target and more than one PIC32 HID
/// bootloader device is present, so there is no safe way to tell which board the caller meant.
/// </summary>
/// <remarks>
/// A second board can sit in bootloader mode for ordinary reasons — a previous update failed, or
/// the board carries only a bootloader — and a bootloader device advertises no serial number to
/// tell it apart. Picking one would erase and reprogram a board the caller never named, so the
/// operation stops here instead. The caller resolves it by naming the board: pass the target
/// device path or the USB location key to the targeted overload, or disconnect the other boards.
/// Derives from <see cref="FirmwareUpdateException"/> so a caller can catch either this exact
/// safety refusal or any firmware failure, and so the refusal reaches the caller with its own
/// message rather than a generic one.
/// </remarks>
public sealed class AmbiguousBootloaderTargetException : FirmwareUpdateException
{
    /// <summary>
    /// Initializes a new ambiguous-target exception for the bootloader devices that were found.
    /// </summary>
    /// <param name="candidateDevicePaths">
    /// The HID device paths of every bootloader device enumerated when the operation stopped.
    /// Named in the message so the caller can pass one of them back as the target.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="candidateDevicePaths"/> is null.</exception>
    public AmbiguousBootloaderTargetException(IReadOnlyList<string> candidateDevicePaths)
        : base(
            FirmwareUpdateState.WaitingForBootloader,
            "wait for HID bootloader enumeration",
            BuildMessage(candidateDevicePaths),
            "Pass the device path or the USB location key of the board you want to update, " +
            "or disconnect the other bootloader devices and retry.")
    {
        CandidateDevicePaths = candidateDevicePaths;
    }

    /// <summary>
    /// Gets the HID device paths of the bootloader devices that were found.
    /// </summary>
    public IReadOnlyList<string> CandidateDevicePaths { get; }

    private static string BuildMessage(IReadOnlyList<string> candidateDevicePaths)
    {
        ArgumentNullException.ThrowIfNull(candidateDevicePaths);

        return
            $"{candidateDevicePaths.Count} HID bootloader devices are present and no target was " +
            "requested, so the device to update is ambiguous. Refusing to erase a board that was " +
            $"not named. Candidates: {string.Join(", ", candidateDevicePaths)}.";
    }
}
