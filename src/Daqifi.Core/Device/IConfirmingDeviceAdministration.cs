using System;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace Daqifi.Core.Device
{
    /// <summary>
    /// The confirming counterparts to the fire-and-forget device-administration commands on
    /// <see cref="IStreamingDevice"/>: each sends the same firmware primitive and then asks the device
    /// whether it accepted it, so a refusal cannot pass for a success.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IStreamingDevice.LoadAdcCalibration"/> and the rest of that block return <c>void</c>
    /// and parse no reply. Bench evidence on a Nyquist running firmware 3.7.2 shows what that costs:
    /// <c>CONFigure:ADC:LOADcal</c> answers <c>-200,"Execution error"</c> on a unit whose user
    /// calibration bank was never written, and the caller is told nothing — "load the user calibration
    /// bank" is a silent no-op that reports success. Sending the identical primitive by hand produced
    /// the same refusal, so this is the firmware's behavior rather than a defect in Core; what Core
    /// owes the caller is a way to find out.
    /// </para>
    /// <para>
    /// This is a separate interface rather than more members on <see cref="IStreamingDevice"/> so
    /// existing implementers of that interface keep compiling, and it follows the same shape as the
    /// other capability slices this device exposes
    /// (<see cref="Network.INetworkConfigurable"/>, <see cref="SdCard.ISdCardOperations"/>,
    /// <see cref="Firmware.ILanChipInfoProvider"/>, <see cref="Diagnostics.IDeviceDiagnostics"/>) — test for it
    /// before use:
    /// </para>
    /// <code>
    /// if (device is IConfirmingDeviceAdministration admin)
    /// {
    ///     await admin.LoadAdcCalibrationAsync(cancellationToken);
    /// }
    /// </code>
    /// <para>
    /// Confirmation is not free: it needs text exchanges rather than a single write, so the
    /// <c>void</c> commands remain the right choice while streaming or when a silent no-op is
    /// acceptable. Nothing here changes what goes on the wire for those. Measured on the bench
    /// Nyquist (fw 3.7.2, USB CDC), one confirming command costs about <b>3 seconds</b> — a drain
    /// exchange plus the confirming exchange, each of which pauses the protobuf consumer and holds
    /// the device's operation lock. Budget for that before putting one in a loop over 16 channels.
    /// </para>
    /// </remarks>
    public interface IConfirmingDeviceAdministration
    {
        /// <summary>
        /// Persists the device's current ADC calibration coefficients to the <b>user</b> NVM bank, and confirms
        /// the device accepted the command.
        /// </summary>
        /// <inheritdoc cref="LoadAdcCalibrationAsync" path="/remarks" />
        /// <param name="cancellationToken">A cancellation token to observe while the exchange runs.</param>
        /// <returns>A task that completes once the device has confirmed the command.</returns>
        /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
        /// <exception cref="DeviceCommandFailedException">Thrown when the device refused the command, or did not confirm it.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
        Task SaveAdcCalibrationAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Restores the device's ADC calibration coefficients from the <b>user</b> NVM bank into its runtime, and
        /// confirms the device accepted the command.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Sends the same firmware primitive as the <c>void</c> command it mirrors, then reads the device's SCPI
        /// error queue so a refusal surfaces as <see cref="DeviceCommandFailedException"/> instead of passing for
        /// success. The queue is drained first, so the entry read afterwards belongs to this command and not to an
        /// older one — which also means anything the queue was already holding is discarded; read it with
        /// <see cref="DaqifiDevice.DrainErrorQueueAsync"/> beforehand if those entries matter.
        /// </para>
        /// <para>
        /// This costs two or more text exchanges, each of which pauses the protobuf consumer and takes the
        /// device's operation lock. Do not call it during active streaming or concurrently with another device
        /// operation; the <c>void</c> command remains the one to use in those situations.
        /// </para>
        /// </remarks>
        /// <param name="cancellationToken">A cancellation token to observe while the exchange runs.</param>
        /// <returns>A task that completes once the device has confirmed the command.</returns>
        /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
        /// <exception cref="DeviceCommandFailedException">Thrown when the device refused the command, or did not confirm it.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
        Task LoadAdcCalibrationAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Sets a single channel's ADC calibration slope (CalM) in device RAM, and confirms the device accepted
        /// the command.
        /// </summary>
        /// <inheritdoc cref="LoadAdcCalibrationAsync" path="/remarks" />
        /// <param name="channelNumber">The analog input channel number.</param>
        /// <param name="calM">The calibration slope (gain) coefficient.</param>
        /// <param name="cancellationToken">A cancellation token to observe while the exchange runs.</param>
        /// <returns>A task that completes once the device has confirmed the command.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="channelNumber"/> is negative.</exception>
        /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
        /// <exception cref="DeviceCommandFailedException">Thrown when the device refused the command, or did not confirm it.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
        Task SetAdcCalibrationSlopeAsync(int channelNumber, double calM, CancellationToken cancellationToken = default);

        /// <summary>
        /// Sets a single channel's ADC calibration offset (CalB) in device RAM, and confirms the device accepted
        /// the command.
        /// </summary>
        /// <inheritdoc cref="LoadAdcCalibrationAsync" path="/remarks" />
        /// <param name="channelNumber">The analog input channel number.</param>
        /// <param name="calB">The calibration offset coefficient.</param>
        /// <param name="cancellationToken">A cancellation token to observe while the exchange runs.</param>
        /// <returns>A task that completes once the device has confirmed the command.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="channelNumber"/> is negative.</exception>
        /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
        /// <exception cref="DeviceCommandFailedException">Thrown when the device refused the command, or did not confirm it.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
        Task SetAdcCalibrationOffsetAsync(int channelNumber, double calB, CancellationToken cancellationToken = default);

        /// <summary>
        /// Persists the device's current ADC calibration coefficients to the <b>factory</b> NVM bank, and confirms
        /// the device accepted the command.
        /// </summary>
        /// <inheritdoc cref="LoadAdcCalibrationAsync" path="/remarks" />
        /// <param name="cancellationToken">A cancellation token to observe while the exchange runs.</param>
        /// <returns>A task that completes once the device has confirmed the command.</returns>
        /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
        /// <exception cref="DeviceCommandFailedException">Thrown when the device refused the command, or did not confirm it.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
        Task SaveFactoryAdcCalibrationAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Restores the device's ADC calibration coefficients from the <b>factory</b> NVM bank into its runtime,
        /// and confirms the device accepted the command.
        /// </summary>
        /// <inheritdoc cref="LoadAdcCalibrationAsync" path="/remarks" />
        /// <param name="cancellationToken">A cancellation token to observe while the exchange runs.</param>
        /// <returns>A task that completes once the device has confirmed the command.</returns>
        /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
        /// <exception cref="DeviceCommandFailedException">Thrown when the device refused the command, or did not confirm it.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
        Task LoadFactoryAdcCalibrationAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Selects which ADC calibration bank the device applies (<c>0</c> = factory, <c>1</c> = user), and
        /// confirms the device accepted the command.
        /// </summary>
        /// <inheritdoc cref="LoadAdcCalibrationAsync" path="/remarks" />
        /// <param name="bank">The calibration bank: <c>0</c> = factory, <c>1</c> = user.</param>
        /// <param name="cancellationToken">A cancellation token to observe while the exchange runs.</param>
        /// <returns>A task that completes once the device has confirmed the command.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="bank"/> is not 0 or 1.</exception>
        /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
        /// <exception cref="DeviceCommandFailedException">Thrown when the device refused the command, or did not confirm it.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
        Task UseAdcCalibrationAsync(int bank, CancellationToken cancellationToken = default);

        /// <summary>
        /// Persists the device's current voltage precision setting to NVM, and confirms the device accepted the
        /// command.
        /// </summary>
        /// <inheritdoc cref="LoadAdcCalibrationAsync" path="/remarks" />
        /// <param name="cancellationToken">A cancellation token to observe while the exchange runs.</param>
        /// <returns>A task that completes once the device has confirmed the command.</returns>
        /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
        /// <exception cref="DeviceCommandFailedException">Thrown when the device refused the command, or did not confirm it.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
        Task SaveVoltagePrecisionAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Restores the device's voltage precision setting from NVM into its runtime, and confirms the device
        /// accepted the command.
        /// </summary>
        /// <inheritdoc cref="LoadAdcCalibrationAsync" path="/remarks" />
        /// <param name="cancellationToken">A cancellation token to observe while the exchange runs.</param>
        /// <returns>A task that completes once the device has confirmed the command.</returns>
        /// <exception cref="DeviceNotConnectedException">Thrown when the device is not connected.</exception>
        /// <exception cref="DeviceCommandFailedException">Thrown when the device refused the command, or did not confirm it.</exception>
        /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
        Task LoadVoltagePrecisionAsync(CancellationToken cancellationToken = default);
    }
}
