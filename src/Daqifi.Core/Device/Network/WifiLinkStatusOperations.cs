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
                // The device also queued that -200. Since Core reports it as an answer rather
                // than a failure, pop one entry so the query leaves the error queue as deep as it
                // found it. Otherwise every poll of a device in standby adds a stale -200 (seen on
                // the bench: three polls, three entries), and a later reader of the queue that
                // does not drain first — the stream-start confirmation — would take one of them
                // for its own command's refusal.
                await _host.DrainErrorQueueAsync(maxIterations: 1, cancellationToken).ConfigureAwait(false);
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
