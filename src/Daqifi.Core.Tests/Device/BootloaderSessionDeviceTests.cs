using System.Reflection;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device;
using Daqifi.Core.Firmware;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Covers <see cref="BootloaderSessionDevice"/>: the three behaviours the PIC32 update flow
/// depends on, the no-op contract for everything else, and a reflection sweep that keeps the type
/// honest as <see cref="IStreamingDevice"/> grows (daqifi-core#477).
/// </summary>
public class BootloaderSessionDeviceTests
{
    #region Contract required by the PIC32 update flow

    [Fact]
    public void FreshInstance_SatisfiesEnsureDeviceConnected()
    {
        var device = new BootloaderSessionDevice();

        // The real guard the update flow opens with. A stand-in that reported IsConnected == false
        // would throw here and fail the update before it started.
        var exception = Record.Exception(() => FirmwareUpdateContext.EnsureDeviceConnected(device));

        Assert.Null(exception);
    }

    [Fact]
    public void AfterDisconnect_FailsEnsureDeviceConnected()
    {
        var device = new BootloaderSessionDevice();
        device.Disconnect();

        // The mirror of the above: once the flow has torn the session down, the guard must reject
        // it, so a second update attempt on a spent instance surfaces as a clear error.
        Assert.Throws<InvalidOperationException>(() => FirmwareUpdateContext.EnsureDeviceConnected(device));
    }

    [Fact]
    public void IsStreaming_IsAlwaysFalse()
    {
        // The flow only calls StopStreaming() when this is true.
        Assert.False(new BootloaderSessionDevice().IsStreaming);
    }

    [Fact]
    public void Send_DiscardsSilently()
    {
        var device = new BootloaderSessionDevice();

        // The flow sends this unconditionally; throwing would abort a valid update.
        var exception = Record.Exception(() => device.Send(ScpiMessageProducer.ForceBootloader));

        Assert.Null(exception);
    }

    #endregion

    #region Connection state

    [Fact]
    public void NewInstance_IsConnected()
    {
        var device = new BootloaderSessionDevice();

        Assert.True(device.IsConnected);
        Assert.Equal(ConnectionStatus.Connected, device.Status);
    }

    [Fact]
    public void Disconnect_TransitionsAndRaisesStatusChangedOnce()
    {
        var device = new BootloaderSessionDevice();
        var statuses = new List<ConnectionStatus>();
        device.StatusChanged += (_, e) => statuses.Add(e.Status);

        device.Disconnect();

        Assert.False(device.IsConnected);
        Assert.Equal(ConnectionStatus.Disconnected, device.Status);
        Assert.Equal([ConnectionStatus.Disconnected], statuses);
    }

    [Fact]
    public void Disconnect_WhenAlreadyDisconnected_DoesNotRaiseAgain()
    {
        var device = new BootloaderSessionDevice();
        device.Disconnect();

        var raised = 0;
        device.StatusChanged += (_, _) => raised++;
        device.Disconnect();

        Assert.Equal(0, raised);
    }

    [Fact]
    public void Connect_WhenAlreadyConnected_DoesNotRaise()
    {
        var device = new BootloaderSessionDevice();
        var raised = 0;
        device.StatusChanged += (_, _) => raised++;

        device.Connect();

        Assert.True(device.IsConnected);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void Reconnect_AfterDisconnect_RaisesConnected()
    {
        var device = new BootloaderSessionDevice();
        device.Disconnect();

        var statuses = new List<ConnectionStatus>();
        device.StatusChanged += (_, e) => statuses.Add(e.Status);
        device.Connect();

        Assert.True(device.IsConnected);
        Assert.Equal([ConnectionStatus.Connected], statuses);
    }

    [Fact]
    public async Task DisposeAsync_DisconnectsAndIsIdempotent()
    {
        var device = new BootloaderSessionDevice();
        var raised = 0;
        device.StatusChanged += (_, _) => raised++;

        await device.DisposeAsync();
        await device.DisposeAsync();

        Assert.False(device.IsConnected);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task ConcurrentDisconnects_RaiseStatusChangedExactlyOnce()
    {
        var device = new BootloaderSessionDevice();
        var raised = 0;
        device.StatusChanged += (_, _) => Interlocked.Increment(ref raised);

        // A dialog's teardown can race the update flow's own Disconnect(). Only one of them
        // performed the transition, so only one notification is owed.
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(device.Disconnect)));

        Assert.False(device.IsConnected);
        Assert.Equal(1, Volatile.Read(ref raised));
    }

    #endregion

    #region Naming

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Name_FallsBackToDefault_WhenNotSupplied(string? name)
    {
        Assert.Equal(BootloaderSessionDevice.DefaultName, new BootloaderSessionDevice(name).Name);
    }

