using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device.Capabilities;
using Daqifi.Core.Device.Internal;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Daqifi.Core.Device;

/// <summary>
/// Stands in for a device that is <b>already sitting in its bootloader</b>, so a manual
/// bootloader-only firmware update can be driven through the same
/// <see cref="Firmware.IFirmwareUpdateService"/> entry points as a normal update.
/// </summary>
/// <remarks>
/// <para>
/// A device in bootloader mode has no SCPI transport: it does not answer commands, stream data, or
/// emit status messages. Every operation on this type is therefore a no-op, and every collection is
/// empty. It exists purely so a recovery/update dialog has something satisfying
/// <see cref="IStreamingDevice"/> to pass to
/// <see cref="Firmware.IFirmwareUpdateService.UpdateFirmwareAsync"/>.
/// </para>
/// <para>
/// Three of those behaviours are load-bearing rather than incidental, because the PIC32 update flow
/// touches the device before it ever reaches the bootloader:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="IsConnected"/> starts <c>true</c>. The flow opens with
/// <c>EnsureDeviceConnected</c>, which throws <see cref="InvalidOperationException"/> on a
/// disconnected device — a stand-in reporting <c>false</c> would fail the update before it began.
/// </description></item>
/// <item><description>
/// <see cref="IsStreaming"/> is always <c>false</c>, so the flow skips its
/// <see cref="StopStreaming"/> call.
/// </description></item>
/// <item><description>
/// <see cref="Send{T}"/> silently discards. The flow sends the force-bootloader command; a device
/// already in the bootloader cannot receive it, and throwing here would abort a valid update.
/// </description></item>
/// </list>
/// <para>
/// Safe to use from multiple threads. Every operation but connect/disconnect is stateless, and
/// those two raise <see cref="StateChanged"/> exactly once per real transition, in order — a dialog tearing the session down can race the update flow's own
/// <see cref="Disconnect"/> without producing a duplicate or a missed notification.
/// </para>
/// <para>
/// This type lives in Core rather than in each consuming application on purpose. It is the only
/// hand-written implementation of <see cref="IStreamingDevice"/> that ships with the product, so
/// keeping it here means a member added to that interface is resolved once, in the change that adds
/// it, and is caught by this repository's build rather than surfacing later as a break in a
/// downstream app (daqifi-core#477).
/// </para>
/// </remarks>
public sealed class BootloaderSessionDevice : IStreamingDevice
{
    /// <summary>
    /// Name used when the caller supplies none.
    /// </summary>
    public const string DefaultName = "DAQiFi Bootloader";

    // Connected or Disconnected, and nothing else: there is no transport to dial and no device to
    // initialize.
    private volatile DeviceState _state = DeviceState.Connected;

    // Makes a transition and its place in the queue one step: StateChanged must be raised exactly
    // once per real transition, and in order, even if a dialog's teardown races the update flow's
    // own Disconnect().
    private readonly Lock _transitionLock = new();

    private readonly StateChangeQueue _stateChanges;

    /// <summary>
    /// Initializes a new instance of the <see cref="BootloaderSessionDevice"/> class, already in the
    /// connected state.
    /// </summary>
    /// <param name="name">
    /// A display name for the session. Falls back to <see cref="DefaultName"/> when null, empty, or
    /// whitespace.
    /// </param>
    public BootloaderSessionDevice(string? name = null)
    {
        Name = string.IsNullOrWhiteSpace(name) ? DefaultName : name;

        // A subscriber's failure is isolated, as on a real device: the update flow calls
        // Disconnect() itself, and that failure must not become the update's. There is nothing to
        // report it on — ErrorOccurred is never raised by this type.
        _stateChanges = new StateChangeQueue(this, () => StateChanged, static _ => { });
    }

    #region IDevice

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public bool IsConnected => _state.IsConnected();

    /// <inheritdoc />
    public DeviceState State => _state;

