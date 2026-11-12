using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device.Internal;

namespace Daqifi.Core.Device.Network;

/// <summary>
/// The <see cref="IWifiLinkStatusProvider"/> implementation behind <see cref="DaqifiStreamingDevice"/>:
/// the firmware v3.9.0 live WiFi queries, <c>LAN:CONnected?</c> and <c>LAN:NETType? 1</c>.
/// </summary>
internal sealed class WifiLinkStatusOperations : IWifiLinkStatusProvider
{
    /// <summary>
    /// SCPI execution error. From <c>LAN:CONnected?</c> it has exactly one source in firmware
    /// v3.9.0 (<c>SCPI_LANConnectedGet</c>): WiFi is disabled or the device is in standby.
    /// </summary>
    private const int ScpiErrorExecution = -200;

    /// <summary>
    /// SCPI "Undefined header": the firmware does not know the command at all, which means it
    /// predates v3.9.0 whatever version it reported.
    /// </summary>
    private const int ScpiErrorUndefinedHeader = -113;

    private readonly IDeviceOperationHost _host;

    internal WifiLinkStatusOperations(IDeviceOperationHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <inheritdoc />
    public async Task<WifiLinkState> GetWifiLinkStateAsync(CancellationToken cancellationToken = default)
    {
        var message = ScpiMessageProducer.GetLanConnected;
        var lines = await QueryAsync(message, cancellationToken).ConfigureAwait(false);
        var command = message.Data.Trim();

        var errorLine = ScpiResponseClassifier.GetLastScpiErrorLine(lines);
        if (errorLine != null)
        {
            // The documented "WiFi not ready" answer for a disabled or powered-down radio. It is a
            // state the caller asked about, not a failed query, so it comes back as a value.
            if (ScpiResponseClassifier.TryExtractErrorCode(errorLine, out var code) && code == ScpiErrorExecution)
            {
                await RemoveOwnQueuedRefusalAsync(cancellationToken).ConfigureAwait(false);
                return WifiLinkState.Unavailable;
            }

            throw Refusal(command, errorLine);
        }

        var reply = FirstReplyLine(lines);
        if (reply == null)
        {
            throw new DeviceCommandFailedException(command);
        }

        // Compared whole, never by prefix: INIT and INITFAULT share one and mean opposite things.
        return reply.ToUpperInvariant() switch
        {
            "CONNECTED" => WifiLinkState.Connected,
            "APIDLE" => WifiLinkState.AccessPointIdle,
            "NOLINK" => WifiLinkState.NoLink,
            "INITFAULT" => WifiLinkState.InitializationFault,
            "INIT" => WifiLinkState.Initializing,
            _ => throw new DeviceCommandFailedException(command, reply)
        };
    }

    /// <inheritdoc />
    public async Task<WifiMode?> GetActiveWifiModeAsync(CancellationToken cancellationToken = default)
    {
        var message = ScpiMessageProducer.GetLanActiveWifiMode;
        var lines = await QueryAsync(message, cancellationToken).ConfigureAwait(false);
        var command = message.Data.Trim();

        var errorLine = ScpiResponseClassifier.GetLastScpiErrorLine(lines);
        if (errorLine != null)
        {
            throw Refusal(command, errorLine);
        }

        var reply = FirstReplyLine(lines);
        if (reply == null)
        {
            throw new DeviceCommandFailedException(command);
        }

        if (!int.TryParse(reply, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mode))
        {
            throw new DeviceCommandFailedException(command, reply);
        }

        return mode switch
        {
            0 => null,
            (int)WifiMode.ExistingNetwork => WifiMode.ExistingNetwork,
            (int)WifiMode.SelfHosted => WifiMode.SelfHosted,
            _ => throw new DeviceCommandFailedException(command, reply)
        };
    }

    /// <summary>
    /// Runs one gated query exchange. Connectivity is checked before the feature gate, because a
    /// disconnected device has no firmware version to gate on and would otherwise be reported as
    /// unsupported rather than disconnected.
    /// </summary>
    private async Task<IReadOnlyList<string>> QueryAsync(
        IOutboundMessage<string> message,
        CancellationToken cancellationToken)
    {
        _host.EnsureConnected(cancellationToken);
        _host.EnsureSupported(DeviceFeature.WifiLinkStatus);

        return await _host.ExecuteTextCommandAsync(
            () => _host.Send(message),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes the <c>-200</c> that <c>LAN:CONnected?</c> just queued, but only when it is provably
    /// the only entry in the device's error queue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Core reports that <c>-200</c> as an answer, not a failure, so leaving it queued would let
    /// every poll of a device in standby add a stale entry (seen on the bench: three polls, three
    /// entries). A later reader that does not drain first — the stream-start confirmation — would
    /// take one of them for its own command's refusal.
    /// </para>
    /// <para>
    /// <c>SYSTem:ERRor?</c> pops the <em>oldest</em> entry, though, so popping blindly would
    /// consume an earlier, unrelated error and leave this one behind. The non-destructive count
    /// settles ownership: a queue of exactly one entry, read right after the device volunteered
    /// this query's <c>-200</c>, holds only that <c>-200</c>. With anything older ahead of it, the
    /// queue is left untouched — the older entries belong to whoever caused them.
    /// </para>
    /// <para>
    /// The count only proves ownership if nothing else touches the queue between the query, the
    /// count and the pop, which are separate exchanges. <see cref="DaqifiStreamingDevice"/> runs
    /// the whole link-state read under its exclusive operation lock for that reason.
    /// </para>
    /// </remarks>
    private async Task RemoveOwnQueuedRefusalAsync(CancellationToken cancellationToken)
    {
        var lines = await _host.ExecuteTextCommandAsync(
            () => _host.Send(ScpiMessageProducer.GetSystemErrorCount),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (LineParsing.TryParseFirst<int>(lines, TryParseCount, out var count) && count == 1)
        {
            await _host.DrainErrorQueueAsync(maxIterations: 1, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool TryParseCount(string? line, out int count)
    {
        count = 0;
        return !string.IsNullOrWhiteSpace(line)
               && int.TryParse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out count);
    }

    /// <summary>
    /// Translates a SCPI error reply into the exception for it: an undefined header is the
    /// firmware-gating backstop (ADR 0001), anything else a refusal of the query.
    /// </summary>
    private Exception Refusal(string command, string errorLine)
    {
        if (!ScpiResponseClassifier.TryExtractErrorCode(errorLine, out var code) || code == 0)
        {
            return new DeviceCommandFailedException(command, errorLine);
        }

        return code == ScpiErrorUndefinedHeader
            ? _host.CreateFeatureNotSupportedException(DeviceFeature.WifiLinkStatus)
            : new DeviceCommandFailedException(command, code, errorLine);
    }

    /// <summary>
    /// The first line of the reply that carries content, trimmed; <c>null</c> when there is none.
    /// </summary>
    private static string? FirstReplyLine(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            if (!string.IsNullOrWhiteSpace(line) && !ScpiResponseClassifier.IsErrorResponseLine(line))
            {
                return line.Trim();
            }
        }

        return null;
    }
}
