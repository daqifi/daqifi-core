using System.Collections.Concurrent;
using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Capabilities;
using Daqifi.Core.Device.SdCard;

namespace Daqifi.Mcp.Tests;

/// <summary>
/// A <see cref="DaqifiStreamingDevice"/> that behaves like an attached Nyquist without a wire:
/// channels are populated from a status message, outbound SCPI is captured instead of written,
/// and the capability document is re-issued on request the way real firmware re-issues it.
/// </summary>
/// <remarks>
/// <para>
/// This is what lets the MCP tool surface be tested past <c>Require</c> (#465). Everything the
/// tools actually decide — which channel numbers exist, what order the digital/PWM commands go
/// out in, whether a live sample rate still fits under the device's cap — is downstream of a
/// connected device, so without a double the suite could only ever assert the "nothing is
/// connected" refusals.
/// </para>
/// <para>
/// It is deliberately a real <see cref="DaqifiStreamingDevice"/> rather than a hand-written
/// <c>IStreamingDevice</c>: the bitmask arithmetic, the PWM-capability mask, the channel
/// snapshotting and <see cref="SampleRateCap"/> are Core's own and are exactly the parts a fake
/// would get to re-specify (and therefore never catch).
/// </para>
/// </remarks>
internal sealed class FakeStreamingDevice : DaqifiStreamingDevice, ISdCardOperations
{
    /// <summary>Board ceiling, high enough that the per-configuration cap is what binds.</summary>
    internal const int HardwareMaximumRateHz = 30_000;

    private readonly ConcurrentQueue<string> _sent = new();

    private FakeStreamingDevice(string name) : base(name)
    {
    }

    /// <summary>Every SCPI command the tools caused, in the order they were issued.</summary>
    internal IReadOnlyList<string> Sent => _sent.ToList();

    /// <summary>How many times a tool asked the device to re-read its capability document.</summary>
    internal int CapabilityReads { get; private set; }

    /// <summary>
    /// What the device answers as its cap for the analog channels enabled at the moment it is
    /// asked. Default mirrors an NQ1: a fixed per-tick budget shared by the enabled inputs.
    /// </summary>
    internal Func<int, int> CapForEnabledAnalogCount { get; set; } =
        enabled => enabled == 0 ? 0 : 20_000 / enabled;

    internal void ClearSent()
    {
        while (_sent.TryDequeue(out _))
        {
        }
    }

    /// <summary>
    /// The analog outputs this device's capability document describes, in document order. Empty by
    /// default, which is what every board but a Nyquist 3 reports.
    /// </summary>
    internal List<CapabilityChannel> AnalogOutputDescriptors { get; } = new();

    /// <summary>
    /// Canned answers for <c>SOURce:VOLTage:LEVel?</c>, keyed by channel number — what the firmware
    /// says it is holding. A channel with no entry answers nothing, which is how a device that
    /// ignores the query behaves.
    /// </summary>
    internal Dictionary<int, string> AnalogOutputReadbacks { get; } = new();

    /// <summary>
    /// Builds a connected device with <paramref name="analogChannels"/> analog inputs and
    /// <paramref name="digitalChannels"/> digital channels, and its capability document already
    /// read once — the state a device is in the moment <c>connect_device</c> returns.
    /// </summary>
    /// <param name="analogOutputs">
    /// Analog-output (DAC) channels for the capability document to describe, each 0-10 V at 12 bits.
    /// Defaults to none, matching the Nyquist 1 the rest of these tests model; the board is left
    /// <see cref="DeviceType.Unknown"/> either way, so the analog-output feature gate lets a write
    /// through (an unidentified board is not a refusal — see <c>DaqifiDevice.Supports</c>).
    /// </param>
    internal static FakeStreamingDevice CreateConnected(
        int analogChannels = 4, int digitalChannels = 8, int analogOutputs = 0)
    {
        var device = new FakeStreamingDevice("Fake Nq1");
        device.PopulateChannelsFromStatus(new DaqifiOutMessage
        {
            AnalogInPortNum = (uint)analogChannels,
            AnalogInRes = 65535,
            DigitalPortNum = (uint)digitalChannels,
        });

        for (var id = 0; id < analogOutputs; id++)
        {
            device.AnalogOutputDescriptors.Add(new CapabilityChannel
            {
                Id = id,
                Kind = CapabilityChannelKind.AnalogOutput,
                SignalType = "voltage",
                Unit = "V",
                ResolutionBits = 12,
                RangeMinimum = 0,
                RangeMaximum = 10,
            });
        }

        device.Metadata.Capabilities.MaxSamplingRate = HardwareMaximumRateHz;
        device.Connect();
        device.ApplyCapabilityDocumentForCurrentChannels();
        device.ClearSent();
        return device;
    }

    public override void Send<T>(IOutboundMessage<T> message)
    {
        if (message is IOutboundMessage<string> text)
        {
            _sent.Enqueue(text.Data);
        }
    }

    /// <summary>
    /// Re-issues the capability document for the channel set enabled right now, which is what a
    /// real device does: <c>CurrentMaximumRateHz</c> describes the selection that was live when
    /// the document was read, so the number only moves when the document is re-read.
    /// </summary>
    public override Task<CapabilityDocument?> ReadCapabilityDocumentAsync(
        CancellationToken cancellationToken = default)
    {
        CapabilityReads++;
        return Task.FromResult<CapabilityDocument?>(ApplyCapabilityDocumentForCurrentChannels());
    }

