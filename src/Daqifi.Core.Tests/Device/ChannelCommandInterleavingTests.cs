using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Internal;

namespace Daqifi.Core.Tests.Device;

/// <summary>
/// Runs analog enables, disables and raw masks through every short interleaving of an operation's
/// own commands, held-back ones, evictions, failed sends, status reports and replays, and checks the
/// mask the device ended up with and the channels Core shows as enabled against <see cref="Model"/>.
/// </summary>
public class ChannelCommandInterleavingTests
{
    private const int ChannelCount = 4;

    /// <summary>What a command does to the channels, as a mask, and how it is sent.</summary>
    private sealed record Command(string Name, Func<uint, uint> Change, Action<Device> Send, uint? Raw = null);

    private static readonly Command[] Commands =
    [
        new("+1", on => on | 0b0010, d => d.EnableChannel(d.Analog(1))),
        new("+2", on => on | 0b0100, d => d.EnableChannel(d.Analog(2))),
        new("-0", on => on & ~0b0001u, d => d.DisableChannel(d.Analog(0))),
        new("-1", on => on & ~0b0010u, d => d.DisableChannel(d.Analog(1))),
        new("none", _ => 0, d => d.DisableAllChannels()),
        new("=12", _ => 0b0110, d => d.Send(ScpiMessageProducer.EnableAdcChannels(0b0110)), Raw: 0b0110),
        new("=3", _ => 0b1000, d => d.Send(ScpiMessageProducer.EnableAdcChannels(0b1000)), Raw: 0b1000),
    ];

    /// <summary>
    /// What the device has, and what it will have: each command changes both in the order the calls
    /// are made, except that one held back changes the first only when it is replayed.
    /// </summary>
    private sealed class Model
    {
        private readonly List<Command> _held = [];
        private uint _requested = 0b0001;

        public uint OnDevice { get; private set; } = 0b0001;

        public void FromOwner(Command command)
        {
            OnDevice = command.Change(OnDevice);
            _requested = _held.Count > 0 ? command.Change(_requested) : OnDevice;
        }

        public void HeldBack(Command command)
        {
            _requested = command.Change(_requested);
            _held.Add(command);
        }

        public void EvictOldest()
        {
            if (_held.Count > 0)
            {
                _held.RemoveAt(0);
                if (_held.Count == 0)
                {
                    // Nothing is left of what was requested.
                    _requested = OnDevice;
                }
            }
        }

        public void Replay()
        {
            if (_held.Count > 0)
            {
                // A raw mask goes out as it was written, whatever was sent ahead of it.
                OnDevice = _requested = _held[^1].Raw ?? _requested;
                _held.Clear();
            }
        }
    }

    private sealed record Step(string Name, Action<Rig, Model> Run, bool NeedsOwner = false);

    private static readonly Step[] Steps =
    [
        .. Commands.Select(c => new Step($"held {c.Name}", (rig, model) =>
        {
            c.Send(rig.Device);
            rig.Backlog.Add(true);
            model.HeldBack(c);
        })),
        .. Commands.Select(c => new Step($"owner {c.Name}", (rig, model) =>
        {
            rig.AsOwner(() => c.Send(rig.Device));
            model.FromOwner(c);
        }, NeedsOwner: true)),
        new("evict", (rig, model) =>
        {
            rig.EvictOldest();
            model.EvictOldest();
        }),
        new("held +3 fails", (rig, _) => rig.SendFailing()),
        new("owner +3 fails", (rig, _) => rig.AsOwner(rig.SendFailing), NeedsOwner: true),
        new("status", (rig, model) => rig.Device.PopulateChannelsFromStatus(Status(model.OnDevice))),
        new("replay", (rig, model) =>
        {
            rig.Replay();
            rig.Hold();
            model.Replay();
        }),
        new("replay so far", (rig, model) =>
        {
            rig.ReplaySoFar();
            model.Replay();
        }, NeedsOwner: true),
    ];

