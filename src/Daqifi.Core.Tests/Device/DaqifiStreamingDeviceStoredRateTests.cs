using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device;
using Daqifi.Core.Tests.Device.Capabilities;
using Xunit;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Issue #960: on firmware v3.9.0 and newer, <see cref="DaqifiStreamingDevice.StreamingFrequency"/>
/// reports the rate the device has stored (<c>SYSTem:STReam:START?</c>) instead of Core's own
/// 100 Hz default — until a rate is chosen on the device object, which then stands.
/// </summary>
public class DaqifiStreamingDeviceStoredRateTests
{
    private const string ApiVersionCommand = "CONFigure:CAPabilities:APIVersion?";
    private const string DocumentCommand = "CONFigure:CAPabilities:JSON?";
    private const string StoredRateCommand = "SYSTem:STReam:START?";
    private const int CoreDefaultHz = 100;

    private static StoredRateDevice CreateDevice(string firmwareVersion = "3.9.0", string? storedRateReply = "1000")
    {
        var device = new StoredRateDevice();
        device.Metadata.FirmwareVersion = firmwareVersion;
        device.Metadata.PartNumber = "Nq1";
        device.Metadata.DeviceType = DeviceType.Nyquist1;
        device.Metadata.Capabilities = DeviceCapabilities.FromDeviceType(DeviceType.Nyquist1);
        device.Responses[ApiVersionCommand] = ["2"];
        device.Responses[DocumentCommand] = [CapabilityDocumentSamples.Nyquist1Firmware372];
        if (storedRateReply != null)
        {
            device.Responses[StoredRateCommand] = [storedRateReply];
        }

        device.Connect();
        return device;
    }

    [Fact]
    public void ANewDevice_StartsAtCoresDefault()
    {
        Assert.Equal(CoreDefaultHz, new StoredRateDevice().StreamingFrequency);
    }

    [Fact]
    public async Task OnFirmware390_TheDevicesStoredRateReplacesCoresDefault()
    {
        // The issue's case: another client (or an SD log) left the device at 1000 Hz. Core used to
        // report 100 Hz here, and the next start silently moved the device to it.
        var device = CreateDevice(storedRateReply: "1000");

        var document = await device.ReadCapabilityDocumentAsync();

        Assert.Equal(1000, device.StreamingFrequency);
        Assert.NotNull(document);
    }

    [Fact]
    public async Task OnFirmware390_TheRateQueryRidesTheCapabilityExchange_AfterTheDocument()
    {
        // No extra exchange on the connect path: one more line in the one already running. Last,
        // because the reply is found by its position after the document.
        var device = CreateDevice();

        await device.ReadCapabilityDocumentAsync();

        Assert.Equal(new[] { ApiVersionCommand, DocumentCommand, StoredRateCommand }, device.SentCommands);
        Assert.Equal(1, device.TextExchangeCount);
    }

    [Fact]
    public async Task OnFirmware390_TheBootDefaultIsAdoptedToo()
    {
        // A device that has never been started reports 1 Hz. That is its real rate.
        var device = CreateDevice(storedRateReply: "1");

        await device.ReadCapabilityDocumentAsync();

        Assert.Equal(1, device.StreamingFrequency);
    }

    [Theory]
    [InlineData("3.8.0")]
    [InlineData("3.9.0b1")]
    [InlineData("3.5.0")]
    public async Task BelowFirmware390_TheRateIsNotAskedAndCoresDefaultStands(string firmwareVersion)
    {
        var device = CreateDevice(firmwareVersion, storedRateReply: "1000");

        var document = await device.ReadCapabilityDocumentAsync();

        Assert.DoesNotContain(StoredRateCommand, device.SentCommands);
        Assert.Equal(new[] { ApiVersionCommand, DocumentCommand }, device.SentCommands);
        Assert.Equal(CoreDefaultHz, device.StreamingFrequency);
        Assert.NotNull(document);
    }