    /// <summary>
    /// Always <c>false</c>: no operation reaches a device in bootloader mode.
    /// </summary>
    /// <param name="feature">Ignored.</param>
    /// <returns><c>false</c>.</returns>
    public bool Supports(DeviceFeature feature) => false;

    /// <summary>
    /// Raised by <see cref="Connect"/> and <see cref="Disconnect"/>. This is the one event this type
    /// actually raises — the device itself produces nothing to report. A subscriber that throws fails
    /// neither the <see cref="Connect"/> or <see cref="Disconnect"/> it was notified of nor the
    /// other subscribers.
    /// </summary>
    public event EventHandler<DeviceStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Never raised: a device in bootloader mode emits no protocol messages. Declared with no-op
    /// accessors so that contract is explicit in the code rather than an unused-field warning.
    /// </summary>
    public event EventHandler<MessageReceivedEventArgs>? MessageReceived
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Never raised: this type owns no transport or background threads, so it has no failures of its
    /// own to report. Update failures surface through
    /// <see cref="Firmware.FirmwareUpdateProgress"/> and
    /// <see cref="Firmware.FirmwareUpdateException"/> instead.
    /// </summary>
    public event EventHandler<DeviceErrorEventArgs>? ErrorOccurred
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Marks the session connected, raising <see cref="StateChanged"/> if it was not already.
    /// Opens no transport — there is nothing to open.
    /// </summary>
    public void Connect() => SetState(DeviceState.Connected);

    /// <summary>
    /// Marks the session disconnected, raising <see cref="StateChanged"/> if it was not already.
    /// Closes no transport — there is nothing to close.
    /// </summary>
    public void Disconnect() => SetState(DeviceState.Disconnected);

