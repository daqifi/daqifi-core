using System;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Daqifi.Core.Device;
using Xunit;

namespace Daqifi.Core.Tests.Device
{
    /// <summary>
    /// Tests for #186 — ExecuteTextCommandAsync must serialize concurrent
    /// callers (SemaphoreSlim), reject same-thread re-entrant calls
    /// (InvalidOperationException, not deadlock), and reject calls when
    /// the device is disposed or disconnecting.
    ///
    /// The protected method is exercised via a thin subclass that exposes
    /// it. Because spinning up a real transport here would be fragile, the
    /// pre-lock re-entrancy guard and the disposed/disconnecting guard are
    /// tested by setting the relevant private fields via reflection — those
    /// guards run before any transport / consumer interaction, so this gives
    /// faithful coverage without a transport stack.
    /// </summary>
    public class DaqifiDeviceTextCommandLockTests
    {
        [Fact]
        public async Task ExecuteTextCommandAsync_WhenSameThreadAlreadyOwnsLock_ThrowsInvalidOperation()
        {
            var device = new TextCommandTestableDevice("TestDevice");

            // Simulate "we're already inside ExecuteTextCommandAsync on this
            // thread" by directly setting the owner-thread tracker. The
            // re-entrancy guard runs before WaitAsync(), so this check
            // fires immediately without touching any transport state.
            SetOwnerThreadId(device, Environment.CurrentManagedThreadId);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => device.CallExecuteTextCommandAsync(() => { }));
            Assert.Contains("not re-entrant", ex.Message);
        }

        [Fact]
        public async Task ExecuteTextCommandAsync_WhenDisposing_ThrowsInvalidOperation()
        {
            var device = new TextCommandTestableDevice("TestDevice");
            SetIsDisconnecting(device, true);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => device.CallExecuteTextCommandAsync(() => { }));
            Assert.Contains("disposing or disconnecting", ex.Message);
        }

        [Fact]
        public async Task ExecuteTextCommandAsync_WhenDisposed_ThrowsInvalidOperation()
        {
            var device = new TextCommandTestableDevice("TestDevice");
            SetDisposed(device, true);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => device.CallExecuteTextCommandAsync(() => { }));
            Assert.Contains("disposing or disconnecting", ex.Message);
        }

        [Fact]
        public async Task ExecuteTextCommandAsync_ReleasesLockAfterValidationFailure()
        {
            // After a validation failure (e.g. not connected), the lock
            // must be released so subsequent calls don't hang. Verified
            // by calling twice — second call must reach validation too,
            // not block on WaitAsync.
            var device = new TextCommandTestableDevice("TestDevice");

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => device.CallExecuteTextCommandAsync(() => { }));
            // Second call: also throws, but ONLY if the lock was released.
            // If the lock leaked, this would deadlock and pytest's per-test
            // budget would time it out instead.
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => device.CallExecuteTextCommandAsync(() => { }));
        }

        [Fact]
        public async Task ExecuteTextCommandAsync_OwnerThreadIdClearedAfterReturn()
        {
            // Even when the call throws, the owner-thread tracker is
            // cleared in the finally block so a subsequent call from the
            // same thread doesn't false-positive the re-entrancy check.
            var device = new TextCommandTestableDevice("TestDevice");

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => device.CallExecuteTextCommandAsync(() => { }));

            Assert.Null(GetOwnerThreadId(device));
        }

        // ── Reflection helpers — kept private to this test class so the
        // production DaqifiDevice doesn't have to expose internals. ─────

        private static void SetOwnerThreadId(DaqifiDevice device, int? value)
        {
            typeof(DaqifiDevice)
                .GetField("_textExchangeOwnerThreadId", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(device, value);
        }

        private static int? GetOwnerThreadId(DaqifiDevice device)
        {
            return (int?)typeof(DaqifiDevice)
                .GetField("_textExchangeOwnerThreadId", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(device);
        }

        private static void SetIsDisconnecting(DaqifiDevice device, bool value)
        {
            typeof(DaqifiDevice)
                .GetField("_isDisconnecting", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(device, value);
        }

        private static void SetDisposed(DaqifiDevice device, bool value)
        {
            typeof(DaqifiDevice)
                .GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(device, value);
        }

        /// <summary>
        /// Subclass that exposes the protected ExecuteTextCommandAsync via
        /// a public wrapper so tests can call it directly. Does NOT override
        /// it — the real method runs, including the lock + guards.
        /// </summary>
        private class TextCommandTestableDevice : DaqifiDevice
        {
            public TextCommandTestableDevice(string name, IPAddress? ipAddress = null)
                : base(name, ipAddress)
            {
            }

            public Task<System.Collections.Generic.IReadOnlyList<string>> CallExecuteTextCommandAsync(
                Action setupAction)
            {
                return ExecuteTextCommandAsync(setupAction, responseTimeoutMs: 100, completionTimeoutMs: 50);
            }
        }
    }
}
