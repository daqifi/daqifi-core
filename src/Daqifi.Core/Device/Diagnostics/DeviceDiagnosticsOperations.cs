using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device.Internal;

#nullable enable

namespace Daqifi.Core.Device.Diagnostics
{
    /// <summary>
    /// The <see cref="IDeviceDiagnostics"/> implementation, extracted from
    /// <see cref="DaqifiStreamingDevice"/> so the device delegates rather than hosts it.
    /// </summary>
    /// <remarks>
    /// Each method issues a single SCPI query/command as a text command (the protobuf consumer is
    /// paused for the exchange, same as the SD and LAN-chip queries) and hands the response to a
    /// tolerant parser. Unlike the SD operations these do not switch the SPI bus, so there is no
    /// PrepareSdInterface / settle delay; and they intentionally do not stop streaming, so callers
    /// can sample live counters — though parsing is most reliable when the device is not actively
    /// streaming.
    /// </remarks>
    internal sealed class DeviceDiagnosticsOperations : IDeviceDiagnostics
    {
        /// <summary>Time allowed for the first diagnostics response line. Generous because
        /// <c>SYSTem:LOG?</c> and the stats queries can emit dozens of lines.</summary>
        private const int DIAGNOSTICS_RESPONSE_TIMEOUT_MS = 2000;

        private readonly IDeviceOperationHost _host;