    /// <inheritdoc cref="Connect" />
    /// <param name="cancellationToken">Observed before the state change is applied.</param>
    /// <returns>A completed task, or a cancelled one if <paramref name="cancellationToken"/> was already signalled.</returns>
    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        Connect();
        return Task.CompletedTask;
    }

    /// <inheritdoc cref="Disconnect" />
    /// <param name="cancellationToken">Observed before the state change is applied.</param>
    /// <returns>A completed task, or a cancelled one if <paramref name="cancellationToken"/> was already signalled.</returns>
    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        Disconnect();
        return Task.CompletedTask;
    }

    /// <summary>No-op: a device in bootloader mode cannot be initialized. <see cref="State"/> does not change.</summary>
    /// <param name="channelPopulationTimeout">Ignored.</param>
    /// <param name="cancellationToken">Observed before completing.</param>
    /// <returns>A completed task, or a cancelled one if <paramref name="cancellationToken"/> was already signalled.</returns>
    public Task InitializeAsync(TimeSpan? channelPopulationTimeout = null, CancellationToken cancellationToken = default)
        => Completed(cancellationToken);

    /// <summary>
    /// Silently discards <paramref name="message"/>. See the remarks on the type: the PIC32 update
    /// flow sends the force-bootloader command unconditionally, and a device already in the
    /// bootloader has no command channel to receive it.
    /// </summary>
    /// <typeparam name="T">The message payload type.</typeparam>
    /// <param name="message">The message to discard.</param>
    public void Send<T>(IOutboundMessage<T> message)
    {
    }

    /// <summary>
    /// Marks the session disconnected. Idempotent, and releases nothing — this type holds no
    /// unmanaged resources.
    /// </summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync()
    {
        Disconnect();
        return default;
    }

    #endregion

    #region Channels and metadata

    /// <summary>
    /// An empty metadata record. A bootloader session reports no part number, firmware version, or
    /// capabilities; the instance is non-null so callers can read it without a guard.
    /// </summary>
    public DeviceMetadata Metadata { get; } = new();

    /// <summary>No-op: a bootloader session reports no status, so <see cref="Metadata"/> does not change.</summary>
    /// <param name="timeout">Ignored.</param>
    /// <param name="cancellationToken">Observed before completing.</param>
    /// <returns>A completed task, or a cancelled one if <paramref name="cancellationToken"/> was already signalled.</returns>
    public Task RefreshDeviceStatusAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        => Completed(cancellationToken);

    /// <summary>Always <c>null</c>: a bootloader session has no capability document.</summary>
    /// <param name="cancellationToken">Observed before completing.</param>
    /// <returns><c>null</c>, or a cancelled task if <paramref name="cancellationToken"/> was already signalled.</returns>
    public Task<CapabilityDocument?> ReadCapabilityDocumentAsync(CancellationToken cancellationToken = default)
        => Completed<CapabilityDocument?>(null, cancellationToken);

    /// <summary>
    /// Always empty: channels are populated from device status messages, which a bootloader session
    /// never emits.
    /// </summary>
    public IReadOnlyList<IChannel> Channels => Array.Empty<IChannel>();

    /// <inheritdoc cref="Channels" />
    /// <returns>An empty list.</returns>
    public IReadOnlyList<IChannel> GetChannelsSnapshot() => Array.Empty<IChannel>();

    /// <summary>
    /// Never raised: see <see cref="Channels"/>.
    /// </summary>
    public event EventHandler<ChannelsPopulatedEventArgs>? ChannelsPopulated
    {
        add { }
        remove { }
    }

    #endregion

    #region Streaming

    /// <summary>
    /// Stored but never acted on, so a caller that round-trips the value sees what it set.
    /// </summary>
    public int StreamingFrequency { get; set; } = 1;

    /// <summary>
    /// Always <c>false</c>. See the remarks on the type: the update flow calls
    /// <see cref="StopStreaming"/> only when this is <c>true</c>.
    /// </summary>
    public bool IsStreaming => false;

    /// <summary>No-op: a bootloader session cannot stream.</summary>
    public void StartStreaming()
    {
    }

    /// <summary>No-op: a bootloader session cannot stream.</summary>
    /// <param name="cancellationToken">Observed before completing.</param>
    /// <returns>A completed task, or a cancelled one if <paramref name="cancellationToken"/> was already signalled.</returns>
    public Task StartStreamingAsync(CancellationToken cancellationToken = default)
        => Completed(cancellationToken);

    /// <summary>No-op: a bootloader session cannot stream.</summary>
    public void StopStreaming()
    {
    }

    /// <summary>No-op: a bootloader session cannot stream.</summary>
    /// <param name="cancellationToken">Observed before completing.</param>
    /// <returns>A completed task, or a cancelled one if <paramref name="cancellationToken"/> was already signalled.</returns>
    public Task StopStreamingAsync(CancellationToken cancellationToken = default)
        => Completed(cancellationToken);

    #endregion

    #region Channel, DIO, PWM, and analog output control

    // All no-ops: these drive hardware through the SCPI command channel, which a device in
    // bootloader mode does not expose. They accept any argument rather than validating it —
    // a stand-in that threw would turn a harmless call into an update failure.

    /// <summary>No-op: see the remarks on this type.</summary>
    /// <param name="channel">Ignored.</param>
    public void EnableChannel(IChannel channel)
    {
    }

    /// <summary>No-op: see the remarks on this type.</summary>
    /// <param name="channels">Ignored.</param>
    public void EnableChannels(IEnumerable<IChannel> channels)
    {
    }

    /// <summary>No-op: see the remarks on this type.</summary>
    /// <param name="channel">Ignored.</param>
    public void DisableChannel(IChannel channel)
    {
    }

    /// <summary>No-op: see the remarks on this type.</summary>
    public void DisableAllChannels()
    {
    }

    /// <summary>No-op: see the remarks on this type.</summary>
    /// <param name="channel">Ignored.</param>
    /// <param name="direction">Ignored.</param>
    public void SetDioDirection(IChannel channel, ChannelDirection direction)
    {
    }

    /// <summary>No-op: see the remarks on this type.</summary>
    /// <param name="channel">Ignored.</param>
    /// <param name="value">Ignored.</param>
    public void SetDioValue(IChannel channel, bool value)
    {
    }

    /// <summary>No-op: see the remarks on this type.</summary>
    /// <param name="channel">Ignored.</param>
    /// <param name="enabled">Ignored.</param>
    public void SetPwmEnabled(IChannel channel, bool enabled)
    {
    }

    /// <summary>No-op: see the remarks on this type.</summary>
    /// <param name="channel">Ignored.</param>
    /// <param name="dutyCyclePercent">Ignored.</param>
    public void SetPwmDutyCycle(IChannel channel, int dutyCyclePercent)
    {
    }

    /// <summary>No-op: see the remarks on this type.</summary>
    /// <param name="frequencyHz">Ignored.</param>
    public void SetPwmFrequency(int frequencyHz)
    {
    }

    /// <summary>
    /// Always <see cref="DaqifiStreamingDevice.DefaultPwmFrequencyHz"/>. Nothing is ever commanded
    /// through <see cref="SetPwmFrequency"/> here, so this reports the same default a real device
    /// reports before its first PWM command rather than a 0 that is not a commandable frequency.
    /// </summary>
    public int PwmFrequencyHz => DaqifiStreamingDevice.DefaultPwmFrequencyHz;

    /// <summary>Always <see cref="DaqifiStreamingDevice.MinPwmFrequencyHz"/>.</summary>
    public int PwmFrequencyFloorHz => DaqifiStreamingDevice.MinPwmFrequencyHz;

    /// <summary>No-op: see the remarks on this type.</summary>
    /// <param name="channelNumber">Ignored.</param>
    /// <param name="voltage">Ignored.</param>
    public void SetAnalogOutput(int channelNumber, double voltage)
    {
    }

    /// <summary>No-op: see the remarks on this type.</summary>
    /// <param name="channelNumber">Ignored.</param>
    /// <param name="voltage">Ignored.</param>
    public void StageAnalogOutput(int channelNumber, double voltage)
    {
    }

    /// <summary>No-op: see the remarks on this type.</summary>
    public void LatchAnalogOutputs()
    {
    }

    /// <summary>Always <see cref="double.NaN"/>: there is no output to report a voltage for.</summary>
    /// <param name="channelNumber">Ignored.</param>
    /// <param name="cancellationToken">Observed before completing.</param>
    /// <returns><see cref="double.NaN"/>, or a cancelled task if <paramref name="cancellationToken"/> was already signalled.</returns>
    public Task<double> GetAnalogOutputAsync(int channelNumber, CancellationToken cancellationToken = default)
        => Completed(double.NaN, cancellationToken);

    /// <summary>
    /// No-op: the device is already in its bootloader, which is where a reboot would be trying to
    /// leave it from.
    /// </summary>
    public void Reboot()
    {
    }

    /// <summary>No-op: see <see cref="Reboot"/>.</summary>
    /// <param name="cancellationToken">Observed before completing.</param>
    /// <returns>A completed task, or a cancelled one if <paramref name="cancellationToken"/> was already signalled.</returns>
    public Task RebootAsync(CancellationToken cancellationToken = default)
        => Completed(cancellationToken);

    #endregion

    #region ADC calibration and voltage precision

    // No-ops for the same reason as the control members above. The confirming members, here and
    // among the streaming ones, complete successfully rather than throwing
    // DeviceCommandFailedException: there is no device to refuse the command, so there is no
    // failure to report.

    /// <summary>No-op: see the remarks on this type.</summary>
    /// <param name="cancellationToken">Observed before completing.</param>
    /// <returns>A completed task, or a cancelled one if <paramref name="cancellationToken"/> was already signalled.</returns>
    public Task SaveAdcCalibrationAsync(CancellationToken cancellationToken = default)
        => Completed(cancellationToken);

    /// <summary>No-op: see the remarks on this type.</summary>
    /// <param name="cancellationToken">Observed before completing.</param>
    /// <returns>A completed task, or a cancelled one if <paramref name="cancellationToken"/> was already signalled.</returns>
    public Task LoadAdcCalibrationAsync(CancellationToken cancellationToken = default)
        => Completed(cancellationToken);

    /// <summary>No-op: see the remarks on this type.</summary>
    /// <param name="channelNumber">Ignored.</param>
    /// <param name="calM">Ignored.</param>
    /// <param name="cancellationToken">Observed before completing.</param>
    /// <returns>A completed task, or a cancelled one if <paramref name="cancellationToken"/> was already signalled.</returns>
    public Task SetAdcCalibrationSlopeAsync(int channelNumber, double calM, CancellationToken cancellationToken = default)
        => Completed(cancellationToken);

    /// <summary>No-op: see the remarks on this type.</summary>
    /// <param name="channelNumber">Ignored.</param>
    /// <param name="calB">Ignored.</param>
    /// <param name="cancellationToken">Observed before completing.</param>
    /// <returns>A completed task, or a cancelled one if <paramref name="cancellationToken"/> was already signalled.</returns>
    public Task SetAdcCalibrationOffsetAsync(int channelNumber, double calB, CancellationToken cancellationToken = default)
        => Completed(cancellationToken);

    /// <summary>No-op: see the remarks on this type.</summary>
    /// <param name="cancellationToken">Observed before completing.</param>
    /// <returns>A completed task, or a cancelled one if <paramref name="cancellationToken"/> was already signalled.</returns>
    public Task SaveFactoryAdcCalibrationAsync(CancellationToken cancellationToken = default)
        => Completed(cancellationToken);

    /// <summary>No-op: see the remarks on this type.</summary>
    /// <param name="cancellationToken">Observed before completing.</param>
    /// <returns>A completed task, or a cancelled one if <paramref name="cancellationToken"/> was already signalled.</returns>
    public Task LoadFactoryAdcCalibrationAsync(CancellationToken cancellationToken = default)
        => Completed(cancellationToken);

    /// <summary>No-op: see the remarks on this type.</summary>
    /// <param name="bank">Ignored.</param>
    /// <param name="cancellationToken">Observed before completing.</param>
    /// <returns>A completed task, or a cancelled one if <paramref name="cancellationToken"/> was already signalled.</returns>
    public Task UseAdcCalibrationAsync(AdcCalibrationBank bank, CancellationToken cancellationToken = default)
        => Completed(cancellationToken);

    /// <summary>No-op: see the remarks on this type.</summary>
    /// <param name="cancellationToken">Observed before completing.</param>
    /// <returns>A completed task, or a cancelled one if <paramref name="cancellationToken"/> was already signalled.</returns>
    public Task SaveVoltagePrecisionAsync(CancellationToken cancellationToken = default)
        => Completed(cancellationToken);

    /// <summary>No-op: see the remarks on this type.</summary>
    /// <param name="cancellationToken">Observed before completing.</param>
    /// <returns>A completed task, or a cancelled one if <paramref name="cancellationToken"/> was already signalled.</returns>
    public Task LoadVoltagePrecisionAsync(CancellationToken cancellationToken = default)
        => Completed(cancellationToken);

    #endregion

    private static Task Completed(CancellationToken cancellationToken)
        => cancellationToken.IsCancellationRequested
            ? Task.FromCanceled(cancellationToken)
            : Task.CompletedTask;

    private static Task<T> Completed<T>(T result, CancellationToken cancellationToken)
        => cancellationToken.IsCancellationRequested
            ? Task.FromCanceled<T>(cancellationToken)
            : Task.FromResult(result);

    private void SetState(DeviceState state)
    {
        StateChangeQueue.Change change;
        lock (_transitionLock)
        {
            if (_state == state)
            {
                // Already there — not a transition, so nothing to announce.
                return;
            }

            change = _stateChanges.Enqueue(_state, state);
            _state = state;
        }

        _stateChanges.Publish(change);
    }
}
