using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Diagnostics;
using Daqifi.Core.Device.Internal;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Daqifi.Core.Tests.Device.Diagnostics;

/// <summary>
/// Unit tests that drive <see cref="DeviceDiagnosticsOperations"/> directly through its
/// <see cref="IDeviceOperationHost"/> seam, rather than through the
/// <see cref="DaqifiStreamingDevice"/> facade it was extracted from (#344, per the remaining
/// collaborators named in #464).
/// </summary>
/// <remarks>
/// <para>
/// <c>DeviceDiagnosticsTests</c> already covers what each diagnostics query puts on the wire
/// end-to-end through a testable device subclass, and is deliberately left untouched — that is the
/// evidence the extraction changed nothing observable. What a direct test adds is everything that
/// facade distance hides: that the "any line means the device answered" empty-log distinction
/// (#543) and the error-only-response check are applied per query rather than folded together; that
/// the binary-corruption guard (#537) is applied to exactly the four numeric queries and not to the
/// text-dump or short-ack ones; and that a genuinely empty response (no lines at all) is treated as
/// success everywhere it is meaningful to.
/// </para>
/// <para>
/// The fake host below throws <see cref="NotSupportedException"/> for every member outside this
/// collaborator's remit (channel state, streaming control, raw capture, feature gates), so a change
/// that reaches for one of those fails loudly instead of passing quietly.
/// </para>
/// </remarks>
public class DeviceDiagnosticsOperationsTests
{
    #region Construction

