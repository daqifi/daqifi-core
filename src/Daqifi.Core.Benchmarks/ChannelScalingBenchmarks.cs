using BenchmarkDotNet.Attributes;
using Daqifi.Core.Channel;

namespace Daqifi.Core.Benchmarks;

/// <summary>
/// Per-sample device calibration (<see cref="AnalogChannel.GetScaledValue"/>) and transducer
/// scaling (<see cref="ChannelScaling.Apply"/>).
/// </summary>
/// <remarks>
/// Both run once per sample, so a validity check, unit lookup, or nullable coefficient here adds
/// a cost nothing else would notice. <see cref="GetScaledValue"/> is measured through the channel,
/// lock included: that lock stops a concurrent status refresh tearing the calibration coefficients,
/// so it is part of the sample cost rather than overhead to measure around.
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
