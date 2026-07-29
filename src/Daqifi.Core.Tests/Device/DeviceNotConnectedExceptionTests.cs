using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Network;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Daqifi.Core.Tests.Device
{
    /// <summary>
    /// Tests for issue #395 — the up-front connectivity guards must throw the typed
    /// <see cref="DeviceNotConnectedException"/> so clients can classify "the device went away"
    /// (ordinary and expected) apart from a genuine application defect, without matching on the
    /// exception message.
    /// </summary>
    public class DeviceNotConnectedExceptionTests
    {
        // ── Type contract ───────────────────────────────────────────────────

        [Fact]
        public void DeviceNotConnectedException_DerivesFromInvalidOperationException()
        {
            // The guards previously threw a plain InvalidOperationException. Deriving keeps
            // existing catch (InvalidOperationException) sites working unchanged.
            Assert.IsAssignableFrom<InvalidOperationException>(new DeviceNotConnectedException());
        }

        [Fact]
        public void DeviceNotConnectedException_IsNotATransportNotConnectedException()
        {
            // The two typed connectivity exceptions are deliberate siblings, not a hierarchy: a
            // device can fail its guard while its transport is healthy (mid-Disconnect), and a
            // transport can drop while the device still reports Connected.
            Assert.IsNotAssignableFrom<TransportNotConnectedException>(new DeviceNotConnectedException());
            Assert.IsNotAssignableFrom<DeviceNotConnectedException>(new TransportNotConnectedException());
        }

        [Fact]
        public void DeviceNotConnectedException_DefaultMessage_MatchesThePreviousGuardWording()
        {
            // Kept byte-for-byte so downstream code still matching on the message keeps working
            // while it migrates to the type.
            Assert.Equal("Device is not connected.", new DeviceNotConnectedException().Message);
        }

        [Fact]
        public void DeviceNotConnectedException_IsShuttingDown_DefaultsToFalse()
        {
            Assert.False(new DeviceNotConnectedException().IsShuttingDown);
            Assert.False(new DeviceNotConnectedException("custom").IsShuttingDown);
            Assert.False(new DeviceNotConnectedException("custom", new Exception("inner")).IsShuttingDown);
        }

        [Fact]
        public void DeviceNotConnectedException_PreservesMessageAndInnerException()
        {
            var inner = new Exception("inner");
            var ex = new DeviceNotConnectedException("custom", inner);

            Assert.Equal("custom", ex.Message);
            Assert.Same(inner, ex.InnerException);
        }

        [Fact]
        public void DeviceNotConnectedException_ShuttingDownConstructor_SetsTheFlag()
        {
            var ex = new DeviceNotConnectedException("tearing down", isShuttingDown: true);

            Assert.True(ex.IsShuttingDown);
            Assert.Equal("tearing down", ex.Message);
        }

        // ── Guard sites: synchronous API ────────────────────────────────────

        public static IEnumerable<object[]> SynchronousGuardSites()
        {
            yield return Site("StartStreaming", d => d.StartStreaming());
            yield return Site("StopStreaming", d => d.StopStreaming());
            yield return Site("Reboot", d => d.Reboot());
            yield return Site("DisableAllChannels", d => d.DisableAllChannels());
            yield return Site("SetAnalogOutput", d => d.SetAnalogOutput(0, 1.0));
            yield return Site("SetPwmFrequency", d => d.SetPwmFrequency(1000));
            yield return Site("SaveAdcCalibration", d => d.SaveAdcCalibration());
            yield return Site("LoadAdcCalibration", d => d.LoadAdcCalibration());
            yield return Site("SaveFactoryAdcCalibration", d => d.SaveFactoryAdcCalibration());
            yield return Site("LoadFactoryAdcCalibration", d => d.LoadFactoryAdcCalibration());
            yield return Site("UseAdcCalibration", d => d.UseAdcCalibration(1));
            yield return Site("SetAdcCalibrationSlope", d => d.SetAdcCalibrationSlope(0, 1.0));
            yield return Site("SetAdcCalibrationOffset", d => d.SetAdcCalibrationOffset(0, 0.0));
            yield return Site("SaveVoltagePrecision", d => d.SaveVoltagePrecision());
            yield return Site("LoadVoltagePrecision", d => d.LoadVoltagePrecision());
            yield return Site("PrepareSdInterface", d => d.PrepareSdInterface());
            yield return Site("PrepareLanInterface", d => d.PrepareLanInterface());
            yield return Site("SetSdCardMinimumFreeSpace", d => d.SetSdCardMinimumFreeSpace(52_428_800));
            yield return Site("Send", d => d.Send(new ScpiMessage("*IDN?")));

            static object[] Site(string name, Action<DaqifiStreamingDevice> call) => [name, call];
        }

        [Theory]
        [MemberData(nameof(SynchronousGuardSites))]
        public void SynchronousGuard_WhenDisconnected_ThrowsDeviceNotConnected(
            string siteName,
            Action<DaqifiStreamingDevice> call)
        {
            _ = siteName;
            var device = new DaqifiStreamingDevice("TestDevice");

            var ex = Assert.Throws<DeviceNotConnectedException>(() => call(device));

            Assert.Equal("Device is not connected.", ex.Message);
            Assert.False(ex.IsShuttingDown);
        }

        // ── Guard sites: asynchronous API (SD, network, diagnostics) ────────

        public static IEnumerable<object[]> AsynchronousGuardSites()
        {
            yield return Site("GetSdCardFilesAsync", d => d.GetSdCardFilesAsync());
            yield return Site("GetSdCardStorageAsync", d => d.GetSdCardStorageAsync());
            yield return Site("CheckSdCardSpaceAsync", d => d.CheckSdCardSpaceAsync());
            yield return Site("StartSdCardLoggingAsync", d => d.StartSdCardLoggingAsync());
            yield return Site("StartSdCardLoggingSessionAsync", d => d.StartSdCardLoggingSessionAsync());
            yield return Site("StopSdCardLoggingAsync", d => d.StopSdCardLoggingAsync());
            yield return Site("DeleteSdCardFileAsync", d => d.DeleteSdCardFileAsync("test.bin"));
            yield return Site("FormatSdCardAsync", d => d.FormatSdCardAsync());
            yield return Site("DownloadSdCardFileAsync", d => d.DownloadSdCardFileAsync("test.bin", new MemoryStream()));
            yield return Site("UpdateNetworkConfigurationAsync", d => d.UpdateNetworkConfigurationAsync(
                new NetworkConfiguration(WifiMode.ExistingNetwork, WifiSecurityType.WpaPskPhrase, "ssid", "pw")));
            yield return Site("LoadNetworkConfigurationAsync", d => d.LoadNetworkConfigurationAsync());
            yield return Site("FactoryResetNetworkAsync", d => d.FactoryResetNetworkAsync());
            yield return Site("GetLanChipInfoAsync", d => d.GetLanChipInfoAsync());
            yield return Site("GetSystemLogAsync", d => d.GetSystemLogAsync());
            yield return Site("ClearSystemLogAsync", d => d.ClearSystemLogAsync());
            yield return Site("SetLogLevelAsync", d => d.SetLogLevelAsync("STREAM", 2));
            yield return Site("GetCommandHistoryAsync", d => d.GetCommandHistoryAsync());
            yield return Site("TestSystemLogAsync", d => d.TestSystemLogAsync());
            yield return Site("GetSystemErrorCountAsync", d => d.GetSystemErrorCountAsync());
            yield return Site("GetStreamStatsAsync", d => d.GetStreamStatsAsync());
            yield return Site("GetMemoryDiagnosticsAsync", d => d.GetMemoryDiagnosticsAsync());
            yield return Site("SetFriendlyNameAsync", d => d.SetFriendlyNameAsync("Lab Nq1"));

            static object[] Site(string name, Func<DaqifiStreamingDevice, Task> call) => [name, call];
        }

        [Theory]
        [MemberData(nameof(AsynchronousGuardSites))]
        public async Task AsynchronousGuard_WhenDisconnected_ThrowsDeviceNotConnected(
            string siteName,
            Func<DaqifiStreamingDevice, Task> call)
        {
            _ = siteName;
            var device = new DaqifiStreamingDevice("TestDevice");

            var ex = await Assert.ThrowsAsync<DeviceNotConnectedException>(() => call(device));

            Assert.Equal("Device is not connected.", ex.Message);
            Assert.False(ex.IsShuttingDown);
        }

        [Fact]
        public async Task InitializeAsync_WhenDisconnected_ThrowsDeviceNotConnected()
        {
            // This guard keeps its own wording, so it is worth pinning separately: the type is
            // what callers classify on, not the message.
            var device = new DaqifiStreamingDevice("TestDevice");

            var ex = await Assert.ThrowsAsync<DeviceNotConnectedException>(() => device.InitializeAsync());

            Assert.Equal("Device must be connected before initialization.", ex.Message);
            Assert.False(ex.IsShuttingDown);
        }

        // ── The disposing / disconnecting distinction ───────────────────────

        [Fact]
        public async Task TextCommand_WhenNotConnected_ThrowsWithIsShuttingDownFalse()
        {
            var device = new TextCommandTestableDevice("TestDevice");

            var ex = await Assert.ThrowsAsync<DeviceNotConnectedException>(
                () => device.CallExecuteTextCommandAsync());

            Assert.Equal("Device is not connected.", ex.Message);
            Assert.False(ex.IsShuttingDown);
        }

        [Fact]
        public async Task TextCommand_WhenDisconnecting_ThrowsWithIsShuttingDownTrue()
        {
            // The real disconnect race the issue describes: Disconnect() sets _isDisconnecting
            // before the transport check further down is ever reached, so this guard is what an
            // in-flight caller actually sees.
            var device = new TextCommandTestableDevice("TestDevice");
            SetPrivateField(device, "_isDisconnecting", true);

            var ex = await Assert.ThrowsAsync<DeviceNotConnectedException>(
                () => device.CallExecuteTextCommandAsync());

            Assert.True(ex.IsShuttingDown);
            Assert.Contains("disposing or disconnecting", ex.Message);
        }

        [Fact]
        public async Task TextCommand_WhenDisposed_ThrowsWithIsShuttingDownTrue()
        {
            var device = new TextCommandTestableDevice("TestDevice");
            SetPrivateField(device, "_disposed", true);

            var ex = await Assert.ThrowsAsync<DeviceNotConnectedException>(
                () => device.CallExecuteTextCommandAsync());

            Assert.True(ex.IsShuttingDown);
            Assert.Contains("disposing or disconnecting", ex.Message);
        }

        [Fact]
        public async Task TextCommand_WhenLockAlreadyDisposedByRacingDispose_ThrowsWithIsShuttingDownTrue()
        {
            // Dispose() disposed the text-exchange semaphore while this caller was about to wait
            // on it. That path is also "the device went away", so it carries the same flag rather
            // than leaking a low-level ObjectDisposedException.
            var device = new TextCommandTestableDevice("TestDevice");
            var semaphore = (SemaphoreSlim)typeof(DaqifiDevice)
                .GetField("_textExchangeLock", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(device)!;
            semaphore.Dispose();

            var ex = await Assert.ThrowsAsync<DeviceNotConnectedException>(
                () => device.CallExecuteTextCommandAsync());

            Assert.True(ex.IsShuttingDown);
            Assert.Contains("disposed", ex.Message);

            // The translation must not discard the cause: the original
            // ObjectDisposedException survives so this rare race stays diagnosable.
            Assert.IsType<ObjectDisposedException>(ex.InnerException);
        }

        [Fact]
        public void DeviceNotConnectedException_CanCarryBothAnInnerExceptionAndTheShutdownFlag()
        {
            var inner = new ObjectDisposedException("SemaphoreSlim");
            var ex = new DeviceNotConnectedException("tearing down", inner, isShuttingDown: true);

            Assert.Equal("tearing down", ex.Message);
            Assert.Same(inner, ex.InnerException);
            Assert.True(ex.IsShuttingDown);
        }

        [Fact]
        public async Task TextCommand_ReEntrancyGuard_StaysAPlainInvalidOperationException()
        {
            // Re-entering ExecuteTextCommandAsync from a setupAction is an application defect,
            // not a connectivity condition — it must NOT be classified as "the device went away".
            var device = new TextCommandTestableDevice("TestDevice");
            var flag = (AsyncLocal<bool>)typeof(DaqifiDevice)
                .GetField("_isInsideTextExchange", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(device)!;
            flag.Value = true;

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => device.CallExecuteTextCommandAsync());

            Assert.IsNotAssignableFrom<DeviceNotConnectedException>(ex);
            Assert.Contains("not re-entrant", ex.Message);
        }

        // ── Source compatibility for existing consumers ─────────────────────

        [Fact]
        public void ExistingCatchOfInvalidOperationException_StillCatchesTheGuard()
        {
            var device = new DaqifiStreamingDevice("TestDevice");
            var caught = false;

            try
            {
                device.StartStreaming();
            }
            catch (InvalidOperationException)
            {
                caught = true;
            }

            Assert.True(caught);
        }

        [Fact]
        public void GuardException_CanBeClassifiedApartFromAnUnrelatedInvalidOperation()
        {
            // The whole point of the issue: a disconnect is separable from a real defect without
            // reading either exception's message.
            var device = new DaqifiStreamingDevice("TestDevice");

            var disconnect = Record.Exception(() => device.StartStreaming());
            Exception defect = new InvalidOperationException("a genuine bug");

            Assert.IsType<DeviceNotConnectedException>(disconnect);
            Assert.IsNotAssignableFrom<DeviceNotConnectedException>(defect);
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private static void SetPrivateField(DaqifiDevice device, string fieldName, object value)
        {
            typeof(DaqifiDevice)
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(device, value);
        }

        /// <summary>
        /// Exposes the protected <c>ExecuteTextCommandAsync</c> so the text-path guards can be
        /// exercised directly. The real method runs, guards included.
        /// </summary>
        private sealed class TextCommandTestableDevice : DaqifiDevice
        {
            public TextCommandTestableDevice(string name, IPAddress? ipAddress = null)
                : base(name, ipAddress)
            {
            }

            public Task<IReadOnlyList<string>> CallExecuteTextCommandAsync()
            {
                return ExecuteTextCommandAsync(() => { }, responseTimeoutMs: 100, completionTimeoutMs: 50);
            }
        }
    }
}
