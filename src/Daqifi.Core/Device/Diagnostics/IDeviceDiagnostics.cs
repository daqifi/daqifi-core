using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace Daqifi.Core.Device.Diagnostics
{
    /// <summary>
    /// Exposes the firmware's logging and diagnostics surface — the system log buffer, runtime log
    /// levels, SCPI command history, error-queue depth, and streaming/memory performance counters.
    /// </summary>
    /// <remarks>
    /// These values originate on the device; this interface is a typed wrapper over the corresponding
    /// SCPI queries, not a client-side instrumentation framework. All methods require an established
    /// connection and run as text commands, so avoid issuing them concurrently with each other or with
    /// other text-mode operations on the same device. For reliable parsing, prefer querying while the
    /// device is not actively streaming.
    /// </remarks>
    public interface IDeviceDiagnostics
    {
        /// <summary>
        /// Reads and clears the device system log buffer (<c>SYSTem:LOG?</c>).
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>The buffered log entries, oldest first; empty when the buffer was empty.</returns>
        /// <exception cref="System.InvalidOperationException">Thrown when the device is not connected.</exception>
        Task<IReadOnlyList<SystemLogEntry>> GetSystemLogAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Clears the device system log buffer without reading it (<c>SYSTem:LOG:CLEar</c>).
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <exception cref="System.InvalidOperationException">Thrown when the device is not connected.</exception>
        Task ClearSystemLogAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Sets the runtime log level for a single firmware module (<c>SYSTem:LOG:LEVel</c>).
        /// </summary>
        /// <param name="module">The module name (e.g. <c>STREAM</c>, <c>WIFI</c>, <c>SD</c>). Case-insensitive on the device.</param>
        /// <param name="level">The log level: 0 = None, 1 = Error, 2 = Info, 3 = Debug.</param>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>The level actually applied, as echoed by the device (may be capped by the module's ceiling).</returns>
        /// <exception cref="System.InvalidOperationException">Thrown when the device is not connected.</exception>
        /// <exception cref="System.ArgumentException">Thrown when <paramref name="module"/> is null, empty, or contains invalid characters.</exception>
        /// <exception cref="System.ArgumentOutOfRangeException">Thrown when <paramref name="level"/> is outside 0–3.</exception>
        /// <exception cref="DeviceDiagnosticsException">Thrown when the device rejected the request or returned an unparseable response.</exception>
        Task<LogLevelSetting> SetLogLevelAsync(string module, int level, CancellationToken cancellationToken = default);

        /// <summary>
        /// Reads the device's recent SCPI command history (<c>SYSTem:LOG:CMDHistory?</c>).
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>The remembered commands, newest first; empty when there is no history.</returns>
        /// <exception cref="System.InvalidOperationException">Thrown when the device is not connected.</exception>
        Task<IReadOnlyList<string>> GetCommandHistoryAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Injects a handful of test messages into the system log for pipeline verification (<c>SYSTem:LOG:TEST</c>).
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        /// <exception cref="System.InvalidOperationException">Thrown when the device is not connected.</exception>
        Task TestSystemLogAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Reads the number of entries currently in the SCPI error queue without popping them
        /// (<c>SYSTem:ERRor:COUNt?</c>).
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>The current error-queue depth.</returns>
        /// <exception cref="System.InvalidOperationException">Thrown when the device is not connected.</exception>
        /// <exception cref="DeviceDiagnosticsException">Thrown when the device returned an unparseable response.</exception>
        Task<int> GetSystemErrorCountAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Reads streaming performance counters (<c>SYSTem:STReam:STATS?</c>).
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>The parsed streaming statistics.</returns>
        /// <exception cref="System.InvalidOperationException">Thrown when the device is not connected.</exception>
        /// <exception cref="DeviceDiagnosticsException">Thrown when the device returned an unparseable response.</exception>
        Task<StreamStats> GetStreamStatsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Reads device memory diagnostics (<c>SYSTem:MEMory:FREE?</c>).
        /// </summary>
        /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
        /// <returns>The parsed memory diagnostics.</returns>
        /// <exception cref="System.InvalidOperationException">Thrown when the device is not connected.</exception>
        /// <exception cref="DeviceDiagnosticsException">Thrown when the device returned an unparseable response.</exception>
        Task<MemoryDiagnostics> GetMemoryDiagnosticsAsync(CancellationToken cancellationToken = default);
    }
}
