namespace Daqifi.Core.Firmware;

/// <summary>
/// Lightweight PIC32 HID-bootloader diagnostics that run <em>outside</em> the full
/// <see cref="IFirmwareUpdateService.UpdateFirmwareAsync(Daqifi.Core.Device.IStreamingDevice, string, System.IProgress{FirmwareUpdateProgress}?, System.Threading.CancellationToken)"/>
/// flow: a version health check and a <c>JMP_TO_APP</c> soft reset, neither of which
/// erases or programs flash. Intended for consumers (e.g. a recovery/manual bootloader
/// dialog) that want to probe or reset a bootloader session before committing to a flash.
/// </summary>
/// <remarks>
/// <para>
/// Implementations share the same HID transport and operation serialization as the full update
/// flow, so a diagnostic never runs concurrently with an in-flight update. A call made from a
/// <em>separate</em> execution context while an update is running <em>waits</em> for that update
/// to release the shared lock and then proceeds — it does not throw. Only a call re-entered from
/// within an in-flight operation's own synchronous callback (progress or state-change) is
/// rejected, because the shared lock is not reentrant and a diagnostic owns the HID
/// connect/version/reset exchange.
/// </para>
/// <para>
/// <em>Bootloader operation</em> failures (enumeration, connect, version, or reset) are wrapped in
/// <see cref="FirmwareUpdateException"/> (carrying
/// <see cref="FirmwareUpdateException.RecoveryGuidance"/>), consistent with the full update flow.
/// Precondition, lifecycle, and concurrency failures are thrown directly instead:
/// <see cref="System.ArgumentException"/> for a whitespace target path,
/// <see cref="System.ObjectDisposedException"/> when the service is disposed,
/// <see cref="System.InvalidOperationException"/> for callback reentrancy or a non-idle service,
/// and <see cref="System.OperationCanceledException"/> when the supplied token is canceled.
/// </para>
/// <para>
/// <strong>These calls are not instantaneous when no bootloader is present.</strong> Waiting for
/// the HID bootloader to enumerate is bounded by
/// <c>FirmwareUpdateServiceOptions.WaitingForBootloaderTimeout</c> (45 seconds by default), so a
/// probe against a device that is <em>not</em> in bootloader mode blocks for that long before
/// failing. A UI that probes opportunistically — e.g. when a device is first grabbed — should pass
/// a <see cref="System.Threading.CancellationToken"/> with its own deadline, or configure a
/// shorter timeout.
/// </para>
/// <para>
/// Unlike the full update flow, a failed health check does <em>not</em> attempt the
/// <c>JMP_TO_APP</c> self-recovery that flow applies before giving up: a check reports what it
/// finds rather than mutating device state. A failed
/// <see cref="CheckBootloaderHealthAsync"/> therefore does not imply an update would also fail.
/// Callers that want the remedy should invoke <see cref="ResetBootloaderAsync"/> explicitly.
/// </para>
/// </remarks>
public interface IPic32BootloaderDiagnostics
{
    /// <summary>
    /// Connects to the HID bootloader at the given device path (or the first match when
    /// <paramref name="targetDevicePath"/> is null) and reads its version as a health check,
    /// without erasing or programming flash. The HID transport is disconnected before the
    /// call returns.
    /// </summary>
    /// <param name="targetDevicePath">
    /// HID device path identifying which bootloader to probe (from discovery's
    /// <c>HidDeviceInfo.DevicePath</c>). Null probes the first enumerated bootloader.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The bootloader version string on success.</returns>
    /// <exception cref="FirmwareUpdateException">
    /// Thrown when the bootloader could not be enumerated, connected to, or returned an
    /// invalid version response. The exception's <see cref="FirmwareUpdateException.FailedState"/>
    /// and <see cref="FirmwareUpdateException.RecoveryGuidance"/> describe where the health
    /// check failed.
    /// </exception>
    /// <exception cref="System.ArgumentException">
    /// Thrown when <paramref name="targetDevicePath"/> is non-null but whitespace.
    /// </exception>
    /// <exception cref="System.ObjectDisposedException">Thrown when the service has been disposed.</exception>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when re-entered from within an in-flight firmware operation's own synchronous
    /// callback, or when the service is not in an idle state. A concurrent call from a separate
    /// execution context waits for the in-flight operation rather than throwing.
    /// </exception>
    /// <exception cref="System.OperationCanceledException">
    /// Thrown when <paramref name="cancellationToken"/> is canceled.
    /// </exception>
    Task<string> CheckBootloaderHealthAsync(
        string? targetDevicePath = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Issues a <c>JMP_TO_APP</c> soft reset to the HID bootloader at the given device path
    /// (or the first match when <paramref name="targetDevicePath"/> is null), forcing a clean
    /// USB re-enumeration back into application mode, without touching flash contents. The HID
    /// transport is disconnected before the call returns.
    /// </summary>
    /// <param name="targetDevicePath">
    /// HID device path identifying which bootloader to reset (from discovery's
    /// <c>HidDeviceInfo.DevicePath</c>). Null resets the first enumerated bootloader.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="FirmwareUpdateException">
    /// Thrown when the bootloader could not be enumerated, connected to, or the soft-reset
    /// message could not be written. The exception's <see cref="FirmwareUpdateException.FailedState"/>
    /// and <see cref="FirmwareUpdateException.RecoveryGuidance"/> describe where the reset failed.
    /// </exception>
    /// <exception cref="System.ArgumentException">
    /// Thrown when <paramref name="targetDevicePath"/> is non-null but whitespace.
    /// </exception>
    /// <exception cref="System.ObjectDisposedException">Thrown when the service has been disposed.</exception>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when re-entered from within an in-flight firmware operation's own synchronous
    /// callback, or when the service is not in an idle state. A concurrent call from a separate
    /// execution context waits for the in-flight operation rather than throwing.
    /// </exception>
    /// <exception cref="System.OperationCanceledException">
    /// Thrown when <paramref name="cancellationToken"/> is canceled.
    /// </exception>
    Task ResetBootloaderAsync(
        string? targetDevicePath = null,
        CancellationToken cancellationToken = default);
}