    [Fact]
    public void Constructor_NullHost_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DeviceDiagnosticsOperations(null!));
    }

    #endregion

    #region GetSystemLogAsync — the "any line means answered" distinction (#543)

    [Fact]
    public async Task GetSystemLogAsync_PopulatedLog_ParsesEntries()
    {
        var host = new FakeHost();
        host.EnqueueResponse("first entry", "second entry", "");
        var ops = new DeviceDiagnosticsOperations(host);

        var entries = await ops.GetSystemLogAsync();

        Assert.Equal(new[] { "first entry", "second entry" }, GetMessages(entries));
        Assert.Equal(new[] { "send:SYSTem:LOG?" }, host.Calls);
    }

    [Fact]
    public async Task GetSystemLogAsync_EmptyLogTerminatorOnly_ReturnsEmptyList()
    {
        // The firmware always terminates the dump with a blank line, even for an empty buffer.
        // keepBlankLines is what lets that single blank line arrive here at all.
        var host = new FakeHost();
        host.EnqueueResponse("");
        var ops = new DeviceDiagnosticsOperations(host);

        var entries = await ops.GetSystemLogAsync();

        Assert.Empty(entries);
    }

    [Fact]
    public async Task GetSystemLogAsync_NoLinesAtAll_ThrowsDistinctFromEmptyLog()
    {
        // No lines at all -- not even the terminator -- means a silent/unresponsive device, which
        // must be distinguishable from a genuinely empty log (the previous test).
        var host = new FakeHost();
        var ops = new DeviceDiagnosticsOperations(host);

        var ex = await Assert.ThrowsAsync<DeviceDiagnosticsException>(() => ops.GetSystemLogAsync());
        Assert.Contains("did not answer", ex.Message);
    }

    [Fact]
    public async Task GetSystemLogAsync_ErrorOnlyResponse_Throws()
    {
        var host = new FakeHost();
        host.EnqueueResponse("**ERROR: -113,\"Undefined header\"");
        var ops = new DeviceDiagnosticsOperations(host);

        await Assert.ThrowsAsync<DeviceDiagnosticsException>(() => ops.GetSystemLogAsync());
    }

    [Fact]
    public async Task GetSystemLogAsync_CorruptedByStreamData_KeepsRealEntriesAndDropsTheNoise()
    {
        // Issue #682: mid-stream the firmware's protobuf frames split the reply into hundreds of
        // mangled lines. The log read is destructive, so unlike the numeric queries this must NOT
        // throw the survivors away -- it drops the mangled lines one by one instead.
        var host = new FakeHost();
        host.EnqueueResponse(
            "\uFFFD\u0008\u0001\uFFFD\u0002",
            "first entry",
            "\u0000\u0012\u0004\uFFFD",
            "second entry",
            "");
        var ops = new DeviceDiagnosticsOperations(host);

        var entries = await ops.GetSystemLogAsync();

        Assert.Equal(new[] { "first entry", "second entry" }, GetMessages(entries));
    }

    [Fact]
    public async Task GetSystemLogAsync_EntirelyStreamNoise_ReturnsEmptyRatherThanThrowing()
    {
        // Documents the deliberate boundary of the #682 filter: an all-noise response is still a
        // response, and the numeric queries' DeviceDiagnosticsCorruptedResponseException is
        // deliberately not extended here (the read already cleared the device's buffer, so there
        // is nothing to retry). On hardware real entries always survived alongside the noise.
        var host = new FakeHost();
        host.EnqueueResponse("\uFFFD\u0008\u0001", "\u0000\u0012\uFFFD", "");
        var ops = new DeviceDiagnosticsOperations(host);

        Assert.Empty(await ops.GetSystemLogAsync());
    }

    [Fact]
    public async Task GetSystemLogAsync_NotConnected_ThrowsAndSendsNothing()
    {
        var host = new FakeHost { IsConnected = false };
        var ops = new DeviceDiagnosticsOperations(host);

        await Assert.ThrowsAsync<DeviceNotConnectedException>(() => ops.GetSystemLogAsync());
        Assert.Empty(host.Calls);
    }

    #endregion

    #region ClearSystemLogAsync / TestSystemLogAsync — short-ack commands, no corruption guard

    [Fact]
    public async Task ClearSystemLogAsync_Ack_Succeeds()
    {
        var host = new FakeHost();
        host.EnqueueResponse("Log cleared");
        var ops = new DeviceDiagnosticsOperations(host);

        await ops.ClearSystemLogAsync();

        Assert.Equal(new[] { "send:SYSTem:LOG:CLEar" }, host.Calls);
    }

    [Fact]
    public async Task ClearSystemLogAsync_ErrorOnlyResponse_Throws()
    {
        var host = new FakeHost();
        host.EnqueueResponse("**ERROR: -200,\"Execution error\"");
        var ops = new DeviceDiagnosticsOperations(host);

        await Assert.ThrowsAsync<DeviceDiagnosticsException>(() => ops.ClearSystemLogAsync());
    }

    [Fact]
    public async Task TestSystemLogAsync_Ack_Succeeds()
    {
        var host = new FakeHost();
        host.EnqueueResponse("Added test log messages");
        var ops = new DeviceDiagnosticsOperations(host);

        await ops.TestSystemLogAsync();

        Assert.Equal(new[] { "send:SYSTem:LOG:TEST" }, host.Calls);
    }

    [Fact]
    public async Task TestSystemLogAsync_ErrorOnlyResponse_Throws()
    {
        var host = new FakeHost();
        host.EnqueueResponse("ERROR -200,\"Execution error\"");
        var ops = new DeviceDiagnosticsOperations(host);

        await Assert.ThrowsAsync<DeviceDiagnosticsException>(() => ops.TestSystemLogAsync());
    }

    #endregion

    #region SetLogLevelAsync — validation ordering and response classification

    [Fact]
    public async Task SetLogLevelAsync_ValidEcho_ReturnsParsedSetting()
    {
        var host = new FakeHost();
        host.EnqueueResponse("STREAM: 2 (ceiling 3)");
        var ops = new DeviceDiagnosticsOperations(host);

        var setting = await ops.SetLogLevelAsync("STREAM", 2);

        Assert.Equal("STREAM", setting.Module);
        Assert.Equal(2, setting.Level);
        Assert.Equal(3, setting.Ceiling);
        Assert.Equal(new[] { "send:SYSTem:LOG:LEVel STREAM,2" }, host.Calls);
    }

    [Fact]
    public async Task SetLogLevelAsync_DeviceRejects_ThrowsWithoutParsing()
    {
        var host = new FakeHost();
        host.EnqueueResponse("**ERROR: -113,\"Undefined header\"");
        var ops = new DeviceDiagnosticsOperations(host);

        var ex = await Assert.ThrowsAsync<DeviceDiagnosticsException>(
            () => ops.SetLogLevelAsync("STREAM", 2));
        Assert.Contains("STREAM", ex.Message);
    }

    [Fact]
    public async Task SetLogLevelAsync_UnparseableEcho_Throws()
    {
        var host = new FakeHost();
        host.EnqueueResponse("not a level echo");
        var ops = new DeviceDiagnosticsOperations(host);

        await Assert.ThrowsAsync<DeviceDiagnosticsException>(() => ops.SetLogLevelAsync("STREAM", 2));
    }

    [Fact]
    public async Task SetLogLevelAsync_CorruptedByStreamData_ThrowsCorruptedResponseException()
    {
        // Checked after the rejection check but before the parse -- a mangled echo would otherwise
        // fail the parse with a less useful message.
        var host = new FakeHost();
        host.EnqueueResponse("STR\u0001EAM: 2 (ceiling 3)");
        var ops = new DeviceDiagnosticsOperations(host);

        await Assert.ThrowsAsync<DeviceDiagnosticsCorruptedResponseException>(
            () => ops.SetLogLevelAsync("STREAM", 2));
    }

    [Fact]
    public async Task SetLogLevelAsync_InvalidArguments_ThrowBeforeConnectionCheck()
    {
        // Command construction (and its argument validation) happens before EnsureConnected, so
        // this must surface as an argument exception even though the host reports disconnected --
        // matching SetAnalogOutput / SetDioDirection in ChannelControlOperations.
        var host = new FakeHost { IsConnected = false };
        var ops = new DeviceDiagnosticsOperations(host);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ops.SetLogLevelAsync("STREAM", 99));
        Assert.Empty(host.Calls);
    }

    #endregion

    #region GetCommandHistoryAsync

    [Fact]
    public async Task GetCommandHistoryAsync_PopulatedHistory_ReturnsOldestFirst()
    {
        var host = new FakeHost();
        host.EnqueueResponse("Last 2 commands:", "2: SYSTem:LOG?", "1: SYSTem:LOG:CMDHistory?");
        var ops = new DeviceDiagnosticsOperations(host);

        var commands = await ops.GetCommandHistoryAsync();

        Assert.Equal(new[] { "SYSTem:LOG?", "SYSTem:LOG:CMDHistory?" }, commands);
    }

    [Fact]
    public async Task GetCommandHistoryAsync_NoHistoryMarker_ReturnsEmptyListWithoutThrowing()
    {
        var host = new FakeHost();
        host.EnqueueResponse("No command history");
        var ops = new DeviceDiagnosticsOperations(host);

        var commands = await ops.GetCommandHistoryAsync();

        Assert.Empty(commands);
    }

    [Fact]
    public async Task GetCommandHistoryAsync_ErrorOnlyResponse_Throws()
    {
        var host = new FakeHost();
        host.EnqueueResponse("**ERROR: -113,\"Undefined header\"");
        var ops = new DeviceDiagnosticsOperations(host);

        await Assert.ThrowsAsync<DeviceDiagnosticsException>(() => ops.GetCommandHistoryAsync());
    }

    #endregion

    #region GetSystemErrorCountAsync — numeric parse and the corruption guard

    [Fact]
    public async Task GetSystemErrorCountAsync_NumericReply_ReturnsCount()
    {
        var host = new FakeHost();
        host.EnqueueResponse("3");
        var ops = new DeviceDiagnosticsOperations(host);

        var count = await ops.GetSystemErrorCountAsync();

        Assert.Equal(3, count);
        Assert.Equal(new[] { "send:SYSTem:ERRor:COUNt?" }, host.Calls);
    }

    [Fact]
    public async Task GetSystemErrorCountAsync_UnparseableReply_Throws()
    {
        var host = new FakeHost();
        host.EnqueueResponse("not-a-count");
        var ops = new DeviceDiagnosticsOperations(host);

        var ex = await Assert.ThrowsAsync<DeviceDiagnosticsException>(() => ops.GetSystemErrorCountAsync());
        Assert.Contains("unparseable", ex.Message);
    }

    [Fact]
    public async Task GetSystemErrorCountAsync_CorruptedByStreamData_ThrowsBeforeAttemptingToParse()
    {
        // A control character welded into the reply is the #537 signature. Checked before the
        // numeric parse, so this must be the corrupted-response type, not the generic unparseable one.
        var host = new FakeHost();
        host.EnqueueResponse("\u00013");
        var ops = new DeviceDiagnosticsOperations(host);

        await Assert.ThrowsAsync<DeviceDiagnosticsCorruptedResponseException>(
            () => ops.GetSystemErrorCountAsync());
    }

    [Fact]
    public async Task GetSystemErrorCountAsync_SkipsBlankLinesToFindTheNumber()
    {
        var host = new FakeHost();
        host.EnqueueResponse("", "7");
        var ops = new DeviceDiagnosticsOperations(host);

        var count = await ops.GetSystemErrorCountAsync();

        Assert.Equal(7, count);
    }

    #endregion

    #region GetStreamStatsAsync / GetMemoryDiagnosticsAsync — key/value counters, corruption guard

    [Fact]
    public async Task GetStreamStatsAsync_ParsesKnownCounters()
    {
        var host = new FakeHost();
        host.EnqueueResponse("TotalSamplesStreamed=1000", "QueueDroppedSamples=2");
        var ops = new DeviceDiagnosticsOperations(host);

        var stats = await ops.GetStreamStatsAsync();

        Assert.Equal(1000UL, stats.TotalSamplesStreamed);
        Assert.Equal(2UL, stats.QueueDroppedSamples);
    }

    [Fact]
    public async Task GetStreamStatsAsync_NoParseableCounters_Throws()
    {
        var host = new FakeHost();
        host.EnqueueResponse("not a key value line");
        var ops = new DeviceDiagnosticsOperations(host);

        await Assert.ThrowsAsync<DeviceDiagnosticsException>(() => ops.GetStreamStatsAsync());
    }

    [Fact]
    public async Task GetStreamStatsAsync_CorruptedByStreamData_Throws()
    {
        var host = new FakeHost();
        host.EnqueueResponse("Total\u0001SamplesStreamed=1000");
        var ops = new DeviceDiagnosticsOperations(host);

        await Assert.ThrowsAsync<DeviceDiagnosticsCorruptedResponseException>(
            () => ops.GetStreamStatsAsync());
    }

    [Fact]
    public async Task GetMemoryDiagnosticsAsync_ParsesKnownFields()
    {
        var host = new FakeHost();
        host.EnqueueResponse("HeapTotal=65536", "HeapFree=32768", "LargestFreeBlock=16384");
        var ops = new DeviceDiagnosticsOperations(host);

        var diagnostics = await ops.GetMemoryDiagnosticsAsync();

        Assert.Equal(65536UL, diagnostics.HeapTotal);
        Assert.Equal(32768UL, diagnostics.HeapFree);
        Assert.Equal(16384UL, diagnostics.LargestFreeBlock);
    }

    [Fact]
    public async Task GetMemoryDiagnosticsAsync_NoParseableFields_Throws()
    {
        var host = new FakeHost();
        host.EnqueueResponse("garbage");
        var ops = new DeviceDiagnosticsOperations(host);

        await Assert.ThrowsAsync<DeviceDiagnosticsException>(() => ops.GetMemoryDiagnosticsAsync());
    }

    [Fact]
    public async Task GetMemoryDiagnosticsAsync_CorruptedByStreamData_Throws()
    {
        var host = new FakeHost();
        host.EnqueueResponse("Heap\u0001Total=65536");
        var ops = new DeviceDiagnosticsOperations(host);

        await Assert.ThrowsAsync<DeviceDiagnosticsCorruptedResponseException>(
            () => ops.GetMemoryDiagnosticsAsync());
    }

    [Fact]
    public async Task GetMemoryDiagnosticsAsync_NotConnected_ThrowsAndSendsNothing()
    {
        var host = new FakeHost { IsConnected = false };
        var ops = new DeviceDiagnosticsOperations(host);

        await Assert.ThrowsAsync<DeviceNotConnectedException>(() => ops.GetMemoryDiagnosticsAsync());
        Assert.Empty(host.Calls);
    }

    #endregion

    private static IEnumerable<string> GetMessages(IEnumerable<SystemLogEntry> entries)
    {
        foreach (var entry in entries)
        {
            yield return entry.Message;
        }
    }

    #region Fake host

    /// <summary>
    /// Minimal <see cref="IDeviceOperationHost"/> double scoped to what
    /// <see cref="DeviceDiagnosticsOperations"/> actually uses: connection state and one text
    /// exchange per query. Every other member throws <see cref="NotSupportedException"/> so a change
    /// that reaches outside this collaborator's remit fails loudly.
    /// </summary>
    private sealed class FakeHost : IDeviceOperationHost
    {
        private readonly List<string> _calls = new();
        private readonly Queue<IReadOnlyList<string>> _responses = new();

        public IReadOnlyList<string> Calls
        {
            get { lock (_calls) { return _calls.ToArray(); } }
        }

        public bool IsConnected { get; set; } = true;
        public bool IsStreaming { get; set; }
        public int StreamingFrequency { get; set; } = 100;
        public long ChannelStateVersion { get; set; }

        public void EnqueueResponse(params string[] lines) => _responses.Enqueue(lines);

        private void Record(string call)
        {
            lock (_calls) { _calls.Add(call); }
        }

        public void Send<T>(IOutboundMessage<T> message) => Record("send:" + message.Data);

        public Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Action setupAction,
            int responseTimeoutMs = 1000,
            int completionTimeoutMs = 250,
            CancellationToken cancellationToken = default,
            Func<CancellationToken, Task>? prepareAsync = null,
            Func<Task>? finalizeAsync = null,
            bool keepBlankLines = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            setupAction();
            IReadOnlyList<string> lines = _responses.Count > 0 ? _responses.Dequeue() : Array.Empty<string>();

            // Mirror the real TextExchangeEngine: blank lines are dropped unless the caller opted
            // into keepBlankLines. Without this, a regression that stops passing keepBlankLines: true
            // for SYSTem:LOG? would go undetected -- the fake would keep handing back the queued
            // terminator regardless of what the collaborator actually requested.
            if (!keepBlankLines)
            {
                lines = lines.Where(line => line.Length > 0).ToList();
            }

            return Task.FromResult(lines);
        }

        // Outside this collaborator's remit -- reaching for any of these is a regression.
        public DeviceMetadata Metadata => throw new NotSupportedException();
        public void StopStreaming() => throw new NotSupportedException();
        public void StartStreaming() => throw new NotSupportedException();
        public void Disconnect() => throw new NotSupportedException();
        public IReadOnlyList<Daqifi.Core.Channel.IChannel> SnapshotChannels() => throw new NotSupportedException();
        public void WithChannelsLock(Action action) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> DrainErrorQueueAsync(
            int maxIterations = 256, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task ExecuteRawCaptureAsync(
            Func<System.IO.Stream, CancellationToken, Task> rawAction,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public void EnsureSupported(DeviceFeature feature) => throw new NotSupportedException();
        public FeatureNotSupportedException CreateFeatureNotSupportedException(DeviceFeature feature)
            => throw new NotSupportedException();
        public void RaiseStreamFrameDiscarded(StreamFrameDiscardedEventArgs e) => throw new NotSupportedException();
        public void RaiseGapDetected(TimestampGapEventArgs e) => throw new NotSupportedException();
        public void RaiseRawStreamFrame(DaqifiOutMessage message) => throw new NotSupportedException();
        public void RaiseStreamDecodeFailure(Exception error) => throw new NotSupportedException();
    }

    #endregion
}
