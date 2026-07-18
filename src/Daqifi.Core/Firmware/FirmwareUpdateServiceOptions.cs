using Daqifi.Core.Device;

namespace Daqifi.Core.Firmware;

/// <summary>
/// Configuration options for <see cref="FirmwareUpdateService"/>.
/// </summary>
public sealed class FirmwareUpdateServiceOptions
{
    /// <summary>
    /// DAQiFi PIC32 bootloader USB vendor identifier.
    /// </summary>
    public int BootloaderVendorId { get; set; } = 0x04D8;

    /// <summary>
    /// DAQiFi PIC32 bootloader USB product identifier.
    /// </summary>
    public int BootloaderProductId { get; set; } = 0x003C;

    /// <summary>
    /// Poll interval used while waiting for bootloader and serial re-enumeration.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Timeout for the <see cref="FirmwareUpdateState.PreparingDevice"/> state.
    /// </summary>
    public TimeSpan PreparingDeviceTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Timeout for the <see cref="FirmwareUpdateState.WaitingForBootloader"/> state.
    /// </summary>
    public TimeSpan WaitingForBootloaderTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Timeout for the <see cref="FirmwareUpdateState.Connecting"/> state.
    /// </summary>
    public TimeSpan ConnectingTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Timeout for the <see cref="FirmwareUpdateState.ErasingFlash"/> state.
    /// </summary>
    public TimeSpan ErasingFlashTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Timeout for the <see cref="FirmwareUpdateState.Programming"/> state.
    /// </summary>
    public TimeSpan ProgrammingTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Timeout for the <see cref="FirmwareUpdateState.Verifying"/> state.
    /// </summary>
    public TimeSpan VerifyingTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Timeout for the <see cref="FirmwareUpdateState.JumpingToApp"/> state.
    /// </summary>
    public TimeSpan JumpingToApplicationTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Timeout used for individual bootloader read operations.
    /// </summary>
    public TimeSpan BootloaderResponseTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Delay after sending SCPI FORCEBOOT before disconnecting serial.
    /// </summary>
    public TimeSpan PostForceBootDelay { get; set; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Delay after switching LAN firmware update mode before disconnecting serial.
    /// </summary>
    public TimeSpan PostLanFirmwareModeDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Delay after the managed serial connection is disconnected (entering WiFi update mode)
    /// and before the external WINC flash tool is launched. The OS does not free the USB-CDC
    /// COM handle the instant the device's serial connection is disconnected; without this pause
    /// the flash tool tries to open the port before it is released, fails to open it,
    /// and exits within ~1s producing no programming output — which the output-based success
    /// verification (see <see cref="WifiFlashSuccessMarker"/>) then correctly reports as a
    /// failure. Set to <see cref="TimeSpan.Zero"/> to skip the wait (e.g. unit tests, or
    /// transports that release the port synchronously).
    /// </summary>
    public TimeSpan PostLanDisconnectPortReleaseDelay { get; set; } = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// Delay before attempting to reconnect serial after WiFi tool execution.
    /// </summary>
    public TimeSpan PostWifiReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Delay observed at the WINC "Power cycle WINC and set to bootloader mode" prompt before the
    /// empty continue line is sent to the flash tool. Gives the WiFi firmware time to finish its
    /// bridge-mode initialization — especially after <see cref="WifiBridgeActivationCallback"/>
    /// fires — before the tool issues its first serial bridge query. Set to
    /// <see cref="TimeSpan.Zero"/> to respond immediately.
    /// </summary>
    public TimeSpan WincBootPromptResponseDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Optional callback invoked synchronously when the WINC "Power cycle WINC" prompt is detected,
    /// before the <see cref="WincBootPromptResponseDelay"/> wait. Intended for the host to open a
    /// raw serial port and send the bridge-activation commands (LAN:FWUpdate / LAN:APPLY) that
    /// trigger the device's bridge-mode state machine immediately before the tool starts
    /// programming. Kept injectable so the raw-serial specifics stay in the host while Core owns
    /// the flash lifecycle, prompt detection, and response timing. Exceptions thrown by the
    /// callback are logged and swallowed — they must not abort the flash.
    /// </summary>
    public Action? WifiBridgeActivationCallback { get; set; }

    /// <summary>
    /// Total attempts (initial + retries) for the external WINC flash process when an attempt fails
    /// with a transient bridge-init failure (e.g. "failed to read serial bridge ID query response"
    /// or "failed to initialise programming firmware"). A second attempt typically succeeds because
    /// the device has since settled into bridge mode. Must be at least 1.
    /// </summary>
    public int WifiFlashAttempts { get; set; } = 2;

    /// <summary>
    /// Delay between WINC flash retry attempts. Only consulted when <see cref="WifiFlashAttempts"/>
    /// is greater than 1.
    /// </summary>
    public TimeSpan WifiFlashRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Output line (case-insensitive substring) that the WINC flash tool emits on a fully
    /// successful program. Success is verified from the tool's own output rather than from its exit
    /// code or run duration: a genuine flash ends with "verify passed" then this marker, whereas a
    /// tool that cannot reach the WINC (e.g. because the device never released the serial port)
    /// produces none of these. Defaults to the shipped WINC Programming Tool 2.0.1 terminal line.
    /// </summary>
    public string WifiFlashSuccessMarker { get; set; } = "Operation completed successfully";

    /// <summary>
    /// Maximum HID connection attempts during bootloader connect, including the initial attempt.
    /// </summary>
    public int HidConnectRetryCount { get; set; } = 3;

    /// <summary>
    /// Delay between HID connection attempts.
    /// </summary>
    public TimeSpan HidConnectRetryDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Maximum programming attempts per flash record, including the initial attempt.
    /// </summary>
    public int FlashWriteRetryCount { get; set; } = 3;

    /// <summary>
    /// Delay between flash record retry attempts.
    /// </summary>
    public TimeSpan FlashWriteRetryDelay { get; set; } = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Timeout for the external WiFi flashing process.
    /// </summary>
    public TimeSpan WifiProcessTimeout { get; set; } = TimeSpan.FromMinutes(8);

    /// <summary>
    /// File name used when <c>firmwarePath</c> points to a directory containing WiFi tools.
    /// </summary>
    public string WifiFlashToolFileName { get; set; } = "winc_flash_tool.cmd";

    /// <summary>
    /// Arguments template for WiFi flash tool execution.
    /// Supports <c>{port}</c> and optional <c>{firmwarePath}</c> placeholders.
    /// The default WINC script-based flow discovers firmware artifacts from its working directory
    /// and therefore does not require <c>{firmwarePath}</c>.
    /// </summary>
    public string WifiFlashToolArgumentsTemplate { get; set; } = "/p {port} /d WINC1500 /k /e /i aio /w";

    /// <summary>
    /// Optional explicit serial port override for WiFi updates.
    /// Defaults to <see cref="Device.IDevice.Name"/> when null/empty.
    /// </summary>
    public string? WifiPortOverride { get; set; }

    /// <summary>
    /// Optional callback that returns true once the device is ready to
    /// answer normal application commands after PIC32 firmware update +
    /// reconnect (closes #145). The serial transport can re-enumerate
    /// well before the application firmware is actually ready to respond
    /// to protobuf status queries — without this probe, the next steps
    /// in a downstream flow (LAN chip-info, WiFi prep) hit a half-started
    /// device and have to retry. When set, the firmware service polls
    /// the probe with bounded retry after each PIC32 reconnect; if it
    /// never returns true within <see cref="PostReconnectReadinessTimeout"/>,
    /// the update transitions to Failed with a clear timeout exception
    /// rather than silently handing back a half-ready device. When null,
    /// reconnect succeeds as soon as the serial port reopens (legacy
    /// behavior).
    /// </summary>
    public Func<IStreamingDevice, CancellationToken, Task<bool>>? PostReconnectReadinessProbe { get; set; }

    /// <summary>
    /// Wall-clock budget for the post-reconnect readiness probe. Default
    /// 30s covers a slow PIC32 boot and downstream-firmware initialization.
    /// </summary>
    /// <remarks>
    /// This budget runs INSIDE the JumpingToApp state, so the effective
    /// upper bound is <c>JumpingToApplicationTimeout - (serial reconnect
    /// elapsed)</c>. With the defaults (45s state timeout, ~1-5s typical
    /// reconnect, 30s readiness budget) the readiness probe gets its full
    /// budget; if you raise this near or above the state timeout, the
    /// outer state-timeout will fire first and callers will see a generic
    /// JumpingToApp timeout instead of the readiness-specific message.
    /// </remarks>
    public TimeSpan PostReconnectReadinessTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Delay between readiness-probe attempts (cancellation-aware).
    /// </summary>
    public TimeSpan PostReconnectReadinessRetryDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Settling delay between discarding the race-winning serial handle
    /// and re-opening the port after a PIC32 reset (closes the macOS-USB-CDC
    /// shadow-handle problem). After PIC32 firmware update completes the
    /// device's USB CDC interface re-enumerates; on macOS the first
    /// SerialPort.Open() that succeeds during that window is typically a
    /// "shadow" handle — IsOpen==true but writes silently drop and reads
    /// see zero bytes. Closing and re-opening after a brief delay yields
    /// a clean kernel binding. Default 2s covers macOS-observed timing;
    /// set to TimeSpan.Zero on platforms (notably Windows) where the
    /// first open is already clean and the extra cycle is pure latency.
    /// </summary>
    public TimeSpan PostReconnectStaleHandleDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Total attempts (initial + retries) for LAN chip-info queries before
    /// the WiFi version decision falls through to "couldn't check, proceed
    /// with flash". Right after a PIC32 firmware update the application is
    /// typically up while the WiFi subsystem is still finishing startup, so
    /// the first chip-info query can transiently fail; bounded retry covers
    /// that window so callers don't unnecessarily reflash up-to-date WiFi
    /// firmware (closes #144).
    /// </summary>
    /// <remarks>
    /// Each attempt also incurs the per-attempt timeout from the device
    /// implementation (e.g., <c>DaqifiStreamingDevice.GetLanChipInfoAsync</c>
    /// uses 2s). Total wall-clock budget is therefore
    /// <c>sum(attempt durations) + (MaxAttempts-1) * RetryDelay</c>; with the
    /// 2s device default, 3 attempts and 2s delay sum to ~10s in the worst
    /// case. The retry loop holds <c>_operationLock</c>, so use
    /// <see cref="LanChipInfoTotalTimeout"/> to cap the actual wall-clock
    /// time independent of attempt counts.
    /// </remarks>
    public int LanChipInfoMaxAttempts { get; set; } = 3;

    /// <summary>
    /// Delay between LAN chip-info retry attempts (cancellation-aware).
    /// </summary>
    public TimeSpan LanChipInfoRetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Hard upper bound on wall-clock time spent in the LAN chip-info
    /// probe (including per-attempt query timeouts and retry delays).
    /// When exceeded, the loop short-circuits to ChipInfoUnavailable
    /// regardless of remaining attempts. Prevents pathological multi-
    /// attempt cases (e.g., 3 attempts × 2s device timeout + 2 × 2s delays
    /// = ~10s) from stalling firmware flows or UI status probes that hold
    /// the operation lock.
    /// </summary>
    public TimeSpan LanChipInfoTotalTimeout { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// When true, <see cref="FirmwareUpdateService.CheckWifiFirmwareStatusAsync"/> sends
    /// <see cref="Communication.Producers.ScpiMessageProducer.TurnDeviceOn"/> and waits
    /// <see cref="PowerOnWifiModuleSettleDelay"/> before the first chip-info probe, when the
    /// device is connected. Right after a PIC32 reflash the WINC module comes back powered
    /// off, so every <c>GETChipInfo?</c> query fails, the probe reports
    /// <see cref="WifiFirmwareStatusReason.ChipInfoUnavailable"/>, and callers conservatively
    /// default to "needs flash" — producing a needless multi-minute WiFi reflash. Powering the
    /// module on first closes that gap. Defaults to <c>true</c>; set to <c>false</c> to restore
    /// the legacy behavior of probing without a preceding power-on.
    /// </summary>
    public bool PowerOnWifiModuleBeforeProbe { get; set; } = true;

    /// <summary>
    /// Delay after sending the WINC power-on command, before the first chip-info probe.
    /// Only consulted when <see cref="PowerOnWifiModuleBeforeProbe"/> is <c>true</c>. Gives the
    /// module time to power up and start responding to <c>GETChipInfo?</c> queries.
    /// </summary>
    public TimeSpan PowerOnWifiModuleSettleDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// When true, the LAN chip-info retry loop sends
    /// <see cref="Communication.Producers.ScpiMessageProducer.ApplyNetworkLan"/> once, the
    /// first time a <c>GETChipInfo?</c> probe reports SCPI <c>-200</c> (the WINC1500 state
    /// machine is not yet initialized even though <c>LAN:ENAbled? = 1</c>), then continues
    /// retrying within the existing budget. Without this, that steady-state condition (as
    /// opposed to the post-reboot transient #144 already retries for) exhausts the retry
    /// budget and reports <see cref="WifiFirmwareStatusReason.LanNotInitialized"/>, sending
    /// callers into a needless multi-minute reflash of already-current WiFi firmware
    /// (closes #203). Sent at most once per probe to avoid repeatedly tearing down and
    /// re-initializing an already-associated WiFi link. Defaults to <c>true</c>; set to
    /// <c>false</c> to restore the legacy behavior of retrying without ever sending APPLY.
    /// </summary>
    public bool KickLanApplyOnNotInitialized { get; set; } = true;

    /// <summary>
    /// Gets the configured timeout for a given firmware update state.
    /// </summary>
    /// <param name="state">The target state.</param>
    /// <returns>The configured timeout.</returns>
    public TimeSpan GetStateTimeout(FirmwareUpdateState state)
    {
        return state switch
        {
            FirmwareUpdateState.PreparingDevice => PreparingDeviceTimeout,
            FirmwareUpdateState.WaitingForBootloader => WaitingForBootloaderTimeout,
            FirmwareUpdateState.Connecting => ConnectingTimeout,
            FirmwareUpdateState.ErasingFlash => ErasingFlashTimeout,
            FirmwareUpdateState.Programming => ProgrammingTimeout,
            FirmwareUpdateState.Verifying => VerifyingTimeout,
            FirmwareUpdateState.JumpingToApp => JumpingToApplicationTimeout,
            // Cleanup re-erases the application flash, so it is bounded by the
            // same budget as a normal erase.
            FirmwareUpdateState.CleaningUp => ErasingFlashTimeout,
            _ => TimeSpan.FromMinutes(5)
        };
    }

    /// <summary>
    /// Validates option values and throws when a value is invalid.
    /// </summary>
    public void Validate()
    {
        ValidatePositive(PollInterval, nameof(PollInterval));
        ValidatePositive(PreparingDeviceTimeout, nameof(PreparingDeviceTimeout));
        ValidatePositive(WaitingForBootloaderTimeout, nameof(WaitingForBootloaderTimeout));
        ValidatePositive(ConnectingTimeout, nameof(ConnectingTimeout));
        ValidatePositive(ErasingFlashTimeout, nameof(ErasingFlashTimeout));
        ValidatePositive(ProgrammingTimeout, nameof(ProgrammingTimeout));
        ValidatePositive(VerifyingTimeout, nameof(VerifyingTimeout));
        ValidatePositive(JumpingToApplicationTimeout, nameof(JumpingToApplicationTimeout));
        ValidatePositive(BootloaderResponseTimeout, nameof(BootloaderResponseTimeout));
        ValidatePositive(PostForceBootDelay, nameof(PostForceBootDelay));
        ValidatePositive(PostLanFirmwareModeDelay, nameof(PostLanFirmwareModeDelay));
        ValidatePositive(PostWifiReconnectDelay, nameof(PostWifiReconnectDelay));
        ValidatePositive(HidConnectRetryDelay, nameof(HidConnectRetryDelay));
        ValidatePositive(FlashWriteRetryDelay, nameof(FlashWriteRetryDelay));
        ValidatePositive(WifiProcessTimeout, nameof(WifiProcessTimeout));
        ValidatePositive(WifiFlashRetryDelay, nameof(WifiFlashRetryDelay));

        // These two permit Zero (= "skip the wait"); only reject negatives.
        ValidateNonNegative(PostLanDisconnectPortReleaseDelay, nameof(PostLanDisconnectPortReleaseDelay));
        ValidateNonNegative(WincBootPromptResponseDelay, nameof(WincBootPromptResponseDelay));

        if (WifiFlashAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(WifiFlashAttempts),
                WifiFlashAttempts,
                "WiFi flash attempts must be at least 1.");
        }

        if (string.IsNullOrWhiteSpace(WifiFlashSuccessMarker))
        {
            throw new ArgumentException("WiFi flash success marker cannot be empty.", nameof(WifiFlashSuccessMarker));
        }

        // The readiness options only matter when a probe is configured —
        // gate every readiness validation behind that, both the positive
        // checks and the cross-property constraint, so callers that don't
        // use the probe never have to think about these timeouts.
        if (PostReconnectReadinessProbe is not null)
        {
            ValidatePositive(PostReconnectReadinessTimeout, nameof(PostReconnectReadinessTimeout));
            ValidatePositive(PostReconnectReadinessRetryDelay, nameof(PostReconnectReadinessRetryDelay));

            // The whole JumpToApp step is wrapped by JumpingToApplicationTimeout,
            // so a probe budget that meets or exceeds it would always be cut off
            // by the outer state-timeout — surfacing a generic JumpingToApp error
            // instead of the readiness-specific message. Reject the configuration
            // up front so misconfigurations fail fast.
            if (PostReconnectReadinessTimeout >= JumpingToApplicationTimeout)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(PostReconnectReadinessTimeout),
                    PostReconnectReadinessTimeout,
                    $"{nameof(PostReconnectReadinessTimeout)} must be strictly less than {nameof(JumpingToApplicationTimeout)} when {nameof(PostReconnectReadinessProbe)} is set, " +
                    "otherwise the outer state-timeout fires first and masks the readiness-specific timeout.");
            }
        }

