using System;
using System.Collections.Generic;
using System.Linq;
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
    /// <para>
    /// Each method issues a single SCPI query/command as a text command (the protobuf consumer is
    /// paused for the exchange, same as the SD and LAN-chip queries) and hands the response to a
    /// tolerant parser. Unlike the SD operations these do not switch the SPI bus, so there is no
    /// PrepareSdInterface / settle delay; and they deliberately do not stop streaming, so they can
    /// be issued mid-capture.
    /// </para>
    /// <para>
    /// Not stopping the stream has a cost the tolerant parsers used to hide. Pausing the protobuf
    /// consumer does not tell the firmware to stop, so protobuf frame bytes keep arriving and land
    /// on the front of the reply's first line; the parser drops what it cannot recognise, and the
    /// caller gets a plausible-looking result with a counter silently missing (issue #537). The
    /// four queries whose entire value is machine-parsed numbers therefore check for that evidence
    /// and throw <see cref="DeviceDiagnosticsCorruptedResponseException"/> instead of reporting a
    /// partial answer as a whole one.
    /// </para>
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
            _host.EnsureConnected(cancellationToken);

            // keepBlankLines is what makes "the log is empty" distinguishable from
            // "the device never answered" (issue #543). Both used to arrive here as an
            // empty list, so a silent link, a wedged text exchange, or an unsupported
            // header all reported as "your log is empty" -- the least useful answer a
            // DIAGNOSTICS call can give, because it is indistinguishable from health.
            //
            // The firmware ends every SYSTem:LOG? dump with a blank line, empty or not
            // (measured on a bench Nq1 running 3.7.2 with a raw pyserial probe, three
            // trials of three: an empty log answers b'\r\n' in 6 ms; a populated one
            // answers its entries followed by the same trailing b'\r\n'). So ANY line
            // reaching us -- blank or not -- means the device answered.
            var raw = await _host.ExecuteTextCommandAsync(
                () => _host.Send(ScpiMessageProducer.GetSystemLog),
                responseTimeoutMs: DIAGNOSTICS_RESPONSE_TIMEOUT_MS,
                cancellationToken: cancellationToken,
                keepBlankLines: true).ConfigureAwait(false);

            if (raw.Count == 0)
            {
                throw new DeviceDiagnosticsException(
                    "The device did not answer SYSTem:LOG? - not even the blank line that "
                    + "terminates every log dump. This is a silent or unresponsive device, "
                    + "not an empty log.",
                    raw);
            }

            // Everything downstream expects content lines, exactly as before.
            var lines = raw.Where(line => line.Length > 0).ToList();

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
            _host.EnsureConnected(cancellationToken);

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

            _host.EnsureConnected(cancellationToken);

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

            // After the rejection check: a device that answered "no" gave a real answer, and that
            // diagnosis is more useful than "your reply was mangled". Before the parse, because a
            // mangled echo is exactly why the parse below is about to fail.
            ThrowIfCorruptedByStreamData(lines, $"the log-level command for module '{module}'");

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
            _host.EnsureConnected(cancellationToken);

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
            _host.EnsureConnected(cancellationToken);

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
            _host.EnsureConnected(cancellationToken);

            var lines = await _host.ExecuteTextCommandAsync(
                () => _host.Send(ScpiMessageProducer.GetSystemErrorCount),
                responseTimeoutMs: DIAGNOSTICS_RESPONSE_TIMEOUT_MS,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            ThrowIfCorruptedByStreamData(lines, "the error-count query");

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
            _host.EnsureConnected(cancellationToken);

            var lines = await _host.ExecuteTextCommandAsync(
                () => _host.Send(ScpiMessageProducer.GetStreamStats),
                responseTimeoutMs: DIAGNOSTICS_RESPONSE_TIMEOUT_MS,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            ThrowIfCorruptedByStreamData(lines, "the streaming-stats query");

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
            _host.EnsureConnected(cancellationToken);

            var lines = await _host.ExecuteTextCommandAsync(
                () => _host.Send(ScpiMessageProducer.GetMemoryDiagnostics),
                responseTimeoutMs: DIAGNOSTICS_RESPONSE_TIMEOUT_MS,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            ThrowIfCorruptedByStreamData(lines, "the memory-diagnostics query");

            if (MemoryDiagnosticsParser.TryParse(lines, out var diagnostics))
            {
                return diagnostics;
            }

            throw new DeviceDiagnosticsException(
                "The memory-diagnostics query returned an unparseable response.",
                lines);
        }

        /// <summary>
        /// Throws a <see cref="DeviceDiagnosticsCorruptedResponseException"/> when the device's reply
        /// arrived with non-text bytes welded into it — the signature of protobuf frames from a
        /// stream the firmware never stopped emitting (issue #537). Without this the tolerant
        /// parsers below drop the mangled line and return the rest, which reads as a complete
        /// answer with a counter missing.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Applied only to the four queries whose whole value is machine-parsed numbers, where a
        /// dropped line is invisible data loss. Deliberately NOT applied elsewhere, and both
        /// exclusions matter:
        /// </para>
        /// <para>
        /// <see cref="GetSystemLogAsync"/> and <see cref="GetCommandHistoryAsync"/> return
        /// firmware-authored text, one entry per line. Losing one entry out of ten is visible in the
        /// result, and the log read is destructive on the device — the buffer is cleared by the very
        /// query that read it — so throwing the surviving entries away would destroy more than it
        /// protects.
        /// </para>
        /// <para>
        /// <see cref="ClearSystemLogAsync"/> and <see cref="TestSystemLogAsync"/> answer with a short
        /// ack, not a result. The command still ran; failing them over a mangled ack would report a
        /// failure that did not happen.
        /// </para>
        /// <para>
        /// Not retried either: the interference lasts as long as the stream does, so a second
        /// attempt meets the same frames. Stopping the stream is the caller's call to make, not
        /// Core's.
        /// </para>
        /// </remarks>
        private static void ThrowIfCorruptedByStreamData(IReadOnlyList<string> lines, string operation)
        {
            if (!ScpiResponseClassifier.ContainsBinaryCorruptedLine(lines))
            {
                return;
            }

            throw new DeviceDiagnosticsCorruptedResponseException(
                $"The reply to {operation} arrived with binary streaming data welded into it, so the "
                + "values it carries are incomplete. This happens when the query runs while the device "
                + "is streaming: pausing Core's protobuf reader does not stop the firmware, so its "
                + "frames land on the front of the reply. Stop streaming and query again.",
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
