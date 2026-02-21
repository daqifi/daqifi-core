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
    /// Delay before attempting to reconnect serial after WiFi tool execution.
    /// </summary>
    public TimeSpan PostWifiReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

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
}
