using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Capabilities;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// A channel command held back behind another caller's operation and then dropped from the backlog
/// must not leave Core showing state the device was never sent (issue #1034).
/// </summary>
public class ChannelCommandDroppedTests
{
    public static TheoryData<string> Commands() => new(Cases.Keys);

    /// <summary>A command, and how to read the state it changes.</summary>
    private static readonly Dictionary<string, (Action<HeldDevice> Send, Func<HeldDevice, object?> State)> Cases = new()
    {
        ["enable analog"] = (d => d.EnableChannel(d.Analog(1)), d => d.Enabled),
        ["disable analog"] = (d => d.DisableChannel(d.Analog(0)), d => d.Enabled),
        ["enable digital"] = (d => d.EnableChannel(d.Digital(3)), d => d.Enabled),
        ["enable both"] = (d => d.EnableChannels([d.Analog(1), d.Digital(3)]), d => d.Enabled),
        ["disable all"] = (d => d.DisableAllChannels(), d => d.Enabled),
        ["direction"] = (d => d.SetDioDirection(d.Digital(3), ChannelDirection.Output), d => d.Digital(3).Direction),
        ["PWM on"] = (d => d.SetPwmEnabled(d.Digital(3), true), d => d.Digital(3).Mode),
        ["PWM off"] = (d => d.SetPwmEnabled(d.Digital(4), false), d => d.Digital(4).Mode),
        ["duty cycle"] = (d => d.SetPwmDutyCycle(d.Digital(3), 30), d => d.Digital(3).PwmDutyCyclePercent),
        ["frequency"] = (d => d.SetPwmFrequency(2000), d => d.PwmFrequencyHz),
        ["analog output"] = (d => d.SetAnalogOutput(d.Output, 2.5), d => (d.Output.PendingVoltage, d.Output.OutputVoltage)),
    };

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task Dropped_LeavesTheStateTheDeviceHas(string command)
    {
        using var device = await HeldDevice.CreateAsync();
        var (send, state) = Cases[command];
        var before = state(device);

        await device.WhileHeldAsync(() =>
        {
            send(device);
            device.DropWhatIsHeldBack();
        });

        Assert.Equal(before, state(device));
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task HeldBackAndSent_IsRecorded(string command)
    {
        using var device = await HeldDevice.CreateAsync();
        var (send, state) = Cases[command];
        var before = state(device);

        await device.WhileHeldAsync(() => send(device));

        Assert.NotEqual(before, state(device));
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public async Task HeldBackWhenTheDeviceIsDisconnected_LeavesTheStateTheDeviceHad(string command)
    {
        if (command == "PWM off")
        {
            // A disconnect turns PWM off whatever was commanded (#945).
            return;
        }

        using var device = await HeldDevice.CreateAsync();
        var (send, state) = Cases[command];
        var before = state(device);

        await device.WhileHeldAsync(() =>
        {
            send(device);
            device.DisconnectNow();
        });

        Assert.Equal(before, state(device));
    }

    [Fact]
    public async Task HeldBackPwmOff_WhenTheDeviceIsDisconnected_LeavesPwmOff()
    {
        using var device = await HeldDevice.CreateAsync();

        await device.WhileHeldAsync(() =>
        {
            device.SetPwmEnabled(device.Digital(4), false);
            device.DisconnectNow();
        });

        Assert.False(device.Digital(4).IsPwmEnabled);
    }

    [Fact]
    public async Task TwoDropped_GoBackToWhatTheDeviceHas()
    {
        // Not to what the first one recorded: that never went out either.
        using var device = await HeldDevice.CreateAsync();

        await device.WhileHeldAsync(() =>
        {
            device.EnableChannel(device.Analog(1));
            device.EnableChannel(device.Analog(2));
            device.DropWhatIsHeldBack();
        });

        Assert.Equal("AI0", device.Enabled);
    }

    [Fact]
    public async Task OnlyTheFirstOfTwoDropped_KeepsTheSecond()
    {
        // The second mask names the first one's channel too.
        using var device = await HeldDevice.CreateAsync(backlogCap: 1);

        await device.WhileHeldAsync(() =>
        {
            device.EnableChannel(device.Analog(1));
            device.EnableChannel(device.Analog(2));
        });

        Assert.Equal("AI0 AI1 AI2", device.Enabled);
    }

    [Fact]
    public async Task Dropped_AfterSomethingElseMovedTheState_LeavesThat()
    {
        using var device = await HeldDevice.CreateAsync();

        await device.WhileHeldAsync(() =>
        {
            device.EnableChannel(device.Analog(1));
            device.PopulateChannelsFromStatus(HeldDevice.Status(analogEnabled: 0b100));
            device.DropWhatIsHeldBack();
        });

        Assert.Equal("AI2", device.Enabled);
    }

    [Fact]
    public async Task NotSentAtAll_LeavesTheStateTheDeviceHas()
    {
        using var device = await HeldDevice.CreateAsync();
        device.FailNextSend = true;

        Assert.Throws<IOException>(() => device.EnableChannel(device.Analog(1)));

        Assert.Equal("AI0", device.Enabled);
    }

    [Fact]
    public async Task ALatchedOutput_RaisesItsSampleWhenTheLatchGoesOut()
    {
        using var device = await HeldDevice.CreateAsync();
        var samples = new List<double>();
        device.Output.SampleReceived += (_, e) => samples.Add(e.Sample.Value);

        await device.WhileHeldAsync(() =>
        {
            device.SetAnalogOutput(device.Output, 2.5);
            Assert.Empty(samples);
        });

        Assert.Equal([2.5], samples);
    }

    /// <summary>
    /// A connected device with analog channel 0 enabled and PWM running on digital channel 4, whose
    /// operation lock a test can hold.
    /// </summary>
    private sealed class HeldDevice : DaqifiStreamingDevice
    {
        private readonly int _backlogCap;

        private HeldDevice(int backlogCap)
            : base("Held", new NullTransport())
        {
            _backlogCap = backlogCap;
        }

        internal override int MaxDeferredSends => _backlogCap;

        public bool FailNextSend { get; set; }

        public IAnalogOutputChannel Output => Channels.OfType<IAnalogOutputChannel>().Single();

        /// <summary>The names of the enabled channels.</summary>
        public string Enabled => string.Join(' ', Channels.Where(c => c.IsEnabled).Select(c => c.Name));

        public IAnalogChannel Analog(int number) =>
            Channels.OfType<IAnalogChannel>().Single(c => c.ChannelNumber == number);

        public IDigitalChannel Digital(int number) =>
            Channels.OfType<IDigitalChannel>().Single(c => c.ChannelNumber == number);

        public static DaqifiOutMessage Status(byte analogEnabled) => new()
        {
            AnalogInPortNum = 4,
            AnalogInRes = 65535,
            AnalogInPortEnabled = Google.Protobuf.ByteString.CopyFrom(analogEnabled),
            DigitalPortNum = 8,
        };

        public static async Task<HeldDevice> CreateAsync(int backlogCap = 4)
        {
            Assert.True(CapabilityDocumentParser.TryParse(
                """
                {"schema_version":2,"channels":[
                  {"id":0,"kind":"analog-output","unit":"V","resolution_bits":12,"ranges":[{"min":0.000,"max":10.000}],"extensions":{}}
                ]}
                """,
                out var document));

            var device = new HeldDevice(backlogCap);
            await device.ConnectAsync();
            device.UpdateMetadata(metadata => metadata.WithCapabilityDocument(document!));
            device.SyncAnalogOutputChannelsFromCapabilities();
            device.PopulateChannelsFromStatus(Status(analogEnabled: 0b001));
            device.SetPwmEnabled(device.Digital(4), true);
            return device;
        }

        /// <summary>Runs <paramref name="action"/> while an operation on another flow has the device.</summary>
        public async Task WhileHeldAsync(Action action)
        {
            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var operation = Task.Run(() => RunExclusiveAsync(async _ =>
            {
                held.SetResult();
                await release.Task;
            }));

            await held.Task;
            if (!Task.Run(action).Wait(TimeSpan.FromSeconds(20)))
            {
                Assert.Fail("The action did not return.");
            }

            release.SetResult();
            await operation;
        }

        /// <summary>Disconnects without waiting for the operation that has the device.</summary>
        public void DisconnectNow() => DisconnectAsync(new CancellationToken(canceled: true)).GetAwaiter().GetResult();

        /// <summary>Fills the backlog until none of what was held back is left in it.</summary>
        public void DropWhatIsHeldBack()
        {
            for (var i = 0; i < _backlogCap; i++)
            {
                Send(new ScpiMessage("FILLER"));
            }
        }

        public override void Send<T>(IOutboundMessage<T> message)
        {
            if (FailNextSend)
            {
                FailNextSend = false;
                throw new IOException("gone");
            }

            base.Send(message);
        }
    }

    private sealed class NullTransport : IStreamTransport
    {
        public bool IsUsbConnection => false;

        public Stream Stream { get; } = Stream.Null;

        public bool IsConnected { get; private set; }

        public string ConnectionInfo => "Null";

        public event EventHandler<TransportStatusEventArgs>? StatusChanged;

        public Task ConnectAsync(ConnectionRetryOptions? retryOptions = null, CancellationToken cancellationToken = default)
        {
            IsConnected = true;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(true, ConnectionInfo));
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = false;
            StatusChanged?.Invoke(this, new TransportStatusEventArgs(false, ConnectionInfo));
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }
}
