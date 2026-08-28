using BenchmarkDotNet.Attributes;
using Daqifi.Core.Channel;

namespace Daqifi.Core.Benchmarks;

/// <summary>
/// The two per-sample conversions: the device's calibration
/// (<see cref="AnalogChannel.GetScaledValue"/>, raw ADC count to volts) and the user's transducer
/// transform (<see cref="ChannelScaling.Apply"/>, volts to engineering units).
/// </summary>
/// <remarks>
/// <para>
/// Both run once per sample per channel, so at 16 channels and 1 kHz they run 16,000 times a
/// second. Neither should allocate and both should be a handful of nanoseconds; the reason to
/// measure them is that they are the easiest place in the library for a well-meaning change — a
/// validity check, a unit lookup, a nullable coefficient — to add a per-sample cost that nothing
/// else would notice.
/// </para>
/// <para>
/// <see cref="GetScaledValue"/> is measured through the channel, lock included: that lock is what
/// stops a concurrent status refresh tearing the calibration coefficients, so it is part of what a
/// sample costs and not an overhead to be measured around.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class ChannelScalingBenchmarks
{
    /// <summary>
    /// Conversions per invocation, so the reported figure is per sample.
    /// </summary>
    private const int SampleCount = 1_000;

    private readonly AnalogChannel _channel = new(0);

    private readonly ChannelScaling _transducerScaling = new(gain: 12.5, offset: -1.25, unit: "PSI");

    private readonly ChannelScaling _identityScaling = ChannelScaling.Identity;

    /// <summary>
    /// Raw ADC counts, pre-generated so the loop measures conversion rather than number generation.
    /// </summary>
    private int[] _rawValues = null!;

    private double[] _volts = null!;

    [GlobalSetup]
    public void Setup()
    {
        _rawValues = new int[SampleCount];
        _volts = new double[SampleCount];

        for (var i = 0; i < SampleCount; i++)
        {
            _rawValues[i] = i * 37 % 65_536;
            _volts[i] = _rawValues[i] / 65_535.0 * 5.0;
        }
    }

    /// <summary>
    /// The device calibration as a consumer reaches it, through the channel and its lock.
    /// </summary>
    [Benchmark(Baseline = true, OperationsPerInvoke = SampleCount)]
    public double GetScaledValue()
    {
        var total = 0.0;
        for (var i = 0; i < _rawValues.Length; i++)
        {
            total += _channel.GetScaledValue(_rawValues[i]);
        }

        return total;
    }

    /// <summary>
    /// A configured transducer transform: gain, offset, and the finiteness guard around them.
    /// </summary>
    [Benchmark(OperationsPerInvoke = SampleCount)]
    public double ApplyTransducerScaling()
    {
        var total = 0.0;
        for (var i = 0; i < _volts.Length; i++)
        {
            total += _transducerScaling.Apply(_volts[i]);
        }

        return total;
    }

    /// <summary>
    /// The far commoner case — no transducer configured — which every stream pays on every sample.
    /// </summary>
    [Benchmark(OperationsPerInvoke = SampleCount)]
    public double ApplyIdentityScaling()
    {
        var total = 0.0;
        for (var i = 0; i < _volts.Length; i++)
        {
            total += _identityScaling.Apply(_volts[i]);
        }

        return total;
    }
}