        internal DeviceDiagnosticsOperations(IDeviceOperationHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<SystemLogEntry>> GetSystemLogAsync(CancellationToken cancellationToken = default)
        {
            if (!_host.IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            cancellationToken.ThrowIfCancellationRequested();

            var lines = await _host.ExecuteTextCommandAsync(
                () => _host.Send(ScpiMessageProducer.GetSystemLog),
                responseTimeoutMs: DIAGNOSTICS_RESPONSE_TIMEOUT_MS,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var entries = SystemLogParser.Parse(lines);

            // The parser drops error/status lines, so an error-only response would
            // otherwise be indistinguishable from a genuinely empty log buffer.
            // Surface a command failure (e.g. unsupported on below-floor firmware)
            // rather than returning a misleading empty list.
            ThrowIfErrorOnlyResponse(entries.Count, lines, "read the system log");

            return entries;
        }

        /// <inheritdoc />
        public async Task ClearSystemLogAsync(CancellationToken cancellationToken = default)
        {
            if (!_host.IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            cancellationToken.ThrowIfCancellationRequested();

            var lines = await _host.ExecuteTextCommandAsync(
                () => _host.Send(ScpiMessageProducer.ClearSystemLog),
                responseTimeoutMs: DIAGNOSTICS_RESPONSE_TIMEOUT_MS,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // On success the device echoes a short ack ("Log cleared"); an error-only
            // response means the command failed and must not be swallowed.
            ThrowIfErrorOnlyResponse(0, lines, "clear the system log");
        }

        /// <inheritdoc />
        public async Task<LogLevelSetting> SetLogLevelAsync(string module, int level, CancellationToken cancellationToken = default)
        {
            // Build the command first so argument validation (ArgumentException /
            // ArgumentOutOfRangeException) surfaces the same way regardless of
            // connection state, matching SetAnalogOutput / SetDioDirection.
            var command = ScpiMessageProducer.SetLogLevel(module, level);

            if (!_host.IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            cancellationToken.ThrowIfCancellationRequested();

            var lines = await _host.ExecuteTextCommandAsync(
                () => _host.Send(command),
                responseTimeoutMs: DIAGNOSTICS_RESPONSE_TIMEOUT_MS,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (ScpiResponseClassifier.ContainsScpiError(lines))
            {
                throw new DeviceDiagnosticsException(
                    $"The device rejected log level {level} for module '{module}'.",
                    lines);
            }

            if (LogLevelParser.TryParseLines(lines, out var setting))
            {
                return setting;
            }

            throw new DeviceDiagnosticsException(
                $"Setting the log level for module '{module}' returned an unparseable response.",
                lines);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<string>> GetCommandHistoryAsync(CancellationToken cancellationToken = default)
        {
            if (!_host.IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            cancellationToken.ThrowIfCancellationRequested();

            var lines = await _host.ExecuteTextCommandAsync(
                () => _host.Send(ScpiMessageProducer.GetCommandHistory),
                responseTimeoutMs: DIAGNOSTICS_RESPONSE_TIMEOUT_MS,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var commands = CommandHistoryParser.Parse(lines);

            // An empty list is valid ("No command history"), but an error-only
            // response is a failure — distinguish the two. The "No command history"
            // marker is not an error line, so it never trips this check.
            ThrowIfErrorOnlyResponse(commands.Count, lines, "read the command history");

            return commands;
        }

        /// <inheritdoc />
        public async Task TestSystemLogAsync(CancellationToken cancellationToken = default)
        {
            if (!_host.IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            cancellationToken.ThrowIfCancellationRequested();

            var lines = await _host.ExecuteTextCommandAsync(
                () => _host.Send(ScpiMessageProducer.TestSystemLog),
                responseTimeoutMs: DIAGNOSTICS_RESPONSE_TIMEOUT_MS,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // On success the device echoes "Added test log messages"; an error-only
            // response means the command failed and must not be swallowed.
            ThrowIfErrorOnlyResponse(0, lines, "run the system-log self-test");
        }

        /// <inheritdoc />
        public async Task<int> GetSystemErrorCountAsync(CancellationToken cancellationToken = default)
        {
            if (!_host.IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            cancellationToken.ThrowIfCancellationRequested();

            var lines = await _host.ExecuteTextCommandAsync(
                () => _host.Send(ScpiMessageProducer.GetSystemErrorCount),
                responseTimeoutMs: DIAGNOSTICS_RESPONSE_TIMEOUT_MS,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (int.TryParse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
                {
                    return count;
                }
            }

            throw new DeviceDiagnosticsException(
                "The error-count query returned an unparseable response.",
                lines);
        }

        /// <inheritdoc />
        public async Task<StreamStats> GetStreamStatsAsync(CancellationToken cancellationToken = default)
        {
            if (!_host.IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            cancellationToken.ThrowIfCancellationRequested();

            var lines = await _host.ExecuteTextCommandAsync(
                () => _host.Send(ScpiMessageProducer.GetStreamStats),
                responseTimeoutMs: DIAGNOSTICS_RESPONSE_TIMEOUT_MS,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (StreamStatsParser.TryParse(lines, out var stats))
            {
                return stats;
            }

            throw new DeviceDiagnosticsException(
                "The streaming-stats query returned an unparseable response.",
                lines);
        }

        /// <inheritdoc />
        public async Task<MemoryDiagnostics> GetMemoryDiagnosticsAsync(CancellationToken cancellationToken = default)
        {
            if (!_host.IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            cancellationToken.ThrowIfCancellationRequested();

            var lines = await _host.ExecuteTextCommandAsync(
                () => _host.Send(ScpiMessageProducer.GetMemoryDiagnostics),
                responseTimeoutMs: DIAGNOSTICS_RESPONSE_TIMEOUT_MS,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (MemoryDiagnosticsParser.TryParse(lines, out var diagnostics))
            {
                return diagnostics;
            }

            throw new DeviceDiagnosticsException(
                "The memory-diagnostics query returned an unparseable response.",
                lines);
        }

        /// <summary>
        /// Throws a <see cref="DeviceDiagnosticsException"/> when a diagnostics command produced no
        /// usable result and the device's response consisted solely of SCPI error/status lines —
        /// i.e. the command failed (commonly an unsupported header on below-floor firmware) rather
        /// than legitimately returning nothing. A truly empty response (no lines) is treated as
        /// success so callers can distinguish "empty log" from "command failed".
        /// </summary>
        private static void ThrowIfErrorOnlyResponse(int parsedResultCount, IReadOnlyList<string> lines, string operation)
        {
            if (parsedResultCount == 0 && ScpiResponseClassifier.IsErrorOnlyResponse(lines))
            {
                throw new DeviceDiagnosticsException(
                    $"The device returned an error while attempting to {operation}.",
                    lines);
            }
        }
    }
}
