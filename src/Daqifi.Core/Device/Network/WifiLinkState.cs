namespace Daqifi.Core.Device.Network;

/// <summary>
/// What a device's WiFi link is actually doing, as reported by
/// <see cref="IWifiLinkStatusProvider.GetWifiLinkStateAsync"/> (firmware v3.9.0+,
/// <c>SYSTem:COMMunicate:LAN:CONnected?</c>).
/// </summary>
/// <remarks>
/// This is the live state, not the configuration: after
/// <see cref="INetworkConfigurable.UpdateNetworkConfigurationAsync"/> applies new settings, it is
/// how a caller confirms the device actually joined (or started) the network instead of guessing
/// from elapsed time.
/// </remarks>
public enum WifiLinkState
{
    /// <summary>
    /// The WiFi link cannot be reported because WiFi is disabled or the device is in standby. The
    /// device answers SCPI error <c>-200</c> ("WiFi not ready (device powered up?)") rather than a
    /// state. Power the device up (<c>SYSTem:POWer:STATe 1</c>) and enable the LAN interface
    /// before polling again; this is not a fault.
    /// </summary>
    Unavailable = 0,

    /// <summary>
    /// The WiFi module is still being brought up — normal for a couple of seconds after power-up
    /// or after new settings are applied. Expect it to clear; poll again. Firmware mnemonic
    /// <c>INIT</c>.
    /// </summary>
    Initializing = 1,

    /// <summary>
    /// The WiFi module failed to initialise and will keep retrying without ever starting a
    /// network. This does not clear on its own: treat the module as down and reset or reflash
    /// it. Firmware mnemonic <c>INITFAULT</c>.
    /// </summary>
    InitializationFault = 2,

    /// <summary>
    /// The radio is up but has no link: a device joining an existing network has not associated
    /// with it yet, or a self-hosted access point has not finished starting. Firmware mnemonic
    /// <c>NOLINK</c>.
    /// </summary>
    NoLink = 3,

    /// <summary>
    /// The device's self-hosted access point is up and beaconing, with nobody connected to it.
    /// Unlike <see cref="NoLink"/>, this confirms the module is live without needing a peer.
    /// Firmware mnemonic <c>APIDLE</c>.
    /// </summary>
    AccessPointIdle = 4,

    /// <summary>
    /// A peer is attached: the device has associated with an existing network, or a station has
    /// joined its self-hosted access point. Firmware mnemonic <c>CONNECTED</c>.
    /// </summary>
    /// <remarks>
    /// The firmware latches this from association events rather than measuring it live, and it
    /// documents a path (an in-place access-point restart) that can leave it reported after the
    /// peer has gone (daqifi-nyquist-firmware #1060). Treat it as "a peer attached", not as proof
    /// that one is attached right now.
    /// </remarks>
    Connected = 5
}
