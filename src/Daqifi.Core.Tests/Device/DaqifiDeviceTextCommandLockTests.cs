using System;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Device;
using Xunit;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Tests for #186 — a failed <c>ExecuteTextCommandAsync</c> releases its
/// semaphore and clears the AsyncLocal re-entrancy flag. The disconnected
/// and re-entrancy exception shapes are covered by
/// <see cref="DeviceNotConnectedExceptionTests"/>.
///
/// The protected method is exercised via a thin subclass that exposes it.
/// </summary>
public class DaqifiDeviceTextCommandLockTests
{
    [Fact]
    public async Task ExecuteTextCommandAsync_ReleasesLockAfterValidationFailure()
    {
        // After a validation failure (e.g. not connected), the lock
        // must be released so subsequent calls don't hang. Verified
        // by calling twice — second call must reach validation too,
        // not block on WaitAsync.
        var device = new TextCommandTestableDevice("TestDevice");

        await Assert.ThrowsAsync<DeviceNotConnectedException>(
            () => device.CallExecuteTextCommandAsync(() => { }));
        // Second call: also throws, but ONLY if the lock was released.
        // If the lock leaked, this would deadlock and xunit's per-test
        // budget would time it out instead.
        await Assert.ThrowsAsync<DeviceNotConnectedException>(
            () => device.CallExecuteTextCommandAsync(() => { }));
    }

    [Fact]
    public async Task ExecuteTextCommandAsync_AsyncLocalClearedAfterReturn()
    {
        // Even when the call throws, the AsyncLocal re-entrancy flag
        // is cleared in the finally block so a subsequent call from
        // the same flow doesn't false-positive the re-entrancy check.
        var device = new TextCommandTestableDevice("TestDevice");

        await Assert.ThrowsAsync<DeviceNotConnectedException>(
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
