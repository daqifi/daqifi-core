using System;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Device;
using Xunit;

namespace Daqifi.Core.Tests.Device
{
    /// <summary>
    /// Tests for #186 — ExecuteTextCommandAsync must serialize concurrent
    /// callers (SemaphoreSlim), reject re-entrant calls from the same
    /// async flow (InvalidOperationException, not deadlock), and reject
    /// calls when the device is disposed or disconnecting.
    ///
    /// The protected method is exercised via a thin subclass that exposes
    /// it. The disposed/disconnecting guards are tested by setting the
    /// relevant private fields via reflection — those guards run before
    /// any transport / consumer interaction, so this gives faithful
    /// coverage without a transport stack. Re-entrancy is tested by
    /// flipping the AsyncLocal flag from inside the same logical flow.
    /// </summary>
    public class DaqifiDeviceTextCommandLockTests
    {
        [Fact]
        public async Task ExecuteTextCommandAsync_WhenAlreadyInsideAsyncFlow_ThrowsInvalidOperation()
        {
            var device = new TextCommandTestableDevice("TestDevice");

            // Simulate "we're already inside ExecuteTextCommandAsync on this
            // async flow" by setting the AsyncLocal flag. The re-entrancy
            // guard runs before WaitAsync(), so this check fires immediately
            // without touching any transport state.
            GetIsInsideTextExchange(device).Value = true;

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
            // If the lock leaked, this would deadlock and xunit's per-test
            // budget would time it out instead.
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => device.CallExecuteTextCommandAsync(() => { }));
        }

        [Fact]
        public async Task ExecuteTextCommandAsync_AsyncLocalClearedAfterReturn()
        {
            // Even when the call throws, the AsyncLocal re-entrancy flag
            // is cleared in the finally block so a subsequent call from
            // the same flow doesn't false-positive the re-entrancy check.
            var device = new TextCommandTestableDevice("TestDevice");

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => device.CallExecuteTextCommandAsync(() => { }));

            Assert.False(GetIsInsideTextExchange(device).Value);
        }

        // ── Reflection helpers — kept private to this test class so the
        // production DaqifiDevice doesn't have to expose internals. ─────

        private static AsyncLocal<bool> GetIsInsideTextExchange(DaqifiDevice device)
        {
            return (AsyncLocal<bool>)typeof(DaqifiDevice)
                .GetField("_isInsideTextExchange", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(device)!;
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
