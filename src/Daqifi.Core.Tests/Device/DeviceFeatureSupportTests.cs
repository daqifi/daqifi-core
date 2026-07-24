using Daqifi.Core.Device;
using Daqifi.Core.Firmware;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Daqifi.Core.Tests.Device
{
    /// <summary>
    /// Covers the <see cref="DaqifiDevice.Supports"/> / <see cref="DaqifiDevice.EnsureSupported"/>
    /// seam and the requirement table behind it (ADR 0001, issue #256). Each feature is exercised
    /// across every axis the table can constrain: firmware version (below / at / above the minimum,
    /// plus absent and unparseable), board variant, and hardware presence.
    /// </summary>
    public class DeviceFeatureSupportTests
    {
        private static DaqifiDevice DeviceOn(
            DeviceType board,
            string firmwareVersion = "",
            DeviceCapabilities? capabilities = null)
        {
            var device = new DaqifiDevice("TestDevice");
            device.Metadata.DeviceType = board;
            device.Metadata.FirmwareVersion = firmwareVersion;
            device.Metadata.Capabilities = capabilities ?? DeviceCapabilities.FromDeviceType(board);
            return device;
        }

        // ---- Table integrity ------------------------------------------------------------------

        [Fact]
        public void RequirementTable_CoversEveryDeviceFeatureMember()
        {
            // A DeviceFeature added without a table entry would throw at the first Supports() call
            // rather than mis-gate silently — this catches it at build time instead.
            var members = Enum.GetValues<DeviceFeature>();
            var defined = DeviceFeatureRequirements.DefinedFeatures.ToHashSet();

            Assert.Equal(members.Length, defined.Count);
            Assert.All(members, feature => Assert.Contains(feature, defined));
        }

        [Fact]
        public void Supports_WhenFeatureHasNoTableEntry_ThrowsArgumentOutOfRange()
        {
            var device = DeviceOn(DeviceType.Nyquist1, "3.7.0");
            var undefined = (DeviceFeature)9999;

            Assert.Throws<ArgumentOutOfRangeException>(() => device.Supports(undefined));
        }

        [Fact]
        public void RequirementTable_SdFileTransferOverWifi_RequiresV3_7_0()
        {
            // The above-floor gate migrated from PR #347 — pinned so a table edit can't silently
            // relax it back below the version firmware #598/#599 actually shipped in.
            var requirement = DeviceFeatureRequirements.For(DeviceFeature.SdFileTransferOverWifi);

            Assert.Equal(new FirmwareVersion(3, 7, 0, null, 0), requirement.MinVersion);
            Assert.Equal(
                HardwareRequirement.SdCard | HardwareRequirement.WiFi,
                requirement.Hardware);
        }

        [Fact]
        public void RequirementTable_SdStorageQuery_ReportsTheSupportedFloorNotTheIntroducingVersion()
        {
            // SYSTem:STORage:SD:SPACe? shipped in v3.4.6b1, but the floor is what daqifi-core
            // guarantees, so that is the version the typed exception reports.
            var requirement = DeviceFeatureRequirements.For(DeviceFeature.SdStorageQuery);

            Assert.Equal(DaqifiDevice.MinSupportedFirmware, requirement.MinVersion);
        }

        // ---- Firmware-version axis ------------------------------------------------------------

        /// <summary>
        /// Version-gated features with a real released firmware version on each side of the
        /// boundary. Spelled out rather than computed from the minimum: deriving "one below" by
        /// decrementing a component can yield an unparseable string (e.g. a 3.0.0 minimum), which
        /// would still report unsupported — passing the test for the wrong reason.
        /// </summary>
        public static IEnumerable<object[]> VersionGatedFeatures() =>
            new List<object[]>
            {
                //                feature                                below,    at min,   above
                new object[] { DeviceFeature.SdFileTransferOverWifi, "3.6.3", "3.7.0", "3.7.2" },
                new object[] { DeviceFeature.CapabilityDocument,     "3.4.4", "3.5.0", "3.6.0" },
                new object[] { DeviceFeature.SdStorageQuery,         "3.4.3", "3.5.0", "3.6.0" }
            };

        [Theory]
        [MemberData(nameof(VersionGatedFeatures))]
        public void Supports_AcrossTheFirmwareVersionBoundary(
            DeviceFeature feature, string below, string atMin, string above)
        {
            // Guards the data itself: a "below" that stopped parsing would report unsupported via
            // the fail-closed path instead of the comparison under test.
            Assert.True(FirmwareVersion.TryParse(below, out _));

            Assert.False(DeviceOn(DeviceType.Nyquist1, below).Supports(feature));
            Assert.True(DeviceOn(DeviceType.Nyquist1, atMin).Supports(feature));
            Assert.True(DeviceOn(DeviceType.Nyquist1, above).Supports(feature));
        }

        [Theory]
        [InlineData("")]
        [InlineData("not-a-version")]
        [InlineData("999999999999999999.0.0")] // overflows Int32 — must fail closed, not crash
        public void Supports_WhenFirmwareVersionAbsentOrUnparseable_FailsClosed(string firmwareVersion)
        {
            // An unknown version is not permission: dispatching an SD command over WiFi to
            // pre-v3.7.0 firmware stalls on the shared SPI bus.
            var device = DeviceOn(DeviceType.Nyquist1, firmwareVersion);

            Assert.False(device.Supports(DeviceFeature.SdFileTransferOverWifi));
            Assert.False(device.Supports(DeviceFeature.CapabilityDocument));
            Assert.False(device.Supports(DeviceFeature.SdStorageQuery));
        }

        [Fact]
        public void Supports_PreReleaseBelowMinimum_ReturnsFalse()
        {
            // 3.7.0b1 precedes the 3.7.0 release under FirmwareVersion's precedence rules.
            var device = DeviceOn(DeviceType.Nyquist1, "3.7.0b1");

            Assert.False(device.Supports(DeviceFeature.SdFileTransferOverWifi));
        }

        [Fact]
        public void Supports_EvaluatesLiveAgainstCurrentMetadata()
        {
            // The board and the firmware version arrive in separate status-message branches, so a
            // cached flag would be snapshotted before one of them. Guard against reintroducing one.
            var device = DeviceOn(DeviceType.Nyquist1, "3.6.3");
            Assert.False(device.Supports(DeviceFeature.SdFileTransferOverWifi));

            device.Metadata.FirmwareVersion = "3.7.0";
            Assert.True(device.Supports(DeviceFeature.SdFileTransferOverWifi));
        }

        // ---- Board axis -----------------------------------------------------------------------

        [Fact]
        public void Supports_AnalogOutput_OnNyquist3_ReturnsTrue()
        {
            var device = DeviceOn(DeviceType.Nyquist3, "3.5.0");

            Assert.True(device.Supports(DeviceFeature.AnalogOutput));
        }

        [Theory]
        [InlineData(DeviceType.Nyquist1)]
        [InlineData(DeviceType.Nyquist2)]
        public void Supports_AnalogOutput_OnNonNyquist3Board_ReturnsFalse(DeviceType board)
        {
            // Board-gated, not version-gated: the newest firmware on an NQ1 still has no DAC.
            var device = DeviceOn(board, "3.7.2");

            Assert.False(device.Supports(DeviceFeature.AnalogOutput));
        }

        [Fact]
        public void Supports_AnalogOutput_IsNotVersionGated()
        {
            var device = DeviceOn(DeviceType.Nyquist3, firmwareVersion: "");

            Assert.True(device.Supports(DeviceFeature.AnalogOutput));
        }

        // ---- Hardware axis --------------------------------------------------------------------

        [Fact]
        public void Supports_SdFileTransferOverWifi_WhenSdHardwareAbsent_ReturnsFalse()
        {
            var noSd = DeviceCapabilities.FromDeviceType(DeviceType.Nyquist1);
            noSd.HasSdCard = false;
            var device = DeviceOn(DeviceType.Nyquist1, "3.7.0", noSd);

            Assert.False(device.Supports(DeviceFeature.SdFileTransferOverWifi));
        }

        [Fact]
        public void Supports_SdFileTransferOverWifi_WhenWifiHardwareAbsent_ReturnsFalse()
        {
            var noWifi = DeviceCapabilities.FromDeviceType(DeviceType.Nyquist1);
            noWifi.HasWiFi = false;
            var device = DeviceOn(DeviceType.Nyquist1, "3.7.0", noWifi);

            Assert.False(device.Supports(DeviceFeature.SdFileTransferOverWifi));
        }

        [Fact]
        public void Supports_SdStorageQuery_WhenSdHardwareAbsent_ReturnsFalse()
        {
            var noSd = DeviceCapabilities.FromDeviceType(DeviceType.Nyquist1);
            noSd.HasSdCard = false;
            var device = DeviceOn(DeviceType.Nyquist1, "3.5.0", noSd);

            Assert.False(device.Supports(DeviceFeature.SdStorageQuery));
        }

        [Fact]
        public void Supports_CapabilityDocument_HasNoHardwareRequirement()
        {
            var bare = new DeviceCapabilities();
            var device = DeviceOn(DeviceType.Nyquist1, "3.5.0", bare);

            Assert.True(device.Supports(DeviceFeature.CapabilityDocument));
        }

        // ---- Unknown board --------------------------------------------------------------------

        [Fact]
        public void Supports_WhenBoardUnknown_SkipsBoardAndHardwareRequirements()
        {
            // Capabilities are all-false until FromDeviceType runs, which means "not yet known",
            // not "hardware absent" — so a device that has reported a firmware version but not yet
            // a part number is not refused. The wire-level -113 remains the backstop.
            var device = DeviceOn(DeviceType.Unknown, "3.7.0");

            Assert.False(device.Metadata.Capabilities.HasSdCard);
            Assert.True(device.Supports(DeviceFeature.SdFileTransferOverWifi));
            Assert.True(device.Supports(DeviceFeature.AnalogOutput));
        }

        [Fact]
        public void Supports_WhenBoardUnknown_StillEnforcesTheVersionGate()
        {
            // The version axis is independent of the board: an unknown board does not waive it.
            var device = DeviceOn(DeviceType.Unknown, "3.6.3");

            Assert.False(device.Supports(DeviceFeature.SdFileTransferOverWifi));
        }

        // ---- EnsureSupported ------------------------------------------------------------------

        [Fact]
        public void EnsureSupported_WhenSupported_DoesNotThrow()
        {
            var device = DeviceOn(DeviceType.Nyquist1, "3.7.0");

            device.EnsureSupported(DeviceFeature.SdFileTransferOverWifi);
        }

        [Fact]
        public void EnsureSupported_WhenUnsupported_ThrowsWithFeatureRequiredActualAndBoard()
        {
            var device = DeviceOn(DeviceType.Nyquist1, "3.6.3");

            var ex = Assert.Throws<FeatureNotSupportedException>(
                () => device.EnsureSupported(DeviceFeature.SdFileTransferOverWifi));

            Assert.Equal(DeviceFeature.SdFileTransferOverWifi, ex.Feature);
            Assert.Equal(new FirmwareVersion(3, 7, 0, null, 0), ex.RequiredVersion);
            Assert.Equal("3.6.3", ex.ActualVersion);
            Assert.Equal(DeviceType.Nyquist1, ex.Board);
        }

        [Fact]
        public void EnsureSupported_WhenBoardUnknown_ReportsNullBoard()
        {
            // DeviceType.Unknown is "not reported", so the exception says nothing about the board
            // rather than naming a placeholder value.
            var device = DeviceOn(DeviceType.Unknown, "3.6.3");

            var ex = Assert.Throws<FeatureNotSupportedException>(
                () => device.EnsureSupported(DeviceFeature.SdFileTransferOverWifi));

            Assert.Null(ex.Board);
            Assert.Equal("3.6.3", ex.ActualVersion);
        }

        [Fact]
        public void EnsureSupported_ForBoardGatedFeature_ReportsNoRequiredVersion()
        {
            var device = DeviceOn(DeviceType.Nyquist1, "3.7.2");

            var ex = Assert.Throws<FeatureNotSupportedException>(
                () => device.EnsureSupported(DeviceFeature.AnalogOutput));

            Assert.Equal(DeviceFeature.AnalogOutput, ex.Feature);
            Assert.Null(ex.RequiredVersion);
            Assert.Equal(DeviceType.Nyquist1, ex.Board);
        }

        [Fact]
        public void EnsureSupported_WhenHardwareIsTheFailingAxis_DoesNotClaimAFirmwareRequirement()
        {
            // Regression: the exception used to take its required version straight from the table
            // regardless of which axis failed, so a device whose firmware already met the minimum
            // but lacked the SD card was told "Requires firmware >= 3.7.0; the device reports
            // '3.7.0'" — self-contradictory, and pointing at an upgrade that cannot fix it.
            var noSd = DeviceCapabilities.FromDeviceType(DeviceType.Nyquist1);
            noSd.HasSdCard = false;
            var device = DeviceOn(DeviceType.Nyquist1, "3.7.0", noSd);

            var ex = Assert.Throws<FeatureNotSupportedException>(
                () => device.EnsureSupported(DeviceFeature.SdFileTransferOverWifi));

            Assert.Equal(DeviceFeature.SdFileTransferOverWifi, ex.Feature);
            Assert.Null(ex.RequiredVersion);
            Assert.DoesNotContain("Requires firmware", ex.Message);
            Assert.Equal(DeviceType.Nyquist1, ex.Board);
        }

        [Fact]
        public void EnsureSupported_WhenVersionIsTheFailingAxis_StillReportsTheRequirement()
        {
            // The other half of the same rule: when the version *is* what failed, the required
            // version must still be reported — that is the caller's actionable next step.
            var device = DeviceOn(DeviceType.Nyquist1, "3.6.3");

            var ex = Assert.Throws<FeatureNotSupportedException>(
                () => device.EnsureSupported(DeviceFeature.SdFileTransferOverWifi));

            Assert.Equal(new FirmwareVersion(3, 7, 0, null, 0), ex.RequiredVersion);
            Assert.Contains("Requires firmware >= 3.7.0", ex.Message);
        }

        [Fact]
        public void RequirementTable_BoardAllowListIsImmutable()
        {
            // The table hands the same instance to every caller, so a mutable allow-list would let
            // any friend-assembly code silently re-gate the feature process-wide.
            var boards = DeviceFeatureRequirements.For(DeviceFeature.AnalogOutput).Boards;

            Assert.True(boards.HasValue);
            Assert.Equal(new[] { DeviceType.Nyquist3 }, boards!.Value);
        }
    }
}
