namespace Daqifi.Core.Firmware;

/// <summary>
/// Thrown by <see cref="ILanChipInfoProvider.GetLanChipInfoAsync"/> when the device
/// responds to <c>SYSTem:COMMunicate:LAN:GETChipInfo?</c> with a SCPI <c>-200</c>
/// ("Execution error") instead of JSON. This is distinct from a generic unparseable
/// response: it means the WiFi module's saved settings report enabled
/// (<c>LAN:ENAbled? = 1</c>) but the WINC1500 state machine has not reached
/// <c>INITIALIZED</c> yet — a steady-state condition, not the transient post-reboot
/// startup window already covered by bounded retry (closes #144). A single
/// <c>SYSTem:COMMunicate:LAN:APPLY</c> resolves it within a couple of seconds (#203).
/// </summary>
public sealed class LanNotInitializedException : Exception
{
    /// <summary>
    /// Initializes a new instance of <see cref="LanNotInitializedException"/>.
    /// </summary>
    /// <param name="message">The raw SCPI error line reported by the device.</param>
    public LanNotInitializedException(string message) : base(message)
    {
    }
}