    /// <summary>
    /// Answers a text exchange the way the firmware would: the query still goes into
    /// <see cref="Sent"/> (so the tests can assert what reached the wire), and the reply comes from
    /// <see cref="AnalogOutputReadbacks"/>. A channel with no canned answer replies with nothing,
    /// which is a device that ignored the query rather than one that answered zero.
    /// </summary>
    protected override Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
        Action setupAction,
        int responseTimeoutMs = 1000,
        int completionTimeoutMs = 250,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? prepareAsync = null,
        Func<Task>? finalizeAsync = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        setupAction();

        var query = Sent.LastOrDefault() ?? string.Empty;
        const string readbackPrefix = "SOURce:VOLTage:LEVel? ";
        if (query.StartsWith(readbackPrefix, StringComparison.Ordinal)
            && int.TryParse(query[readbackPrefix.Length..], out var channel)
            && AnalogOutputReadbacks.TryGetValue(channel, out var answer))
        {
            return Task.FromResult<IReadOnlyList<string>>(new[] { answer });
        }

        return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    // --------------------------------------------------------------------- SD card
    //
    // Re-implemented rather than overridden: Core's SD members are non-virtual forwards to an
    // internal collaborator, so an explicit re-implementation of the interface is the only way to
    // give the tool layer a card to talk to. Only the members the tools call are replaced; the
    // rest keep their inherited behavior.

    /// <summary>Files the card reports; empty by default, which the tools must read as an empty card.</summary>
    internal List<SdCardFileInfo> SdFiles { get; } = new();

    /// <summary>Storage figures the card reports.</summary>
    internal SdCardStorageInfo SdStorage { get; set; } = new(FreeBytes: 750, TotalBytes: 1000);

    /// <summary>When set, every card query fails with it — how a busy or collapsed card behaves.</summary>
    internal Exception? SdFailure { get; set; }

    /// <summary>The logging session the last <c>start_sd_logging</c> opened, if any.</summary>
    internal SdCardLoggingSession? StartedSession { get; private set; }

    private bool _logging;

    bool ISdCardOperations.IsLoggingToSdCard => _logging;

    Task<IReadOnlyList<SdCardFileInfo>> ISdCardOperations.GetSdCardFilesAsync(CancellationToken cancellationToken) =>
        SdFailure is { } failure
            ? Task.FromException<IReadOnlyList<SdCardFileInfo>>(failure)
            : Task.FromResult<IReadOnlyList<SdCardFileInfo>>(SdFiles.ToList());

    Task<SdCardStorageInfo> ISdCardOperations.GetSdCardStorageAsync(CancellationToken cancellationToken) =>
        SdFailure is { } failure
            ? Task.FromException<SdCardStorageInfo>(failure)
            : Task.FromResult(SdStorage);

    Task<SdCardLoggingSession> ISdCardOperations.StartSdCardLoggingSessionAsync(
        string? fileName, string? channelMask, SdCardLogFormat format, CancellationToken cancellationToken)
    {
        if (SdFailure is { } failure)
        {
            return Task.FromException<SdCardLoggingSession>(failure);
        }

        _logging = true;
        StartedSession = new SdCardLoggingSession(
            string.IsNullOrWhiteSpace(fileName) ? "log_20260813_000000" : fileName!, format);
        return Task.FromResult(StartedSession);
    }

    Task ISdCardOperations.StopSdCardLoggingAsync(CancellationToken cancellationToken)
    {
        _logging = false;
        return Task.CompletedTask;
    }

    private CapabilityDocument ApplyCapabilityDocumentForCurrentChannels()
    {
        var enabledAnalog = GetChannelsSnapshot()
            .Count(c => c.Type == ChannelType.Analog && c.Direction == ChannelDirection.Input && c.IsEnabled);

        var document = new CapabilityDocument
        {
            SchemaVersion = 1,
            Channels = AnalogOutputDescriptors.ToList(),
            Streaming = new CapabilityStreaming
            {
                MaximumSampleRateHz = HardwareMaximumRateHz,
                CurrentMaximumRateHz = CapForEnabledAnalogCount(enabledAnalog),
            },
        };

        Metadata.ApplyCapabilityDocument(document);

        // Both halves of what the real ReadCapabilityDocumentAsync does: the document is the only
        // place a device describes its DACs, so applying it without this sync would leave the
        // outputs unmodelled and the analog-output tools with nothing to find.
        SyncAnalogOutputChannelsFromCapabilities();
        return document;
    }
}

/// <summary>
/// Convenience for the contract tests: an agent with one device already filed under
/// <c>serial:FAKE</c>, so a test can go straight to the tool call it is about.
/// </summary>
/// <remarks>
/// Nothing here needs disposing. The double is connected without a transport, so it owns no OS
/// handle and starts no reader; the registry that takes ownership of it is collected with the
/// agent. Tests that are <i>about</i> teardown (disconnect, shutdown) drive it through the tools,
/// which is the behavior they are checking rather than cleanup.
/// </remarks>
internal static class AgentHarness
{
    internal const string DeviceId = "serial:FAKE";

    internal static (DaqifiAgent Agent, FakeStreamingDevice Device) WithConnectedDevice(
        bool readOnly = false,
        int? maxSampleRateHz = null,
        int analogChannels = 4,
        int digitalChannels = 8,
        int analogOutputs = 0)
    {
        var agent = new DaqifiAgent(new ServerOptions
        {
            ReadOnly = readOnly,
            MaxSampleRateHz = maxSampleRateHz,
        });

        var device = FakeStreamingDevice.CreateConnected(analogChannels, digitalChannels, analogOutputs);
        agent.RegisterConnectedDevice(DeviceId, device);
        return (agent, device);
    }
}
