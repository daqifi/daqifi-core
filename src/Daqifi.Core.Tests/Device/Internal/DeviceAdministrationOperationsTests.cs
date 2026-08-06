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

    /// <summary>
    /// An <see cref="IDeviceOperationHost"/> that records, in order, the only two things this block
    /// is allowed to do to a device: send a command, and (for reboot) disconnect. Everything else
    /// throws.
    /// </summary>
    private sealed class FakeHost : IDeviceOperationHost
    {
        private int _sendCount;

        public List<string> Calls { get; } = new();

        public bool IsConnected { get; set; }

        public DeviceMetadata Metadata { get; } = new();

        /// <summary>1-based index of the send that should throw, or 0 for none.</summary>
        public int FailSendAt { get; set; }

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
        public IReadOnlyList<IChannel> SnapshotChannels() => throw new NotSupportedException();
        public void WithChannelsLock(Action action) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ExecuteTextCommandAsync(
            Action setupAction,
            int responseTimeoutMs = 1000,
            int completionTimeoutMs = 250,
            CancellationToken cancellationToken = default,
            Func<CancellationToken, Task>? prepareAsync = null,
            Func<Task>? finalizeAsync = null) => throw new NotSupportedException();
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
