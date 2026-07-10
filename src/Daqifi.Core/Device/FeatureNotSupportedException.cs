using System;
using Daqifi.Core.Firmware;

#nullable enable

namespace Daqifi.Core.Device
{
    /// <summary>
    /// Thrown when a device does not support a requested <see cref="DeviceFeature"/> — typically
    /// because its firmware predates the feature's minimum version, per ADR 0001
    /// (docs/adr/0001-firmware-feature-gating.md). The firmware's <c>**ERROR: -113, "Undefined
    /// header"</c> reply (libscpi's <c>SCPI_ERROR_UNDEFINED_HEADER</c>) is the authoritative signal
    /// on the command path; an up-front check against <see cref="DaqifiDevice.MinSupportedFirmware"/>
    /// or <see cref="DaqifiDevice.IsFirmwareVersionSupported"/> can pre-empt the round-trip for UI.
    /// </summary>
    public sealed class FeatureNotSupportedException : Exception
    {
        /// <summary>
        /// Gets the feature the device does not support.
        /// </summary>
        public DeviceFeature Feature { get; }

        /// <summary>
        /// Gets the minimum firmware version required for <see cref="Feature"/>, if known.
        /// </summary>
        public FirmwareVersion? RequiredVersion { get; }

        /// <summary>
        /// Gets the device's reported firmware version string as last received. May be empty
        /// (not yet reported) or unparseable — this is the raw value, not a parsed
        /// <see cref="FirmwareVersion"/>, so it is preserved even when it doesn't parse.
        /// </summary>
        public string? ActualVersion { get; }

        /// <summary>
        /// Gets the device's board type, if known.
        /// </summary>
        public DeviceType? Board { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="FeatureNotSupportedException"/> class.
        /// </summary>
        /// <param name="feature">The feature the device does not support.</param>
        /// <param name="requiredVersion">The minimum firmware version required for <paramref name="feature"/>, if known.</param>
        /// <param name="actualVersion">The device's reported firmware version string, if known.</param>
        /// <param name="board">The device's board type, if known.</param>
        public FeatureNotSupportedException(
            DeviceFeature feature,
            FirmwareVersion? requiredVersion = null,
            string? actualVersion = null,
            DeviceType? board = null)
            : base(BuildMessage(feature, requiredVersion, actualVersion, board))
        {
            Feature = feature;
            RequiredVersion = requiredVersion;
            ActualVersion = actualVersion;
            Board = board;
        }

        private static string BuildMessage(
            DeviceFeature feature,
            FirmwareVersion? requiredVersion,
            string? actualVersion,
            DeviceType? board)
        {
            var message = $"The device does not support '{feature}'.";

            if (requiredVersion.HasValue)
            {
                message += string.IsNullOrEmpty(actualVersion)
                    ? $" Requires firmware >= {requiredVersion.Value}; the device's firmware version is unknown."
                    : $" Requires firmware >= {requiredVersion.Value}; the device reports '{actualVersion}'.";
            }

            if (board.HasValue)
            {
                message += $" Board: {board.Value}.";
            }

            return message;
        }
    }
}
