using BenchmarkDotNet.Attributes;
using Daqifi.Core.Channel;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Google.Protobuf;

namespace Daqifi.Core.Benchmarks;

/// <summary>
/// The library's hot loop: a decoded <see cref="DaqifiOutMessage"/> frame going in and per-channel
/// <see cref="IChannel.SampleReceived"/> samples coming out.
/// </summary>
/// <remarks>
/// <para>
/// Measured through <c>OnStreamMessageReceived</c> on a real <see cref="DaqifiStreamingDevice"/>
/// rather than against the internal decoder directly, because that is the whole path a consumer
/// pays for: the channel-snapshot cache, timestamp reconstruction, gap detection, the analog and
/// digital unpacking, and the per-sample event dispatch. Nothing is stubbed except the transport,
/// which is absent — frames are injected the way the live-stream tests inject them.
/// </para>
/// <para>
/// Every case reports per <em>frame</em>, not per invocation
/// (<see cref="BenchmarkAttribute.OperationsPerInvoke"/>), so the numbers can be read directly
/// against a sample rate: at 1 kHz with 16 channels the device emits a frame every millisecond.
/// The allocation column is the one worth watching. Issue #490 removed per-frame allocation from
/// this path and #531 showed how badly a correctness test measures it; a regression here shows up
/// as bytes per frame climbing off zero.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class StreamDecodeBenchmarks
{
    /// <summary>
    /// Frames decoded per invocation. Large enough that the per-invocation overhead BenchmarkDotNet
    /// cannot subtract is negligible against the work, small enough that the pre-built frame array
    /// stays in cache-friendly territory.
    /// </summary>
    private const int FrameCount = 1_000;

    /// <summary>
    /// The enabled analog channel count. Sixteen is a fully populated Nyquist.
    /// </summary>
    private const int AnalogChannelCount = 16;

    /// <summary>
    /// The enabled digital channel count for the combined case. Sixteen DIO is a Nyquist, and it
    /// makes the digital payload two bytes, so the byte-and-bit indexing runs for real.
    /// </summary>
    private const int DigitalChannelCount = 16;

    /// <summary>
    /// Device-clock ticks between frames, at the 50 MHz timestamp frequency the hardware reports.
    /// 50,000 ticks is 1 ms — a 1 kHz stream, which is what the gap detector is fed here.
    /// </summary>
    private const uint TicksPerFrame = 50_000;

    private BenchmarkStreamingDevice _floatDevice = null!;
    private BenchmarkStreamingDevice _rawDevice = null!;
    private BenchmarkStreamingDevice _combinedDevice = null!;

    private DaqifiOutMessage[] _floatFrames = null!;
    private DaqifiOutMessage[] _rawFrames = null!;
    private DaqifiOutMessage[] _combinedFrames = null!;

    /// <summary>
    /// Sink for the subscriber below, so the JIT cannot decide the sample never escapes and elide
    /// the work that produced it.
    /// </summary>
    private double _sink;

    [GlobalSetup]
    public void Setup()
    {
        _floatFrames = BuildFrames(analogFloat: true, digital: false);
        _rawFrames = BuildFrames(analogFloat: false, digital: false);
        _combinedFrames = BuildFrames(analogFloat: true, digital: true);

        _floatDevice = CreateDevice(digitalPortCount: 0);
        _rawDevice = CreateDevice(digitalPortCount: 0);
        _combinedDevice = CreateDevice(digitalPortCount: DigitalChannelCount);
    }

    /// <summary>
    /// The common shape: the firmware's fast streaming encoder sends calibrated floats.
    /// </summary>
    [Benchmark(Baseline = true, OperationsPerInvoke = FrameCount)]
    public void DecodeAnalogFloatFrame() => Decode(_floatDevice, _floatFrames);

    /// <summary>
    /// The same frame carrying raw ADC counts instead, which routes each value through
    /// <see cref="IAnalogChannel.GetScaledValue"/> — the per-sample calibration arithmetic.
    /// </summary>
    [Benchmark(OperationsPerInvoke = FrameCount)]
    public void DecodeRawAnalogFrame() => Decode(_rawDevice, _rawFrames);

    /// <summary>
    /// Analog and digital in one frame, the shape a stream with DIO enabled produces.
    /// </summary>
    [Benchmark(OperationsPerInvoke = FrameCount)]
    public void DecodeCombinedFrame() => Decode(_combinedDevice, _combinedFrames);

    private static void Decode(BenchmarkStreamingDevice device, DaqifiOutMessage[] frames)
    {
        for (var i = 0; i < frames.Length; i++)
        {
            device.InjectStreamFrame(frames[i]);
        }
    }

    private BenchmarkStreamingDevice CreateDevice(int digitalPortCount)
    {
        var device = new BenchmarkStreamingDevice();
        device.Connect();
        device.PopulateChannelsFromStatus(StatusMessage(digitalPortCount));

        foreach (var channel in device.Channels)
        {
            channel.IsEnabled = true;

            // An honest consumer, not an empty one: a real subscriber reads the value, and the
            // per-sample event args are allocated for whoever is listening either way.
            channel.SampleReceived += (_, e) => _sink = e.Sample.Value;
        }

        device.StartStreaming();
        return device;
    }

    private static DaqifiOutMessage StatusMessage(int digitalPortCount)
    {
        var status = new DaqifiOutMessage
        {
            AnalogInPortNum = AnalogChannelCount,
            DigitalPortNum = (uint)digitalPortCount,
            AnalogInRes = 65535,
            TimestampFreq = 50_000_000,
        };

        for (var i = 0; i < AnalogChannelCount; i++)
        {
            status.AnalogInPortRange.Add(5.0f);
            status.AnalogInCalM.Add(1.0f);
            status.AnalogInCalB.Add(0.0f);
            status.AnalogInIntScaleM.Add(1.0f);
        }

        return status;
    }

    private static DaqifiOutMessage[] BuildFrames(bool analogFloat, bool digital)
    {
        var frames = new DaqifiOutMessage[FrameCount];

        for (var frame = 0; frame < FrameCount; frame++)
        {
            var message = new DaqifiOutMessage
            {
                // A real device's tick counter advances between frames, so the gap detector and the
                // timestamp reconstructor do their real work rather than seeing the same tick twice.
                MsgTimeStamp = (uint)(frame + 1) * TicksPerFrame,
            };

            for (var channel = 0; channel < AnalogChannelCount; channel++)
            {
                if (analogFloat)
                {
                    message.AnalogInDataFloat.Add(1.0f + channel * 0.01f);
                }
                else
                {
                    message.AnalogInData.Add(1_000 + channel);
                }
            }

            if (digital)
            {
                message.DigitalData = ByteString.CopyFrom(new[] { (byte)(frame & 0xFF), (byte)0x0F });
            }

            frames[frame] = message;
        }

        return frames;
    }

    /// <summary>
    /// A streaming device with no transport whose frames are injected directly, mirroring the
    /// double the live-stream tests use. <c>Send</c> is overridden because there is nothing to
    /// send to.
    /// </summary>
    private sealed class BenchmarkStreamingDevice() : DaqifiStreamingDevice("BenchmarkDevice")
    {
        public void InjectStreamFrame(DaqifiOutMessage message) => OnStreamMessageReceived(message);

        public override void Send<T>(IOutboundMessage<T> message) { /* no transport */ }
    }
}