        if (HidConnectRetryCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(HidConnectRetryCount),
                HidConnectRetryCount,
                "HID connect retry count must be at least 1.");
        }

        if (FlashWriteRetryCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(FlashWriteRetryCount),
                FlashWriteRetryCount,
                "Flash write retry count must be at least 1.");
        }

        if (LanChipInfoMaxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(LanChipInfoMaxAttempts),
                LanChipInfoMaxAttempts,
                "LAN chip-info max attempts must be at least 1.");
        }

        ValidatePositive(LanChipInfoRetryDelay, nameof(LanChipInfoRetryDelay));
        ValidatePositive(LanChipInfoTotalTimeout, nameof(LanChipInfoTotalTimeout));

        // Only consulted when PowerOnWifiModuleBeforeProbe is true, but validated
        // unconditionally so a misconfiguration surfaces even if the flag is
        // toggled on later without re-touching this value.
        ValidateNonNegative(PowerOnWifiModuleSettleDelay, nameof(PowerOnWifiModuleSettleDelay));

        if (BootloaderVendorId < 0 || BootloaderVendorId > 0xFFFF)
        {
            throw new ArgumentOutOfRangeException(
                nameof(BootloaderVendorId),
                BootloaderVendorId,
                "Bootloader vendor ID must be in range 0..65535.");
        }

        if (BootloaderProductId < 0 || BootloaderProductId > 0xFFFF)
        {
            throw new ArgumentOutOfRangeException(
                nameof(BootloaderProductId),
                BootloaderProductId,
                "Bootloader product ID must be in range 0..65535.");
        }

        if (string.IsNullOrWhiteSpace(WifiFlashToolFileName))
        {
            throw new ArgumentException("WiFi flash tool file name cannot be empty.", nameof(WifiFlashToolFileName));
        }
    }

    private static void ValidatePositive(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Timeouts and delays must be greater than zero.");
        }
    }

    private static void ValidateNonNegative(TimeSpan value, string parameterName)
    {
        if (value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Delay must not be negative.");
        }
    }
}