    [Fact]
    public async Task EveryInterleavingOfThree()
        => await CheckAsync(Enumerable.Range(0, Steps.Length * Steps.Length * Steps.Length)
            .Select(n => new[] { n % Steps.Length, n / Steps.Length % Steps.Length, n / Steps.Length / Steps.Length }));

    [Fact]
    public async Task RandomLongerInterleavings()
    {
        var random = new Random(1034);
        await CheckAsync(Enumerable.Range(0, 4000)
            .Select(_ => Enumerable.Range(0, random.Next(4, 11)).Select(_ => random.Next(Steps.Length)).ToArray())
            .ToArray());
    }

    private static async Task CheckAsync(IEnumerable<int[]> scenarios)
    {
        using var device = new Device(new RecordingTransport());
        await device.ConnectAsync();
        device.PopulateChannelsFromStatus(Status(0b0001));

        var failures = new List<string>();
        var checkedCount = 0;
        foreach (var scenario in scenarios)
        {
            var steps = scenario.Select(index => Steps[index]).ToArray();
            device.Send(ScpiMessageProducer.EnableAdcChannels(0b0001));
            var rig = new Rig(device);
            var model = new Model();
            rig.Hold();
            var possible = true;
            foreach (var step in steps)
            {
                if (step.NeedsOwner && !rig.HasOwner)
                {
                    possible = false;
                    break;
                }

                step.Run(rig, model);
                Check("then", model.OnDevice);
            }

            rig.Replay();
            model.Replay();
            if (!possible)
            {
                continue;
            }

            checkedCount++;
            Check("at the end", model.OnDevice);

            // What the next command builds on.
            var last = Commands[1];
            last.Send(device);
            model.FromOwner(last);
            Check("and after another", model.OnDevice);

            void Check(string when, uint expected)
            {
                var onDevice = device.MaskOnDevice();
                if (onDevice != expected || device.EnabledMask != expected)
                {
                    failures.Add(
                        $"{string.Join(", ", steps.Select(s => s.Name))} — {when}: expected {Convert.ToString(expected, 2)}, "
                        + $"device {Convert.ToString(onDevice, 2)}, shown {Convert.ToString(device.EnabledMask, 2)}");
                }
            }
        }

        Assert.True(checkedCount > 0);
        Assert.True(failures.Count == 0, $"{failures.Count} of {checkedCount}:\n{string.Join('\n', failures.Take(20))}");
    }

    private static DaqifiOutMessage Status(uint enabled) => new()
    {
        AnalogInPortNum = ChannelCount,
        AnalogInRes = 65535,
        AnalogInPortEnabled = Google.Protobuf.ByteString.CopyFrom((byte)enabled),
    };

    /// <summary>The operation that has the device, and what is held back behind it.</summary>
    private sealed class Rig(Device device)
    {
        private BlockingCollection<Action>? _ownerActions;
        private Task? _operation;
        private ReplayGate? _gate;

        public Device Device => device;

        /// <summary>What is held back: whether each is a command, or a filler that evicted one.</summary>
        public List<bool> Backlog { get; } = [];

        public bool HasOwner => _ownerActions != null;

        public void Hold()
        {
            var actions = _ownerActions = new BlockingCollection<Action>();
            var held = new ManualResetEventSlim();
            _operation = Task.Factory.StartNew(
                () => device.RunExclusiveAsync(_ =>
                {
                    held.Set();
                    foreach (var action in actions.GetConsumingEnumerable())
                    {
                        action();
                    }

                    return Task.CompletedTask;
                }).GetAwaiter().GetResult(),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            Wait(held);
            Backlog.Clear();
        }

        /// <summary>Runs an action in the operation.</summary>
        public void AsOwner(Action action)
        {
            var done = new ManualResetEventSlim();
            Exception? thrown = null;
            _ownerActions!.Add(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    thrown = ex;
                }

                done.Set();
            });
            Wait(done);
            Assert.Null(thrown);
        }