    [Theory]
    [InlineData("**ERROR: -113, \"Undefined header\"")]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("1.5")]
    [InlineData("0")]
    [InlineData("99999999999")]
    [InlineData("22001")] // above the Nq1's 22000 Hz ceiling
    [InlineData(null)]    // no answer at all
    public async Task AnUnusableStoredRateReply_KeepsTheCurrentRateAndStillAppliesTheDocument(string? reply)
    {
        // Must not fail the connect: the read runs inside InitializeAsync, and a device that
        // answers this one query badly is otherwise fine.
        var device = CreateDevice(storedRateReply: reply);

        var document = await device.ReadCapabilityDocumentAsync();

        Assert.Equal(CoreDefaultHz, device.StreamingFrequency);
        Assert.NotNull(document);
        Assert.Same(document, device.Metadata.CapabilityDocument);
    }

    [Fact]
    public async Task TheRateIsAdoptedEvenWhenTheDocumentIsNotTrusted()
    {
        // The two outcomes are independent: a document refused for its schema version says
        // nothing about the rate that followed it.
        var device = CreateDevice(storedRateReply: "750");
        device.Responses[ApiVersionCommand] = ["**ERROR: -113, \"Undefined header\""];

        var document = await device.ReadCapabilityDocumentAsync();

        Assert.Null(document);
        Assert.Equal(750, device.StreamingFrequency);
    }

    [Fact]
    public async Task ARateTheCallerSet_IsNotReplaced()
    {
        var device = CreateDevice(storedRateReply: "1000");
        device.StreamingFrequency = 250;

        await device.ReadCapabilityDocumentAsync();

        Assert.Equal(250, device.StreamingFrequency);
    }

    [Fact]
    public async Task ARateTheCallerSetToTheDefaultValue_IsStillTheCallersChoice()
    {
        // Chosen is about who set it, not about the number: a caller who asked for 100 Hz asked.
        var device = CreateDevice(storedRateReply: "1000");
        device.StreamingFrequency = CoreDefaultHz;

        await device.ReadCapabilityDocumentAsync();

        Assert.Equal(CoreDefaultHz, device.StreamingFrequency);
    }

    [Fact]
    public async Task OnceAStreamHasRunAtTheDefault_ALaterReadDoesNotReplaceIt()
    {
        // Starting a stream commits to its rate: it is what a reconnect resumes at. The device
        // here reports 1 Hz, as one that was power-cycled meanwhile would.
        var device = CreateDevice(storedRateReply: "1");
        device.StartStreaming();
        device.StopStreaming();

        await device.ReadCapabilityDocumentAsync();

        Assert.Equal(CoreDefaultHz, device.StreamingFrequency);
        Assert.Contains(ScpiMessageProducer.StartStreaming(CoreDefaultHz).Data, device.SentCommands);
    }

    [Fact]
    public async Task AnAdoptedRateIsNotAChoice_SoALaterReadBringsItUpToDate()
    {
        var device = CreateDevice(storedRateReply: "500");
        await device.ReadCapabilityDocumentAsync();
        Assert.Equal(500, device.StreamingFrequency);

        device.Responses[StoredRateCommand] = ["750"];
        await device.ReadCapabilityDocumentAsync();

        Assert.Equal(750, device.StreamingFrequency);
    }

    [Fact]
    public async Task AnAdoptedRateIsWhatTheNextStartSends()
    {
        // The point of reading it: starting without choosing a rate keeps the device where it was.
        var device = CreateDevice(storedRateReply: "1000");
        await device.ReadCapabilityDocumentAsync();

        device.StartStreaming();

        Assert.Equal(ScpiMessageProducer.StartStreaming(1000).Data, device.SentCommands.Last());
    }

    [Fact]
    public async Task ThePlainDeviceAsksToo_AndIgnoresTheAnswer()
    {
        // The query lives in the base capability read; only the streaming device keeps a rate.
        var device = new PlainDevice();
        device.Metadata.FirmwareVersion = "3.9.0";
        device.Metadata.PartNumber = "Nq1";
        device.Metadata.DeviceType = DeviceType.Nyquist1;
        device.Metadata.Capabilities = DeviceCapabilities.FromDeviceType(DeviceType.Nyquist1);
        device.Responses[ApiVersionCommand] = ["2"];
        device.Responses[DocumentCommand] = [CapabilityDocumentSamples.Nyquist1Firmware372];
        device.Responses[StoredRateCommand] = ["1000"];
        device.Connect();

        var document = await device.ReadCapabilityDocumentAsync();

        Assert.NotNull(document);
        Assert.Equal(new[] { ApiVersionCommand, DocumentCommand, StoredRateCommand }, device.SentCommands);
    }

    /// <summary>
    /// Answers each text command from a script, the way the device answers a batched exchange:
    /// every reply in the order the commands went out.
    /// </summary>
    private sealed class StoredRateDevice : DaqifiStreamingDevice
    {
        public StoredRateDevice() : base("BenchNq1") { }

        public List<string> SentCommands { get; } = new();

        public Dictionary<string, string[]> Responses { get; } = new();

        public int TextExchangeCount { get; private set; }

        public override void Send<T>(IOutboundMessage<T> message)
        {
            if (message is IOutboundMessage<string> stringMessage)
            {
                SentCommands.Add(stringMessage.Data);
            }
        }

        protected override async Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Func<CancellationToken, Task> setupActionAsync,
            int responseTimeoutMs = 1000,
            int completionTimeoutMs = 250,
            CancellationToken cancellationToken = default)
        {
            TextExchangeCount++;
            var before = SentCommands.Count;
            await setupActionAsync(cancellationToken).ConfigureAwait(false);
            return ScriptedReplies.Since(SentCommands, before, Responses);
        }
    }

    /// <summary>The same script on the base device, which keeps no streaming rate.</summary>
    private sealed class PlainDevice : DaqifiDevice
    {
        public PlainDevice() : base("BenchNq1") { }

        public List<string> SentCommands { get; } = new();

        public Dictionary<string, string[]> Responses { get; } = new();

        public override void Send<T>(IOutboundMessage<T> message)
        {
            if (message is IOutboundMessage<string> stringMessage)
            {
                SentCommands.Add(stringMessage.Data);
            }
        }

        protected override async Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Func<CancellationToken, Task> setupActionAsync,
            int responseTimeoutMs = 1000,
            int completionTimeoutMs = 250,
            CancellationToken cancellationToken = default)
        {
            var before = SentCommands.Count;
            await setupActionAsync(cancellationToken).ConfigureAwait(false);
            return ScriptedReplies.Since(SentCommands, before, Responses);
        }
    }

    private static class ScriptedReplies
    {
        public static IReadOnlyList<string> Since(
            List<string> sent,
            int firstCommandIndex,
            Dictionary<string, string[]> responses) =>
            sent
                .Skip(firstCommandIndex)
                .SelectMany(command => responses.TryGetValue(command, out var reply) ? reply : Array.Empty<string>())
                .ToList();
    }
}