    [Fact]
    public void Name_UsesSuppliedValue()
    {
        Assert.Equal("Nyquist 1", new BootloaderSessionDevice("Nyquist 1").Name);
    }

    #endregion

    #region Channels, metadata, and defaults

    [Fact]
    public void Channels_AndSnapshot_AreEmpty()
    {
        var device = new BootloaderSessionDevice();

        Assert.Empty(device.Channels);
        Assert.Empty(device.GetChannelsSnapshot());
    }

    [Fact]
    public void Metadata_IsNonNull()
    {
        // Callers read this without a guard, so it must never be null.
        Assert.NotNull(new BootloaderSessionDevice().Metadata);
    }

    [Fact]
    public void IpAddress_IsNull()
    {
        Assert.Null(new BootloaderSessionDevice().IpAddress);
    }

    [Fact]
    public void PwmFrequencyHz_ReportsTheCommandableDefault()
    {
        // Not 0: the interface documents this as a commandable frequency, and 0 is not one.
        Assert.Equal(DaqifiStreamingDevice.DefaultPwmFrequencyHz, new BootloaderSessionDevice().PwmFrequencyHz);
    }

    [Fact]
    public void StreamingFrequency_RoundTrips()
    {
        var device = new BootloaderSessionDevice { StreamingFrequency = 42 };

        Assert.Equal(42, device.StreamingFrequency);
    }

    #endregion

    #region Cancellation

    [Fact]
    public async Task AsyncMembers_HonorAnAlreadyCancelledToken()
    {
        var device = new BootloaderSessionDevice();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var token = cts.Token;

        var calls = new Func<Task>[]
        {
            () => device.ConnectAsync(token),
            () => device.DisconnectAsync(token),
            () => device.SaveAdcCalibrationAsync(token),
            () => device.LoadAdcCalibrationAsync(token),
            () => device.SetAdcCalibrationSlopeAsync(0, 1.0, token),
            () => device.SetAdcCalibrationOffsetAsync(0, 1.0, token),
            () => device.SaveFactoryAdcCalibrationAsync(token),
            () => device.LoadFactoryAdcCalibrationAsync(token),
            () => device.UseAdcCalibrationAsync(0, token),
            () => device.SaveVoltagePrecisionAsync(token),
            () => device.LoadVoltagePrecisionAsync(token),
        };

        foreach (var call in calls)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(call);
        }
    }

    [Fact]
    public async Task ConnectAsync_WithCancelledToken_LeavesStateUntouched()
    {
        var device = new BootloaderSessionDevice();
        device.Disconnect();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => device.ConnectAsync(cts.Token));

        Assert.False(device.IsConnected);
    }

    #endregion

    #region Future-widening guard

    /// <summary>
    /// Invokes every member of the <see cref="IStreamingDevice"/> surface — including the members it
    /// inherits — and asserts none of them throws.
    /// </summary>
    /// <remarks>
    /// This is the test that earns the type its place in Core. A member added to
    /// <see cref="IStreamingDevice"/> later must be implemented here as a no-op like the rest; if
    /// someone instead leaves it throwing <see cref="NotImplementedException"/>, this fails in
    /// Core's own CI rather than in a downstream app's firmware-update dialog.
    /// </remarks>
    [Fact]
    public async Task EveryInterfaceMember_IsInvokableWithoutThrowing()
    {
        var device = new BootloaderSessionDevice();
        var surface = new[] { typeof(IStreamingDevice) }
            .Concat(typeof(IStreamingDevice).GetInterfaces())
            .SelectMany(i => i.GetMethods())
            .ToList();

        // Guards the sweep itself: if the reflection walk silently stopped finding members, the
        // test would pass while covering nothing.
        Assert.True(surface.Count > 50, $"Expected the interface surface to be large; found {surface.Count}.");

        foreach (var method in surface)
        {
            var target = method.IsGenericMethodDefinition
                ? method.MakeGenericMethod(typeof(string))
                : method;

            var args = target.GetParameters()
                .Select(p => p.ParameterType.IsValueType
                    ? Activator.CreateInstance(p.ParameterType)
                    : null)
                .ToArray();

            object? result;
            try
            {
                result = target.Invoke(device, args);
            }
            catch (TargetInvocationException ex)
            {
                throw new Xunit.Sdk.XunitException(
                    $"{target.Name} threw {ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}");
            }

            // Observe returned tasks so a faulted one is not silently dropped.
            switch (result)
            {
                case Task task:
                    await task;
                    break;
                case ValueTask valueTask:
                    await valueTask;
                    break;
            }
        }
    }

    #endregion
}
