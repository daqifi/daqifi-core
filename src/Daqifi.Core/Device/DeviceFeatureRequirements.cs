using Daqifi.Core.Firmware;
using System;
using System.Collections.Generic;

#nullable enable

namespace Daqifi.Core.Device
{
    /// <summary>
    /// Physical hardware a <see cref="DeviceFeature"/> requires the device to have, independent of
    /// firmware version. Evaluated against <see cref="DeviceMetadata.Capabilities"/>, which is
    /// derived from the board variant by <see cref="DeviceCapabilities.FromDeviceType"/>.
    /// </summary>
    [Flags]
    internal enum HardwareRequirement
    {
        /// <summary>No hardware requirement beyond being a DAQiFi device.</summary>
        None = 0,

        /// <summary>Requires an SD card slot (<see cref="DeviceCapabilities.HasSdCard"/>).</summary>
        SdCard = 1 << 0,

        /// <summary>Requires WiFi connectivity (<see cref="DeviceCapabilities.HasWiFi"/>).</summary>
        WiFi = 1 << 1
    }

    /// <summary>
    /// What a device must satisfy to support one <see cref="DeviceFeature"/>: a minimum firmware
    /// version, a board-variant allow-list, and/or physical hardware. A <c>null</c> version or
    /// board list means that axis places no constraint on the feature.
    /// </summary>
    /// <param name="MinVersion">
    /// Minimum firmware version, or <c>null</c> when the feature is not version-gated. Compared
    /// against the device's reported version with <see cref="FirmwareVersion.TryParse"/>; an
    /// absent or unparseable version fails the check (see <see cref="DaqifiDevice.Supports"/>).
    /// </param>
    /// <param name="Boards">
    /// Board variants that have the feature, or <c>null</c> when every board does.
    /// </param>
    /// <param name="Hardware">Physical hardware the feature drives.</param>
    internal readonly record struct FeatureRequirement(
        FirmwareVersion? MinVersion,
        DeviceType[]? Boards,
        HardwareRequirement Hardware);

    /// <summary>
    /// The <see cref="DeviceFeature"/> → <see cref="FeatureRequirement"/> table behind
    /// <see cref="DaqifiDevice.Supports"/> (ADR 0001, docs/adr/0001-firmware-feature-gating.md).
    /// Sourced from the firmware audit table in that ADR; when daqifi-core starts consuming a new
    /// firmware-gated command, its <see cref="DeviceFeature"/> member and its entry here are added
    /// together — <see cref="For"/> throws for a member with no entry, and
    /// <c>DeviceFeatureRequirementsTests</c> asserts the table stays exhaustive.
    /// </summary>
    internal static class DeviceFeatureRequirements
    {
        /// <summary>
        /// Minimum firmware for SD-card access (file transfer and the storage-space query) over a
        /// WiFi/TCP connection. Firmware <c>#598/#599</c> (first released <b>v3.7.0</b>) route the
        /// SD reply to the requesting interface; before that the SD card and WiFi contend for the
        /// shared SPI bus, so these operations were USB-only.
        /// </summary>
        internal static readonly FirmwareVersion SdOverWifiMinFirmware = new(3, 7, 0, null, 0);

        /// <summary>
        /// Minimum firmware for the capability document (<c>CONFigure:CAPabilities:JSON?</c> /
        /// <c>:APIVersion?</c>), first released in v3.5.0 (firmware #327/#343).
        /// </summary>
        internal static readonly FirmwareVersion CapabilityDocumentMinFirmware = new(3, 5, 0, null, 0);

        private static readonly DeviceType[] Nyquist3Only = { DeviceType.Nyquist3 };

        private static readonly IReadOnlyDictionary<DeviceFeature, FeatureRequirement> Table =
            new Dictionary<DeviceFeature, FeatureRequirement>
            {
                // Board-gated, not version-gated: the DAC commands shipped in v3.2.0 (below the
                // floor) but the firmware rejects them on any board that isn't NQ3.
                [DeviceFeature.AnalogOutput] = new(
                    MinVersion: null,
                    Boards: Nyquist3Only,
                    Hardware: HardwareRequirement.None),

                // Introduced in v3.4.6b1, below the floor, so this entry is a backstop against
                // below-floor devices only. It reports the floor — not v3.4.6b1 — as the required
                // version, since the floor is what daqifi-core actually guarantees.
                [DeviceFeature.SdStorageQuery] = new(
                    MinVersion: DaqifiDevice.MinSupportedFirmware,
                    Boards: null,
                    Hardware: HardwareRequirement.SdCard),

                [DeviceFeature.CapabilityDocument] = new(
                    MinVersion: CapabilityDocumentMinFirmware,
                    Boards: null,
                    Hardware: HardwareRequirement.None),

                // The first above-floor gate (ADR 0001's trigger for building this table).
                [DeviceFeature.SdFileTransferOverWifi] = new(
                    MinVersion: SdOverWifiMinFirmware,
                    Boards: null,
                    Hardware: HardwareRequirement.SdCard | HardwareRequirement.WiFi)
            };

        /// <summary>
        /// Gets the requirement for <paramref name="feature"/>.
        /// </summary>
        /// <param name="feature">The feature to look up.</param>
        /// <returns>The feature's <see cref="FeatureRequirement"/>.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="feature"/> has no table entry. This is a programming error
        /// — a new <see cref="DeviceFeature"/> member added without its requirement — and throws
        /// rather than defaulting, so the omission can't silently mis-gate the feature in either
        /// direction.
        /// </exception>
        internal static FeatureRequirement For(DeviceFeature feature)
        {
            if (!Table.TryGetValue(feature, out var requirement))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(feature),
                    feature,
                    $"No feature requirement is defined for '{feature}'. Add an entry to {nameof(DeviceFeatureRequirements)}.");
            }

            return requirement;
        }

        /// <summary>
        /// Gets the features that have a table entry. Used by tests to assert the table covers
        /// every <see cref="DeviceFeature"/> member.
        /// </summary>
        internal static IEnumerable<DeviceFeature> DefinedFeatures => Table.Keys;
    }
}
