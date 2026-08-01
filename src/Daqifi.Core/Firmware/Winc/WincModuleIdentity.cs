namespace Daqifi.Core.Firmware.Winc;

/// <summary>
/// A non-destructive summary of the WINC module: what the bridge and the module reported without
/// changing anything. Produced by <see cref="WincModuleInspector.ReadIdentityAsync"/>.
/// </summary>
public sealed class WincModuleIdentity
{
    /// <summary>
    /// Chip identity register (0x1000). A live WINC1500 reads 0x0015xxxx with firmware running, or
    /// 0x0010xxxx once halted in download mode.
    /// </summary>
    public required uint ChipId { get; init; }

    /// <summary>SPI flash JEDEC identification word.</summary>
    public required uint FlashJedecId { get; init; }

    /// <summary>
    /// Whether <see cref="ChipId"/> matches a known WINC1500 family. False means the bridge
    /// answered but the module behind it did not — a different problem from a dead port.
    /// </summary>
    public required bool IsRecognizedWinc { get; init; }

    /// <summary>Link speed in effect when the identity was read.</summary>
    public required int NegotiatedBaudRate { get; init; }
}
