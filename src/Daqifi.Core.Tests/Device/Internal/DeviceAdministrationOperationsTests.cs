using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Internal;
using Daqifi.Core.Device.SdCard;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Daqifi.Core.Tests.Device.Internal;

/// <summary>
/// Unit tests for <see cref="DeviceAdministrationOperations"/>, the reboot / ADC-calibration /
/// voltage-precision / friendly-name block extracted from <see cref="DaqifiStreamingDevice"/> (#344).
/// </summary>
/// <remarks>
/// <para>
/// What each command puts on the wire, and that each one refuses a disconnected device, is already
/// pinned through the device by <c>DaqifiStreamingDeviceTests</c>,
/// <c>DaqifiStreamingDeviceFriendlyNameTests</c> and <c>DeviceNotConnectedExceptionTests</c>. Those
/// are deliberately untouched — they are the evidence that the extraction changed nothing, so they
/// are not repeated here.
/// </para>
/// <para>
/// These add the part only a direct test can see: the <b>ordering and the total set of calls back
/// into the host</b>. The fake below throws on every member outside this block's remit, so a future
/// change that reaches for the channels lock, stops a stream, or performs device I/O beyond the one
/// command fails loudly rather than passing quietly.
/// </para>
/// </remarks>
public class DeviceAdministrationOperationsTests
{
    [Fact]
    public void Constructor_NullHost_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DeviceAdministrationOperations(null!));
    }

    #region Reboot

    [Fact]
    public void Reboot_SendsTheRebootCommandBeforeTearingTheConnectionDown()
    {
        var host = new FakeHost { IsConnected = true };

        new DeviceAdministrationOperations(host).Reboot();

        // Order is the whole point: disconnecting first would close the transport the reboot
        // command still has to travel over, so the device would never be told to restart.
        Assert.Equal(new[] { "send:SYSTem:REboot", "disconnect" }, host.Calls);
    }

    [Fact]
    public void Reboot_WhenNotConnected_ThrowsAndLeavesTheConnectionAlone()
    {
        var host = new FakeHost { IsConnected = false };

        Assert.Throws<DeviceNotConnectedException>(() => new DeviceAdministrationOperations(host).Reboot());

        // The guard runs before anything else, so a refused reboot neither sends nor disconnects.
        Assert.Empty(host.Calls);
    }

    #endregion

    #region One command, and nothing else

    public static IEnumerable<object[]> SingleCommandOperations()
    {
        yield return new object[] { "SaveAdcCalibration", "CONFigure:ADC:SAVEcal" };
        yield return new object[] { "LoadAdcCalibration", "CONFigure:ADC:LOADcal" };
        yield return new object[] { "SaveFactoryAdcCalibration", "CONFigure:ADC:SAVEFcal" };
        yield return new object[] { "LoadFactoryAdcCalibration", "CONFigure:ADC:LOADFcal" };
        yield return new object[] { "SaveVoltagePrecision", "CONFigure:VOLTage:SAVE" };
        yield return new object[] { "LoadVoltagePrecision", "CONFigure:VOLTage:LOAD" };
        yield return new object[] { "UseAdcCalibration(0)", "CONFigure:ADC:USECal 0" };
        yield return new object[] { "UseAdcCalibration(1)", "CONFigure:ADC:USECal 1" };
    }

    /// <summary>
    /// Each of these is a single fire-and-forget command. The assertion is not only that the right
    /// text goes out but that the whole interaction with the device is that one send — no stream
    /// stop, no channels lock, no metadata write, no disconnect. Every one of those would throw
    /// from <see cref="FakeHost"/>, and a second send would fail the equality below.
    /// </summary>
    [Theory]
    [MemberData(nameof(SingleCommandOperations))]
    public void SingleCommandOperation_SendsExactlyThatCommandAndTouchesNothingElse(
        string operation,
        string expectedCommand)
    {
        var host = new FakeHost { IsConnected = true };
        var administration = new DeviceAdministrationOperations(host);

        // Dispatched by name rather than by a delegate parameter: the collaborator is internal, so
        // an Action<DeviceAdministrationOperations> cannot appear on a public test method.
        switch (operation)
        {
            case "SaveAdcCalibration": administration.SaveAdcCalibration(); break;
            case "LoadAdcCalibration": administration.LoadAdcCalibration(); break;
            case "SaveFactoryAdcCalibration": administration.SaveFactoryAdcCalibration(); break;
            case "LoadFactoryAdcCalibration": administration.LoadFactoryAdcCalibration(); break;
            case "SaveVoltagePrecision": administration.SaveVoltagePrecision(); break;
            case "LoadVoltagePrecision": administration.LoadVoltagePrecision(); break;
            case "UseAdcCalibration(0)": administration.UseAdcCalibration(0); break;
            case "UseAdcCalibration(1)": administration.UseAdcCalibration(1); break;
            default: throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unmapped operation.");
        }

        Assert.Equal(new[] { "send:" + expectedCommand }, host.Calls);
    }

    [Fact]
    public void SetAdcCalibrationSlope_SendsExactlyOneCommand()
    {
        var host = new FakeHost { IsConnected = true };

        new DeviceAdministrationOperations(host).SetAdcCalibrationSlope(2, 1.0025);

        Assert.Single(host.Calls);
        Assert.StartsWith("send:CONFigure:ADC:chanCALM ", host.Calls[0], StringComparison.Ordinal);
    }

    [Fact]
    public void SetAdcCalibrationOffset_SendsExactlyOneCommand()
    {
        var host = new FakeHost { IsConnected = true };

        new DeviceAdministrationOperations(host).SetAdcCalibrationOffset(3, -0.0031);

        Assert.Single(host.Calls);
        Assert.StartsWith("send:CONFigure:ADC:chanCALB ", host.Calls[0], StringComparison.Ordinal);
    }

    #endregion

    #region Friendly name

    [Fact]
    public async Task SetFriendlyNameAsync_SendsSetThenSaveAndThenWritesMetadata()
    {
        var host = new FakeHost { IsConnected = true };

        await new DeviceAdministrationOperations(host).SetFriendlyNameAsync("Bench01");

        Assert.Equal(2, host.Calls.Count);
        Assert.StartsWith("send:SYSTem:DEVice:NAME ", host.Calls[0], StringComparison.Ordinal);
        Assert.Equal("send:SYSTem:DEVice:NAME:SAVE", host.Calls[1]);
        Assert.Equal("Bench01", host.Metadata.FriendlyName);
    }

    /// <summary>
    /// The metadata write is optimistic because the firmware never echoes the name back — but only
    /// once both commands have actually gone out. A send that throws means the device was never
    /// told, so the local name must not claim otherwise.
    /// </summary>
    [Fact]
    public async Task SetFriendlyNameAsync_WhenTheSaveSendFails_LeavesMetadataUnchanged()
    {
        var host = new FakeHost { IsConnected = true, FailSendAt = 2 };
        host.Metadata.FriendlyName = "Original";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new DeviceAdministrationOperations(host).SetFriendlyNameAsync("Bench01"));

        Assert.Equal("Original", host.Metadata.FriendlyName);
    }

    [Fact]
    public async Task SetFriendlyNameAsync_AlreadyCancelled_SendsNothingAndLeavesMetadataUnchanged()
    {
        var host = new FakeHost { IsConnected = true };
        host.Metadata.FriendlyName = "Original";
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new DeviceAdministrationOperations(host).SetFriendlyNameAsync("Bench01", cts.Token));

        Assert.Empty(host.Calls);
        Assert.Equal("Original", host.Metadata.FriendlyName);
    }

    #endregion

    #region Confirming variants

    /// <summary>The reply a device gives to <c>SYSTem:ERRor?</c> when the queue is clean.</summary>
    private const string NoError = "0,\"No error\"";

    /// <summary>
    /// What the bench saw on a real Nyquist (fw 3.7.2) when its user calibration bank had never been
    /// written — see the evidence on #344.
    /// </summary>
    private const string ExecutionError = "-200,\"Execution error\"";

    /// <summary>
    /// Every confirming variant, paired with the command text it must put on the wire. The two
    /// coefficient commands carry arguments, so the expected text is a prefix of what goes out.
    /// </summary>
    public static IEnumerable<object[]> ConfirmingOperations()
    {
        yield return Site("SaveAdcCalibrationAsync", "CONFigure:ADC:SAVEcal", (a, t) => a.SaveAdcCalibrationAsync(t));
        yield return Site("LoadAdcCalibrationAsync", "CONFigure:ADC:LOADcal", (a, t) => a.LoadAdcCalibrationAsync(t));
        yield return Site("SaveFactoryAdcCalibrationAsync", "CONFigure:ADC:SAVEFcal", (a, t) => a.SaveFactoryAdcCalibrationAsync(t));
        yield return Site("LoadFactoryAdcCalibrationAsync", "CONFigure:ADC:LOADFcal", (a, t) => a.LoadFactoryAdcCalibrationAsync(t));
        yield return Site("SaveVoltagePrecisionAsync", "CONFigure:VOLTage:SAVE", (a, t) => a.SaveVoltagePrecisionAsync(t));
        yield return Site("LoadVoltagePrecisionAsync", "CONFigure:VOLTage:LOAD", (a, t) => a.LoadVoltagePrecisionAsync(t));
        yield return Site("UseAdcCalibrationAsync(0)", "CONFigure:ADC:USECal 0", (a, t) => a.UseAdcCalibrationAsync(0, t));
        yield return Site("UseAdcCalibrationAsync(1)", "CONFigure:ADC:USECal 1", (a, t) => a.UseAdcCalibrationAsync(1, t));
        yield return Site("SetAdcCalibrationSlopeAsync", "CONFigure:ADC:chanCALM ", (a, t) => a.SetAdcCalibrationSlopeAsync(2, 1.0025, t));
        yield return Site("SetAdcCalibrationOffsetAsync", "CONFigure:ADC:chanCALB ", (a, t) => a.SetAdcCalibrationOffsetAsync(3, -0.0031, t));

        // The collaborator is internal, so the delegate cannot be typed over it on a public test
        // method — object is the widest type the signature can carry, and Invoke casts it back.
        static object[] Site(string name, string command, Func<DeviceAdministrationOperations, CancellationToken, Task> call)
            => [name, command, call];
    }

    private static Task Invoke(object call, FakeHost host, CancellationToken cancellationToken = default)
        => ((Func<DeviceAdministrationOperations, CancellationToken, Task>)call)(
            new DeviceAdministrationOperations(host), cancellationToken);

    /// <summary>
    /// The shape of a confirming command, in order: drain the error queue, then — in one exchange —
    /// the command and the <c>SYSTem:ERRor?</c> that reads the device's verdict on it. The drain is
    /// what makes the verdict attributable: without it, the entry popped afterwards could belong to
    /// any earlier command. That it happens before the exchange rather than inside it is the whole
    /// reason the ordering is asserted and not just the set of calls.
    /// </summary>
    [Theory]
    [MemberData(nameof(ConfirmingOperations))]
    public async Task ConfirmingOperation_WhenTheDeviceAccepts_DrainsThenSendsTheCommandAndReadsTheVerdict(
        string siteName,
        string expectedCommand,
        object call)
    {
        _ = siteName;
        var host = new FakeHost { IsConnected = true, ExchangeResponse = new[] { NoError } };

        await Invoke(call, host);

        Assert.Equal(4, host.Calls.Count);
        Assert.Equal("drain:16", host.Calls[0]);
        Assert.Equal("exchange", host.Calls[1]);
        Assert.StartsWith("send:" + expectedCommand, host.Calls[2], StringComparison.Ordinal);
        Assert.Equal("send:SYSTem:ERRor?", host.Calls[3]);
    }

    /// <summary>
    /// The whole point of this surface. <c>LoadAdcCalibration()</c> returns normally against a device
    /// that answered <c>-200</c>; the confirming variant refuses to call that a success, and hands the
    /// caller the device's own code and line rather than a generic failure.
    /// </summary>
    [Fact]
    public async Task LoadAdcCalibrationAsync_WhenTheDeviceReportsAnError_ThrowsWithTheDeviceCode()
    {
        var host = new FakeHost { IsConnected = true, ExchangeResponse = new[] { ExecutionError } };

        var ex = await Assert.ThrowsAsync<DeviceCommandFailedException>(
            () => new DeviceAdministrationOperations(host).LoadAdcCalibrationAsync());

        Assert.Equal("CONFigure:ADC:LOADcal", ex.Command);
        Assert.Equal(-200, ex.ErrorCode);
        Assert.Equal(ExecutionError, ex.DeviceResponse);
    }

    /// <summary>
    /// Every confirming variant classifies the same way — a refusal is a refusal whichever command
    /// drew it, and none of them may report success on one.
    /// </summary>
    [Theory]
    [MemberData(nameof(ConfirmingOperations))]
    public async Task ConfirmingOperation_WhenTheDeviceReportsAnError_Throws(
        string siteName,
        string expectedCommand,
        object call)
    {
        _ = siteName;
        var host = new FakeHost { IsConnected = true, ExchangeResponse = new[] { ExecutionError } };

        var ex = await Assert.ThrowsAsync<DeviceCommandFailedException>(() => Invoke(call, host));

        Assert.StartsWith(expectedCommand, ex.Command, StringComparison.Ordinal);
        Assert.Equal(-200, ex.ErrorCode);
        Assert.Equal(ExecutionError, ex.DeviceResponse);
    }

    /// <summary>
    /// The verdict trails the command, so the exchange must not be allowed to close on the default
    /// 250ms inactivity window: on a device that echoes, the echo starts that clock and a merely-slow
    /// verdict would read as a missing one, failing a command the device had accepted. Same reason the
    /// SD listing raises its own completion window — its terminator is this same query.
    /// </summary>
    [Fact]
    public async Task ConfirmingOperation_AsksForAWindowLongEnoughForTheVerdictToTrailAnEcho()
    {
        var host = new FakeHost { IsConnected = true, ExchangeResponse = new[] { NoError } };

        await new DeviceAdministrationOperations(host).SaveAdcCalibrationAsync();

        Assert.Equal(3000, host.LastResponseTimeoutMs);
        Assert.Equal(1000, host.LastCompletionTimeoutMs);
    }

    /// <summary>
    /// A device that volunteers <c>**ERROR: ...</c> alongside the command is complaining about that
    /// command directly, so it is classified from the line itself rather than from the queue reply.
    /// </summary>
    [Fact]
    public async Task ConfirmingOperation_WhenTheDeviceVolunteersAnErrorLine_ThrowsWithThatCode()
    {
        var host = new FakeHost
        {
            IsConnected = true,
            ExchangeResponse = new[] { "**ERROR: -113,\"Undefined header\"", NoError },
        };

        var ex = await Assert.ThrowsAsync<DeviceCommandFailedException>(
            () => new DeviceAdministrationOperations(host).SaveVoltagePrecisionAsync());

        Assert.Equal(-113, ex.ErrorCode);
        Assert.Equal("**ERROR: -113,\"Undefined header\"", ex.DeviceResponse);
    }

    /// <summary>
    /// <c>ERROR</c> lines the classifier matches but that carry no readable code — a bare token, or a
    /// non-numeric one. These are still refusals, so they must throw; what they must not do is report
    /// a code of <c>0</c>, the one value SCPI reserves for "no error". The raw line survives, since it
    /// is the only diagnostic there is.
    /// </summary>
    [Theory]
    [InlineData("ERROR")]
    [InlineData("**ERROR")]
    [InlineData("ERROR: something went wrong")]
    public async Task ConfirmingOperation_WhenAVolunteeredErrorLineCarriesNoCode_ThrowsWithoutClaimingCodeZero(
        string errorLine)
    {
        var host = new FakeHost { IsConnected = true, ExchangeResponse = new[] { errorLine } };

        var ex = await Assert.ThrowsAsync<DeviceCommandFailedException>(
            () => new DeviceAdministrationOperations(host).LoadAdcCalibrationAsync());

        Assert.Null(ex.ErrorCode);
        Assert.Equal(errorLine, ex.DeviceResponse);
        Assert.Contains(errorLine, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An <c>ERROR</c>-shaped line whose code is <c>0</c> says "no error", so it is not evidence of a
    /// refusal — a device that answers the queue read in that shape rather than the bare one must not
    /// be reported as having rejected a command it accepted. The bare verdict alongside it decides.
    /// </summary>
    [Fact]
    public async Task ConfirmingOperation_WhenAnErrorShapedLineSaysCodeZero_DoesNotTreatItAsARefusal()
    {
        var host = new FakeHost
        {
            IsConnected = true,
            ExchangeResponse = new[] { "**ERROR: 0,\"No error\"", NoError },
        };

        await new DeviceAdministrationOperations(host).LoadAdcCalibrationAsync();
    }

    /// <summary>
    /// Code 0 never reaches <see cref="DeviceCommandFailedException.ErrorCode"/> from any path, which
    /// is what lets callers branch on it: a non-null code always means a real refusal.
    /// </summary>
    /// <remarks>
    /// The line itself must survive. Dropping it would leave the failure reporting silence from a
    /// device that plainly answered — the diagnostic a reader needs most when a device is speaking a
    /// dialect this classifier does not expect. And the message must not call that code unreadable:
    /// it is right there, it just says "no error", which cannot describe a refusal.
    /// </remarks>
    [Theory]
    [InlineData("**ERROR: 0,\"No error\"")]
    [InlineData("ERROR: 0,\"No error\"")]
    public async Task ConfirmingOperation_NeverReportsErrorCodeZero_ButKeepsTheLineAndDescribesItHonestly(
        string errorLine)
    {
        var host = new FakeHost { IsConnected = true, ExchangeResponse = new[] { errorLine } };

        // No bare verdict accompanies it, so this is unconfirmed rather than refused — but either
        // way the one thing it must not be is a reported error code of 0.
        var ex = await Assert.ThrowsAsync<DeviceCommandFailedException>(
            () => new DeviceAdministrationOperations(host).LoadAdcCalibrationAsync());

        Assert.NotEqual(0, ex.ErrorCode ?? -1);
        Assert.Equal(errorLine, ex.DeviceResponse);
        Assert.Contains(errorLine, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not readable", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("no readable", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The never-zero contract holds at the API boundary too, not just on the paths this collaborator
    /// happens to take: constructing the exception directly with <c>0</c> records "no readable code"
    /// rather than a refusal under SCPI's "no error" value. Normalised rather than rejected, because a
    /// constructor that threw would swap a diagnosable device failure for an argument error.
    /// </summary>
    [Fact]
    public void DeviceCommandFailedException_ConstructedWithCodeZero_ReportsNoCodeAndKeepsTheLine()
    {
        var ex = new DeviceCommandFailedException("CONFigure:ADC:LOADcal", 0, "**ERROR: 0,\"No error\"");

        Assert.Null(ex.ErrorCode);
        Assert.Equal("**ERROR: 0,\"No error\"", ex.DeviceResponse);
        Assert.Equal("CONFigure:ADC:LOADcal", ex.Command);
        Assert.DoesNotContain("rejected", ex.Message, StringComparison.Ordinal);

        // The code is readable — it just says "no error". Claiming otherwise would contradict the
        // line quoted alongside it in the same message.
        Assert.DoesNotContain("no readable", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no error", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeviceCommandFailedException_ConstructedWithARealCode_KeepsIt()
    {
        var ex = new DeviceCommandFailedException("CONFigure:ADC:LOADcal", -200, "-200,\"Execution error\"");

        Assert.Equal(-200, ex.ErrorCode);
        Assert.Contains("rejected", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The guard behind the assertion above: these lines really do classify as SCPI errors while
    /// yielding no code, so the null-code path is reachable rather than theoretical.
    /// </summary>
    [Theory]
    [InlineData("ERROR")]
    [InlineData("**ERROR")]
    [InlineData("ERROR: something went wrong")]
    public void CodelessErrorLines_ClassifyAsScpiErrorsButYieldNoCode(string errorLine)
    {
        Assert.True(ScpiResponseClassifier.IsScpiErrorLine(errorLine));
        Assert.False(ScpiResponseClassifier.TryExtractErrorCode(errorLine, out _));
    }

    public static IEnumerable<object[]> UnreadableVerdicts()
    {
        // Nothing came back at all — a timeout, or a device that stopped answering.
        yield return [Array.Empty<string>()];
        // Something came back, but nothing in it is a SYSTem:ERRor? reply.
        yield return [new[] { "CONFigure:ADC:LOADcal" }];
    }

    /// <summary>
    /// No readable verdict is not a success. The command may or may not have been applied, and saying
    /// so — with a null <see cref="DeviceCommandFailedException.ErrorCode"/> to separate it from a
    /// refusal — is the only honest answer available.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnreadableVerdicts))]
    public async Task ConfirmingOperation_WhenTheVerdictIsUnreadable_ThrowsWithoutAnErrorCode(string[] response)
    {
        var host = new FakeHost { IsConnected = true, ExchangeResponse = response };

        var ex = await Assert.ThrowsAsync<DeviceCommandFailedException>(
            () => new DeviceAdministrationOperations(host).LoadAdcCalibrationAsync());

        Assert.Equal("CONFigure:ADC:LOADcal", ex.Command);
        Assert.Null(ex.ErrorCode);
        Assert.Contains("did not confirm", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A trailing <c>0,"No error"</c> is the verdict even when the device volunteered other output
    /// first — the last error-queue reply in the exchange is the one this command's query drew.
    /// </summary>
    [Fact]
    public async Task ConfirmingOperation_WhenExtraOutputPrecedesACleanVerdict_Succeeds()
    {
        var host = new FakeHost
        {
            IsConnected = true,
            ExchangeResponse = new[] { "CONFigure:ADC:LOADcal", string.Empty, NoError },
        };

        await new DeviceAdministrationOperations(host).LoadAdcCalibrationAsync();
    }

    [Theory]
    [MemberData(nameof(ConfirmingOperations))]
    public async Task ConfirmingOperation_WhenNotConnected_ThrowsAndTouchesNothing(
        string siteName,
        string expectedCommand,
        object call)
    {
        _ = siteName;
        _ = expectedCommand;
        var host = new FakeHost { IsConnected = false };

        await Assert.ThrowsAsync<DeviceNotConnectedException>(() => Invoke(call, host));

        // Not even the drain: a refused command must not touch the device's error queue.
        Assert.Empty(host.Calls);
    }

    [Theory]
    [MemberData(nameof(ConfirmingOperations))]
    public async Task ConfirmingOperation_AlreadyCancelled_ThrowsAndTouchesNothing(
        string siteName,
        string expectedCommand,
        object call)
    {
        _ = siteName;
        _ = expectedCommand;
        var host = new FakeHost { IsConnected = true };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Invoke(call, host, cts.Token));

        Assert.Empty(host.Calls);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public async Task UseAdcCalibrationAsync_InvalidBank_ThrowsBeforeTouchingTheDevice(int bank)
    {
        var host = new FakeHost { IsConnected = true };

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => new DeviceAdministrationOperations(host).UseAdcCalibrationAsync(bank));

        Assert.Equal("bank", ex.ParamName);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task SetAdcCalibrationSlopeAsync_NegativeChannel_ThrowsBeforeTouchingTheDevice()
    {
        var host = new FakeHost { IsConnected = true };

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => new DeviceAdministrationOperations(host).SetAdcCalibrationSlopeAsync(-1, 1.0));

        Assert.Equal("channelNumber", ex.ParamName);
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task SetAdcCalibrationOffsetAsync_NegativeChannel_ThrowsBeforeTouchingTheDevice()
    {
        var host = new FakeHost { IsConnected = true };

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => new DeviceAdministrationOperations(host).SetAdcCalibrationOffsetAsync(-1, 0.0));

        Assert.Equal("channelNumber", ex.ParamName);
        Assert.Empty(host.Calls);
    }

    #endregion

    /// <summary>
    /// An <see cref="IDeviceOperationHost"/> that records, in order, the things this block is
    /// allowed to do to a device: send a command, run a text exchange, drain the error queue, and
    /// (for reboot) disconnect. Everything else throws.
    /// </summary>
    private sealed class FakeHost : IDeviceOperationHost
    {
        private int _sendCount;

        public List<string> Calls { get; } = new();

        public bool IsConnected { get; set; }

        public DeviceMetadata Metadata { get; } = new();

        /// <summary>1-based index of the send that should throw, or 0 for none.</summary>
        public int FailSendAt { get; set; }

        /// <summary>What a text exchange hands back — the device's side of a confirming command.</summary>
        public IReadOnlyList<string> ExchangeResponse { get; set; } = Array.Empty<string>();

        /// <summary>The response timeout the last exchange asked for.</summary>
        public int? LastResponseTimeoutMs { get; private set; }

        /// <summary>The completion timeout the last exchange asked for.</summary>
        public int? LastCompletionTimeoutMs { get; private set; }

        public void Send<T>(IOutboundMessage<T> message)
        {
            if (++_sendCount == FailSendAt)
            {
                throw new InvalidOperationException("transport refused the command");
            }

            Calls.Add("send:" + message.Data);
        }

        public void Disconnect() => Calls.Add("disconnect");

        // Outside this block's remit — reaching for any of these is a regression, not a refinement.
        public bool IsUsbConnection => throw new NotSupportedException();
        public bool IsStreaming { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public int StreamingFrequency => throw new NotSupportedException();
        public TimeSpan SdCardDownloadTimeout => throw new NotSupportedException();
        public TimeSpan SdCardTransferIdleTimeout => throw new NotSupportedException();
        public void StopStreaming() => throw new NotSupportedException();
        public void StartStreaming() => throw new NotSupportedException();
        public IReadOnlyList<IChannel> SnapshotChannels() => throw new NotSupportedException();
        public long ChannelStateVersion => throw new NotSupportedException();
        public void WithChannelsLock(Action action) => throw new NotSupportedException();
        /// <summary>
        /// Runs the setup action so its sends land in <see cref="Calls"/> in order, then answers with
        /// <see cref="ExchangeResponse"/>. The <c>exchange</c> marker is recorded first so a test can
        /// see that the drain happened outside this exchange rather than inside it.
        /// </summary>
        public Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Action setupAction,
            int responseTimeoutMs = 1000,
            int completionTimeoutMs = 250,
            CancellationToken cancellationToken = default,
            Func<CancellationToken, Task>? prepareAsync = null,
            Func<Task>? finalizeAsync = null,
            bool keepBlankLines = false)
        {
            Assert.Null(prepareAsync);
            Assert.Null(finalizeAsync);

            LastResponseTimeoutMs = responseTimeoutMs;
            LastCompletionTimeoutMs = completionTimeoutMs;

            Calls.Add("exchange");
            setupAction();
            return Task.FromResult(ExchangeResponse);
        }

        public Task<IReadOnlyList<string>> DrainErrorQueueAsync(
            int maxIterations = 256,
            CancellationToken cancellationToken = default)
        {
            Calls.Add("drain:" + maxIterations);
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }
        public Task ExecuteRawCaptureAsync(
            Func<Stream, CancellationToken, Task> rawAction,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void EnsureSupported(DeviceFeature feature) => throw new NotSupportedException();
        public FeatureNotSupportedException CreateFeatureNotSupportedException(DeviceFeature feature)
            => throw new NotSupportedException();
        public void RaiseLowSdSpaceWarning(LowSdSpaceWarningEventArgs e) => throw new NotSupportedException();
        public void RaiseStreamFrameDiscarded(StreamFrameDiscardedEventArgs e) => throw new NotSupportedException();
        public void RaiseGapDetected(TimestampGapEventArgs e) => throw new NotSupportedException();
        public void RaiseRawStreamFrame(DaqifiOutMessage message) => throw new NotSupportedException();
        public void RaiseStreamDecodeFailure(Exception error) => throw new NotSupportedException();
    }
}