        public void EvictOldest()
        {
            var oldest = Backlog.IndexOf(true);
            if (oldest < 0)
            {
                return;
            }

            // One more than is left once the oldest has gone, which is where the filler goes.
            device.Cap = Backlog.Count - oldest;
            device.Send(new ScpiMessage("FILLER"));
            device.Cap = DaqifiDevice.DefaultMaxDeferredSends;
            Backlog.RemoveRange(0, oldest + 1);
            Backlog.Add(false);
        }

        public void SendFailing()
        {
            device.FailNextSend = true;
            Assert.Throws<IOException>(() => device.EnableChannel(device.Analog(3)));
        }

        /// <summary>Ends the operation, and stops replaying where the backlog ends now.</summary>
        public void ReplaySoFar()
        {
            _gate = device.ParkGate();
            _ownerActions!.CompleteAdding();
            _ownerActions = null;
            Wait(_gate.Reached);
            Backlog.Clear();
        }

        /// <summary>Ends the operation and replays everything.</summary>
        public void Replay()
        {
            _ownerActions?.CompleteAdding();
            _ownerActions = null;
            _gate?.Open.Set();
            _gate = null;
            Assert.True(_operation!.Wait(TimeSpan.FromSeconds(20)));
        }

        private static void Wait(ManualResetEventSlim signal) => Assert.True(signal.Wait(TimeSpan.FromSeconds(20)));
    }

    /// <summary>A held-back message whose replay waits.</summary>
    private sealed class ReplayGate : ITrackedSend
    {
        public ManualResetEventSlim Reached { get; } = new();

        public ManualResetEventSlim Open { get; } = new();

        public void GoOut(Action send) => throw new InvalidOperationException("It was not held back.");

        public void Replay(Action send)
        {
            Reached.Set();
            Open.Wait(TimeSpan.FromSeconds(20));
        }

        public void NotSent(Exception? reason) => Reached.Set();
    }

    private sealed class Device(RecordingTransport wire) : DaqifiStreamingDevice("Interleaved", wire)
    {
        private int _markers;

        internal override int MaxDeferredSends => Cap;

        public int Cap { get; set; } = DefaultMaxDeferredSends;

        public bool FailNextSend { get; set; }

        public uint EnabledMask => Channels.Where(c => c.Type == ChannelType.Analog && c.IsEnabled)
            .Aggregate(0u, (mask, c) => mask | 1u << c.ChannelNumber);

        public IAnalogChannel Analog(int number) =>
            Channels.OfType<IAnalogChannel>().Single(c => c.ChannelNumber == number);

        public ReplayGate ParkGate()
        {
            var gate = new ReplayGate();
            Assert.True(SendOrHoldBack(new ScpiMessage("GATE"), gate));
            return gate;
        }

        /// <summary>The mask of the last command that reached the wire.</summary>
        public uint MaskOnDevice()
        {
            var marker = $"MARKER {_markers++}";
            ((IOperationSerializationHost)this).SendNow(new ScpiMessage(marker));
            return wire.LastMaskBefore(marker);
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

    /// <summary>Keeps the last ADC mask written, in place of a device.</summary>
    private sealed class RecordingTransport : Stream, IStreamTransport
    {
        private readonly BlockingCollection<(string Marker, uint Mask)> _markers = [];
        private uint _mask;

        public uint LastMaskBefore(string marker)
        {
            Assert.True(_markers.TryTake(out var reached, TimeSpan.FromSeconds(20)), "The marker was not written.");
            Assert.Equal(marker, reached.Marker);
            return reached.Mask;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            var command = Encoding.ASCII.GetString(buffer, offset, count).Trim();
            var mask = ScpiMessageProducer.EnableAdcChannelsCommand + " ";
            if (command.StartsWith(mask, StringComparison.Ordinal))
            {
                _mask = uint.Parse(command[mask.Length..], CultureInfo.InvariantCulture);
            }
            else if (command.StartsWith("MARKER", StringComparison.Ordinal))
            {
                _markers.Add((command, _mask));
            }
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => 0;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public bool IsUsbConnection => false;

        public Stream Stream => this;

        public bool IsConnected { get; private set; }

        public string ConnectionInfo => "Recording";

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
    }
}
