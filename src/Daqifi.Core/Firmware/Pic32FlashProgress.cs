namespace Daqifi.Core.Firmware;

/// <summary>
/// What the current PIC32 update has done to the application flash, kept as its own value
/// rather than read off <see cref="FirmwareUpdateState"/> (issue #952).
/// </summary>
/// <remarks>
/// The update state says which step the flow is in, and it moves to
/// <see cref="FirmwareUpdateState.ErasingFlash"/> before the erase command is written, so
/// observers see the step begin. Between that transition and the write the flash is still
/// untouched. Deciding "the flash was changed" from the state alone made a failure or a cancel in
/// that interval erase a good application.
/// <para>
/// The erase is the first command of an update that changes the flash, so one step is all this
/// has to record. It only moves forward during an update.
/// </para>
/// </remarks>
internal enum Pic32FlashProgress
{
    /// <summary>No command that changes the application flash has been written.</summary>
    NotChanged,

    /// <summary>
    /// The erase command has been handed to the HID transport. From here a failure has to be
    /// treated as a changed flash, whether or not the write got through.
    /// </summary>
    EraseSent
}
