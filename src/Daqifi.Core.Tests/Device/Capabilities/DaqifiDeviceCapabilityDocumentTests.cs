using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Capabilities;

namespace Daqifi.Core.Tests.Device.Capabilities;

/// <summary>
/// Tests for <see cref="DaqifiDevice.ReadCapabilityDocumentAsync"/> — the gating on
/// <see cref="DeviceFeature.CapabilityDocument"/> and on the reported schema version, and the
/// requirement that a device which cannot answer is left exactly as it was.
/// </summary>
public class DaqifiDeviceCapabilityDocumentTests
{
    private const string ApiVersionCommand = "CONFigure:CAPabilities:APIVersion?";
    private const string DocumentCommand = "CONFigure:CAPabilities:JSON?";

    private static TestableCapabilityDevice CreateSupportedDevice(string firmwareVersion = "3.7.2")
    {
        var device = new TestableCapabilityDevice("BenchNq1");
        device.Metadata.FirmwareVersion = firmwareVersion;
        device.Metadata.PartNumber = "Nq1";
        device.Metadata.DeviceType = DeviceType.Nyquist1;
        device.Metadata.Capabilities = DeviceCapabilities.FromDeviceType(DeviceType.Nyquist1);
        device.Connect();
        return device;
    }

    [Fact]
    public async Task ReadCapabilityDocumentAsync_WhenDisconnected_Throws()
    {
        var device = new TestableCapabilityDevice("BenchNq1");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => device.ReadCapabilityDocumentAsync());
    }

    [Fact]
    public async Task ReadCapabilityDocumentAsync_OnSupportedDevice_AppliesTheDocument()
    {
        var device = CreateSupportedDevice();
        device.Responses[ApiVersionCommand] = ["2"];
        device.Responses[DocumentCommand] = [CapabilityDocumentSamples.Nyquist1Firmware372];

        var document = await device.ReadCapabilityDocumentAsync();

        Assert.NotNull(document);
        Assert.Equal(2, document!.SchemaVersion);
        Assert.Same(document, device.Metadata.CapabilityDocument);
        Assert.Equal(16, device.Metadata.Capabilities.AnalogInputChannels);
        Assert.Equal(16, device.Metadata.Capabilities.DigitalChannels);
        Assert.Equal(22000, device.Metadata.Capabilities.MaxSamplingRate);
        Assert.Equal(new[] { ApiVersionCommand, DocumentCommand }, device.SentCommands);
    }

    [Fact]
    public async Task ReadCapabilityDocumentAsync_BelowFirmwareFloor_SendsNothingAndChangesNothing()
    {
        // The definition-of-done requirement: a device that cannot answer is unaffected. It must
        // not even be asked — the query does not exist on its firmware.
        var device = CreateSupportedDevice(firmwareVersion: "3.4.6b1");
        device.Responses[ApiVersionCommand] = ["2"];
        device.Responses[DocumentCommand] = [CapabilityDocumentSamples.Nyquist1Firmware372];

        var document = await device.ReadCapabilityDocumentAsync();

        Assert.Null(document);
        Assert.Null(device.Metadata.CapabilityDocument);
        Assert.Empty(device.SentCommands);
        Assert.Equal(1000, device.Metadata.Capabilities.MaxSamplingRate);
        Assert.Equal(0, device.Metadata.Capabilities.AnalogInputChannels);
    }

    [Fact]
    public async Task ReadCapabilityDocumentAsync_WithNoReportedFirmwareVersion_IsSkipped()
    {
        // The version axis fails closed (ADR 0001): an unknown version is not permission.
        var device = CreateSupportedDevice(firmwareVersion: string.Empty);

        Assert.Null(await device.ReadCapabilityDocumentAsync());
        Assert.Empty(device.SentCommands);
    }

    [Fact]
    public async Task ReadCapabilityDocumentAsync_WhenApiVersionQueryFails_DoesNotTrustTheDocument()
    {
        // The document arrives in the same exchange, so it is present and parseable — and still
        // must not be applied, because nothing confirmed the schema it was written to.
        var device = CreateSupportedDevice();
        device.Responses[ApiVersionCommand] = ["**ERROR: -113, \"Undefined header\""];
        device.Responses[DocumentCommand] = [CapabilityDocumentSamples.Nyquist1Firmware372];

        Assert.Null(await device.ReadCapabilityDocumentAsync());
        Assert.Null(device.Metadata.CapabilityDocument);
        Assert.Equal(1000, device.Metadata.Capabilities.MaxSamplingRate);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task ReadCapabilityDocumentAsync_ApiVersionBelowMinimum_DoesNotTrustTheDocument(
        string reportedVersion)
    {
        var device = CreateSupportedDevice();
        device.Responses[ApiVersionCommand] = [reportedVersion];
        device.Responses[DocumentCommand] = [CapabilityDocumentSamples.Nyquist1Firmware372];

        Assert.Null(await device.ReadCapabilityDocumentAsync());
        Assert.Null(device.Metadata.CapabilityDocument);
        Assert.Equal(1000, device.Metadata.Capabilities.MaxSamplingRate);
    }

    [Fact]
    public async Task ReadCapabilityDocumentAsync_ApiVersionAboveMaximum_DoesNotTrustTheDocument()
    {
        // A bumped schema version means a breaking change, so the fields this parser reads may no
        // longer mean what it assumes. Falling back to the board table beats a plausible-but-wrong
        // number.
        var device = CreateSupportedDevice();
        device.Responses[ApiVersionCommand] =
            [(DaqifiDevice.MaximumCapabilityDocumentApiVersion + 1).ToString()];
        device.Responses[DocumentCommand] = [CapabilityDocumentSamples.Nyquist1Firmware372];

        Assert.Null(await device.ReadCapabilityDocumentAsync());
        Assert.Null(device.Metadata.CapabilityDocument);
        Assert.Equal(1000, device.Metadata.Capabilities.MaxSamplingRate);
    }

    [Fact]
    public async Task ReadCapabilityDocumentAsync_WhenDocumentDoesNotParse_LeavesCapabilitiesAlone()
    {
        var device = CreateSupportedDevice();
        device.Responses[ApiVersionCommand] = ["2"];
        device.Responses[DocumentCommand] = ["{\"schema_version\":2, truncated"];

        Assert.Null(await device.ReadCapabilityDocumentAsync());
        Assert.Null(device.Metadata.CapabilityDocument);
        Assert.Equal(1000, device.Metadata.Capabilities.MaxSamplingRate);
        Assert.True(device.Metadata.Capabilities.HasSdCard);
    }

    [Fact]
    public async Task ReadCapabilityDocumentAsync_SurvivesALaterStatusMessage()
    {
        // Status messages repeat the part number. Before this change each one rebuilt Capabilities
        // from the board table, which would have discarded the document mid-session.
        var device = CreateSupportedDevice();
        device.Responses[ApiVersionCommand] = ["2"];
        device.Responses[DocumentCommand] = [CapabilityDocumentSamples.Nyquist1Firmware372];

        await device.ReadCapabilityDocumentAsync();
        device.Metadata.UpdateFromProtobuf(new DaqifiOutMessage { DevicePn = "Nq1" });

        Assert.Equal(22000, device.Metadata.Capabilities.MaxSamplingRate);
        Assert.Equal(16, device.Metadata.Capabilities.AnalogInputChannels);
    }

    [Fact]
    public async Task ReadCapabilityDocumentAsync_DoesNotChangeWhatSupportsReports()
    {
        // No new refusals on the existing Supports() seam: overlaying the document must leave
        // every feature answering exactly as it did from the board table alone.
        var device = CreateSupportedDevice();
        var before = Enum.GetValues<DeviceFeature>().ToDictionary(f => f, device.Supports);

        device.Responses[ApiVersionCommand] = ["2"];
        device.Responses[DocumentCommand] = [CapabilityDocumentSamples.Nyquist1Firmware372];
        await device.ReadCapabilityDocumentAsync();

        foreach (var (feature, supported) in before)
        {
            Assert.Equal(supported, device.Supports(feature));
        }
    }

    /// <summary>
    /// A device whose text-command exchange answers from a per-command script, so the
    /// capability read can be driven without a transport (mirrors <c>TestableLanChipInfoDevice</c>).
    /// </summary>
    private sealed class TestableCapabilityDevice : DaqifiDevice
    {
        public TestableCapabilityDevice(string name)
            : base(name)
        {
        }

        /// <summary>Commands the device was actually asked, in order.</summary>
        public List<string> SentCommands { get; } = new();

        /// <summary>Response lines keyed by the command that triggers them.</summary>
        public Dictionary<string, string[]> Responses { get; } = new();

        public override void Send<T>(IOutboundMessage<T> message)
        {
            if (message is IOutboundMessage<string> stringMessage)
            {
                SentCommands.Add(stringMessage.Data);
            }
        }

        protected override Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Action setupAction,
            int responseTimeoutMs = 1000,
            int completionTimeoutMs = 250,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = SentCommands.Count;
            setupAction();
            return Task.FromResult(ResponsesSince(before));
        }

        protected override async Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Func<CancellationToken, Task> setupActionAsync,
            int responseTimeoutMs = 1000,
            int completionTimeoutMs = 250,
            CancellationToken cancellationToken = default)
        {
            var before = SentCommands.Count;
            await setupActionAsync(cancellationToken).ConfigureAwait(false);
            return ResponsesSince(before);
        }

        /// <summary>
        /// Concatenates the scripted replies for every command sent during one exchange, in the
        /// order they were sent — the device answers a batched exchange the same way.
        /// </summary>
        private IReadOnlyList<string> ResponsesSince(int firstCommandIndex) =>
            SentCommands
                .Skip(firstCommandIndex)
                .SelectMany(command => Responses.TryGetValue(command, out var response)
                    ? response
                    : Array.Empty<string>())
                .ToList();
    }
}
