using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace Daqifi.Core.Device.Internal
{
    /// <summary>
    /// The device-administration half of <see cref="IStreamingDevice"/> — reboot, the ADC
    /// calibration banks, voltage-precision persistence, and the friendly-name write — extracted
    /// from <see cref="DaqifiStreamingDevice"/> (#344) so the device delegates rather than hosts it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are fire-and-forget SCPI commands with no reply to parse: each validates its arguments,
    /// checks the connection, and sends. They are grouped here because they share that shape and
    /// because none of them touches the channel collection, the streaming session, or any device
    /// state — the two exceptions being <see cref="Reboot"/>'s local teardown and
    /// <see cref="SetFriendlyNameAsync"/>'s optimistic metadata write, both of which go back through
    /// the host rather than being done here.
    /// </para>
    /// <para>
    /// Each of those commands also has a confirming <c>...Async</c> twin here, which sends the same
    /// primitive and then reads the device's SCPI error queue so a refusal cannot pass for a success
    /// (see <see cref="SendConfirmedAsync"/>). The two shapes are kept side by side deliberately: the
    /// <c>void</c> ones stay usable mid-stream and cost one write, while the confirming ones need
    /// text exchanges and the device's full attention.
    /// </para>
    /// <para>
    /// Everything reaches the device through <see cref="IDeviceOperationHost"/>, so each command
    /// still passes through the device's own virtual <c>Send</c> and any subclass override of it.
    /// </para>
    /// </remarks>
    internal sealed class DeviceAdministrationOperations
    {
        private readonly IDeviceOperationHost _host;

        internal DeviceAdministrationOperations(IDeviceOperationHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        /// <inheritdoc cref="DaqifiStreamingDevice.SetFriendlyNameAsync(string, CancellationToken)" />
        internal Task SetFriendlyNameAsync(string name, CancellationToken cancellationToken = default)
        {
            if (name is null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            if (!ScpiMessageProducer.IsFriendlyNameValid(name))
            {
                throw new ArgumentException(
                    $"Device name must be 1-{ScpiMessageProducer.MaxFriendlyNameLength} printable ASCII characters and cannot contain '\"' or '\\'.",
                    nameof(name));
            }

            _host.EnsureConnected(cancellationToken);

            _host.Send(ScpiMessageProducer.SetDeviceName(name));
            _host.Send(ScpiMessageProducer.SaveDeviceName);
            _host.Metadata.FriendlyName = name;

            return Task.CompletedTask;
        }

        /// <inheritdoc cref="IStreamingDevice.Reboot" />
        internal void Reboot()
        {
            _host.EnsureConnected();

            _host.Send(ScpiMessageProducer.RebootDevice);

            // The device drops its link while restarting, so tear down the local
            // connection rather than leaving a stale one that reports Connected.
            _host.Disconnect();
        }

        /// <inheritdoc cref="IStreamingDevice.SaveAdcCalibration" />
        internal void SaveAdcCalibration()
        {
            _host.EnsureConnected();

            _host.Send(ScpiMessageProducer.SaveAdcCalibration);
        }

        /// <inheritdoc cref="IStreamingDevice.LoadAdcCalibration" />
        internal void LoadAdcCalibration()
        {
            _host.EnsureConnected();

            _host.Send(ScpiMessageProducer.LoadAdcCalibration);
        }

        /// <inheritdoc cref="IStreamingDevice.SetAdcCalibrationSlope" />
        internal void SetAdcCalibrationSlope(int channelNumber, double calM)
        {
            ValidateChannelNumber(channelNumber);

            _host.EnsureConnected();

            _host.Send(ScpiMessageProducer.SetAdcCalibrationSlope(channelNumber, calM));
        }

        /// <inheritdoc cref="IStreamingDevice.SetAdcCalibrationOffset" />
        internal void SetAdcCalibrationOffset(int channelNumber, double calB)
        {
            ValidateChannelNumber(channelNumber);

            _host.EnsureConnected();

            _host.Send(ScpiMessageProducer.SetAdcCalibrationOffset(channelNumber, calB));
        }

        /// <inheritdoc cref="IStreamingDevice.SaveFactoryAdcCalibration" />
        internal void SaveFactoryAdcCalibration()
        {
            _host.EnsureConnected();

            _host.Send(ScpiMessageProducer.SaveFactoryAdcCalibration);
        }

        /// <inheritdoc cref="IStreamingDevice.LoadFactoryAdcCalibration" />
        internal void LoadFactoryAdcCalibration()
        {
            _host.EnsureConnected();

            _host.Send(ScpiMessageProducer.LoadFactoryAdcCalibration);
        }

        /// <inheritdoc cref="IStreamingDevice.UseAdcCalibration" />
        internal void UseAdcCalibration(int bank)
        {
            ValidateBank(bank);

            _host.EnsureConnected();

            _host.Send(ScpiMessageProducer.UseAdcCalibration(bank));
        }

        /// <inheritdoc cref="IStreamingDevice.SaveVoltagePrecision" />
        internal void SaveVoltagePrecision()
        {
            _host.EnsureConnected();

            _host.Send(ScpiMessageProducer.SaveVoltagePrecision);
        }

        /// <inheritdoc cref="IStreamingDevice.LoadVoltagePrecision" />
        internal void LoadVoltagePrecision()
        {
            _host.EnsureConnected();

            _host.Send(ScpiMessageProducer.LoadVoltagePrecision);
        }

        #region Confirming variants

        /// <inheritdoc cref="IConfirmingDeviceAdministration.SaveAdcCalibrationAsync" />
        internal Task SaveAdcCalibrationAsync(CancellationToken cancellationToken = default)
            => SendConfirmedAsync(ScpiMessageProducer.SaveAdcCalibration, cancellationToken);

        /// <inheritdoc cref="IConfirmingDeviceAdministration.LoadAdcCalibrationAsync" />
        internal Task LoadAdcCalibrationAsync(CancellationToken cancellationToken = default)
            => SendConfirmedAsync(ScpiMessageProducer.LoadAdcCalibration, cancellationToken);

        /// <inheritdoc cref="IConfirmingDeviceAdministration.SetAdcCalibrationSlopeAsync" />
        internal Task SetAdcCalibrationSlopeAsync(int channelNumber, double calM, CancellationToken cancellationToken = default)
        {
            ValidateChannelNumber(channelNumber);

            return SendConfirmedAsync(ScpiMessageProducer.SetAdcCalibrationSlope(channelNumber, calM), cancellationToken);
        }

        /// <inheritdoc cref="IConfirmingDeviceAdministration.SetAdcCalibrationOffsetAsync" />
        internal Task SetAdcCalibrationOffsetAsync(int channelNumber, double calB, CancellationToken cancellationToken = default)
        {
            ValidateChannelNumber(channelNumber);

            return SendConfirmedAsync(ScpiMessageProducer.SetAdcCalibrationOffset(channelNumber, calB), cancellationToken);
        }

        /// <inheritdoc cref="IConfirmingDeviceAdministration.SaveFactoryAdcCalibrationAsync" />
        internal Task SaveFactoryAdcCalibrationAsync(CancellationToken cancellationToken = default)
            => SendConfirmedAsync(ScpiMessageProducer.SaveFactoryAdcCalibration, cancellationToken);

        /// <inheritdoc cref="IConfirmingDeviceAdministration.LoadFactoryAdcCalibrationAsync" />
        internal Task LoadFactoryAdcCalibrationAsync(CancellationToken cancellationToken = default)
            => SendConfirmedAsync(ScpiMessageProducer.LoadFactoryAdcCalibration, cancellationToken);

        /// <inheritdoc cref="IConfirmingDeviceAdministration.UseAdcCalibrationAsync" />
        internal Task UseAdcCalibrationAsync(int bank, CancellationToken cancellationToken = default)
        {
            ValidateBank(bank);

            return SendConfirmedAsync(ScpiMessageProducer.UseAdcCalibration(bank), cancellationToken);
        }

        /// <inheritdoc cref="IConfirmingDeviceAdministration.SaveVoltagePrecisionAsync" />
        internal Task SaveVoltagePrecisionAsync(CancellationToken cancellationToken = default)
            => SendConfirmedAsync(ScpiMessageProducer.SaveVoltagePrecision, cancellationToken);

        /// <inheritdoc cref="IConfirmingDeviceAdministration.LoadVoltagePrecisionAsync" />
        internal Task LoadVoltagePrecisionAsync(CancellationToken cancellationToken = default)
            => SendConfirmedAsync(ScpiMessageProducer.LoadVoltagePrecision, cancellationToken);

        /// <summary>
        /// Sends one administration command and reads the device's verdict on it, throwing
        /// <see cref="DeviceCommandFailedException"/> unless the device confirms it accepted the
        /// command.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Two steps, and the first is what makes the second mean anything. The device's SCPI error
        /// queue is FIFO and can already hold entries left by earlier commands or by the connect
        /// sequence, so a single <c>SYSTem:ERRor?</c> after the command could just as easily pop
        /// somebody else's failure — the same trap documented on the SD listing's terminator, which
        /// is why that one is read as a liveness marker and never classified. Draining the queue
        /// first removes the ambiguity: the entry popped afterwards is this command's own, or the
        /// queue is clean and the command was accepted.
        /// </para>
        /// <para>
        /// The command and its <c>SYSTem:ERRor?</c> query go out in one text exchange, so no other
        /// exchange can interleave between them. The drain necessarily runs in exchanges of its own
        /// beforehand, so a caller driving the same device from another thread can still slip a
        /// command into that gap; these are administration operations that already assume one caller
        /// at a time.
        /// </para>
        /// <para>
        /// Side effect worth knowing about: the drain discards whatever the queue held. A caller who
        /// wants those entries should read them with
        /// <see cref="DaqifiDevice.DrainErrorQueueAsync"/> before calling this.
        /// </para>
        /// </remarks>
        private async Task SendConfirmedAsync(IOutboundMessage<string> command, CancellationToken cancellationToken)
        {
            _host.EnsureConnected(cancellationToken);

            await _host.DrainErrorQueueAsync(ErrorQueueDrainCap, cancellationToken).ConfigureAwait(false);

            var lines = await _host.ExecuteTextCommandAsync(
                () =>
                {
                    _host.Send(command);
                    _host.Send(ScpiMessageProducer.GetSystemError);
                },
                responseTimeoutMs: ConfirmationResponseTimeoutMs,
                completionTimeoutMs: ConfirmationCompletionTimeoutMs,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            ThrowIfNotAccepted(command.Data, lines);
        }

        /// <summary>
        /// Reads the device's verdict out of a confirming exchange's response and throws unless it is
        /// an accepted command.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Three outcomes, and only one of them returns. A volunteered <c>**ERROR: ...</c> line is the
        /// device complaining about the command as it processed it; the <c>SYSTem:ERRor?</c> reply is
        /// the queue's verdict, authoritative because the drain left the queue empty; and no readable
        /// reply at all means the command's outcome is simply unknown, which is not a success and must
        /// not be reported as one.
        /// </para>
        /// <para>
        /// A code is only ever reported when one was actually parsed <i>and</i> is non-zero, which is
        /// what makes <see cref="DeviceCommandFailedException.ErrorCode"/> safe to branch on. Two cases
        /// keep it that way: <c>ERROR</c> lines carrying no readable code — a bare <c>ERROR</c>, or
        /// <c>ERROR: something non-numeric</c>, both of which
        /// <see cref="ScpiResponseClassifier.IsScpiErrorLine"/> matches — are a refusal whose code is
        /// unknown, so they take the null-code path rather than being reported under the one value SCPI
        /// reserves for "no error"; and an <c>ERROR</c>-shaped line that does say <c>0</c> is not
        /// treated as a refusal at all.
        /// </para>
        /// </remarks>
        private static void ThrowIfNotAccepted(string command, IReadOnlyList<string> lines)
        {
            // An error-shaped line that reports code 0 is not a refusal, but it is still something the
            // device said. Held onto so that if no bare verdict turns up either, the failure can carry
            // it rather than claim the device was silent.
            string? codeZeroLine = null;

            var volunteeredError = ScpiResponseClassifier.GetLastScpiErrorLine(lines);
            if (volunteeredError != null)
            {
                if (!ScpiResponseClassifier.TryExtractErrorCode(volunteeredError, out var volunteeredCode))
                {
                    // A refusal whose code cannot be read is still a refusal.
                    throw new DeviceCommandFailedException(command, volunteeredError);
                }

                if (volunteeredCode != 0)
                {
                    throw new DeviceCommandFailedException(command, volunteeredCode, volunteeredError);
                }

                // Code 0 is "no error" — a line that says so is not evidence of a refusal, whatever
                // shape it arrived in. A device that answers the queue read as `**ERROR: 0,"No error"`
                // rather than the bare form would otherwise be reported as having rejected a command
                // it accepted. Fall through and let the queue verdict decide.
                codeZeroLine = volunteeredError;
            }

            var reply = lines.LastOrDefault(ScpiResponseClassifier.IsSystemErrorReplyLine)?.Trim();
            if (reply == null)
            {
                // Nothing in the bare verdict shape. Report what the device did say, if anything: the
                // code-0 constructor normalises the code away but keeps the line, which is the whole
                // point of carrying it this far.
                throw codeZeroLine != null
                    ? new DeviceCommandFailedException(command, 0, codeZeroLine)
                    : new DeviceCommandFailedException(command);
            }

            if (!ScpiResponseClassifier.TryParseSystemErrorReplyCode(reply, out var code))
            {
                throw new DeviceCommandFailedException(command, reply);
            }

            if (code != 0)
            {
                throw new DeviceCommandFailedException(command, code, reply);
            }
        }

        /// <summary>
        /// Cap on the pre-send drain. Far smaller than
        /// <see cref="DaqifiDevice.DrainErrorQueueAsync"/>'s own default because each iteration is a
        /// full text exchange and a healthy device converges on the first one; a queue deeper than
        /// this is a device with bigger problems than the command about to be sent, and the
        /// confirmation below will say so rather than wait it out.
        /// </summary>
        private const int ErrorQueueDrainCap = 16;

        /// <summary>
        /// Time allowed for the first line of a confirming exchange's response. Generous because the
        /// commands being confirmed include NVM writes (<c>SAVEcal</c>, <c>SAVEFcal</c>,
        /// <c>VOLTage:SAVE</c>), which the device does not answer until the write is done.
        /// </summary>
        private const int ConfirmationResponseTimeoutMs = 3000;

        /// <summary>
        /// Inactivity window that ends a confirming exchange, in milliseconds.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Deliberately longer than the 250ms default, and set for the same reason the SD listing
        /// raises its own (<c>SD_LIST_COMPLETION_TIMEOUT_MS</c>): both exchanges are only complete once
        /// a trailing <c>SYSTem:ERRor?</c> reply has been seen, and the exchange switches from
        /// <see cref="ConfirmationResponseTimeoutMs"/> to this window as soon as <i>any</i> line
        /// arrives. On a device that echoes commands, that first line is the echo, so this window — not
        /// the response timeout — is what has to cover the gap between the echo and the verdict. With
        /// the 250ms default, a merely-slow verdict would read as a missing one and fail a command the
        /// device had accepted.
        /// </para>
        /// <para>
        /// It does not have to cover an NVM write on its own: a device that says nothing until the
        /// write finishes is still in the first phase, which
        /// <see cref="ConfirmationResponseTimeoutMs"/> covers. Should a device ever both echo and then
        /// take longer than this to answer, the result is a spurious "not confirmed" — loud and
        /// retryable, which is the failure direction this whole surface is built to prefer over a
        /// silent false success.
        /// </para>
        /// <para>
        /// <b>Measured on the bench Nyquist (fw 3.7.2, USB CDC):</b> this firmware does <i>not</i> echo
        /// commands, and every line it has to say arrives within ~20 ms of the first — a refusal's two
        /// lines land essentially together. So on that path the window is pure trailing latency and
        /// 250 ms would have sufficed. It is kept at 1000 ms as headroom for the cases the bench could
        /// not cover: WiFi, whose gaps are the documented reason the SD listing raised its own window,
        /// and the NVM writers (<c>SAVEcal</c> and friends), which are destructive to a calibrated
        /// unit and so were never sent. Tightening this is a reasonable future change once one of
        /// those has evidence behind it — the cost it buys is quantified on
        /// <see cref="IConfirmingDeviceAdministration"/>.
        /// </para>
        /// </remarks>
        private const int ConfirmationCompletionTimeoutMs = 1000;

        #endregion

        private static void ValidateChannelNumber(int channelNumber)
        {
            if (channelNumber < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(channelNumber), channelNumber, "Channel number cannot be negative.");
            }
        }

        private static void ValidateBank(int bank)
        {
            if (bank is < 0 or > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(bank), bank, "Calibration bank must be 0 (factory) or 1 (user).");
            }
        }
    }
}
