namespace Daqifi.Core.Firmware;

/// <summary>
/// The condition a firmware operation left the PIC32 application flash in when it did not
/// complete. Read it from <see cref="FirmwareUpdateException.FlashCondition"/> or, for an
/// operation that ended without one (a cancellation), from the
/// <see cref="FirmwareUpdateStateChangedEventArgs.FlashCondition"/> of the terminal
/// <see cref="FirmwareUpdateState.Failed"/> / <see cref="FirmwareUpdateState.Recovered"/> transition.
/// </summary>
/// <remarks>
/// This describes what the failed operation did to the PIC32 application flash, and nothing
/// else. A failed WiFi module update reports <see cref="NotChanged"/>: it never writes the PIC32
/// flash, whatever state it left the WiFi module's own firmware in.
/// </remarks>
public enum Pic32FlashCondition
{
    /// <summary>
    /// This operation did not alter the PIC32 application flash: it stopped before any erase or
    /// write, so the flash holds whatever it held before and the operation can be retried. That
    /// is not a statement that the flash holds a working application — an earlier failed update
    /// may already have left it erased or half-flashed — and it says nothing about the WiFi
    /// module's own firmware.
    /// </summary>
    NotChanged,

    /// <summary>
    /// The update failed after touching the flash, and the automatic cleanup re-erased it. The
    /// device has no application but sits in a clean bootloader state, safe to re-flash.
    /// </summary>
    CleanBootloader,

    /// <summary>
    /// The update failed after touching the flash and the cleanup re-erase could not run or did
    /// not succeed. The device may hold a partial image and may not start its application; it
    /// needs the bootloader recovery procedure.
    /// </summary>
    PossiblyHalfFlashed,

    /// <summary>
    /// The image was fully programmed and CRC-verified, but the device was not confirmed back in
    /// application mode afterwards.
    /// </summary>
    ProgrammedNotConfirmed
}
