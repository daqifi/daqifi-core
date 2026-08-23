namespace Daqifi.Core.Channel;

/// <summary>
/// An analog-output (DAC) channel on a DAQiFi device. Populated from the device's capability
/// document, which is the only place the firmware describes its DAC channels — the protobuf status
/// message declares the fields but never fills them in.
/// </summary>
/// <remarks>
/// A write is a two-step device operation: staging a voltage records it on the device without
/// changing the pin, and a latch applies every staged channel at once. This class models both, so
/// <see cref="OutputVoltage"/> only ever reports a voltage the hardware is actually driving and
/// <see cref="PendingVoltage"/> reports one that is waiting.
/// </remarks>
public sealed class AnalogOutputChannel : IAnalogOutputChannel
{
    /// <summary>
    /// The DAC resolution assumed when the device states none: 12 bits, the DAC7718 fitted to NQ3.
    /// </summary>
    public const int DefaultResolutionBits = 12;

    /// <summary>The lowest voltage assumed when the device states no range.</summary>
    public const double DefaultMinimumVoltage = 0.0;

    /// <summary>
    /// The highest voltage assumed when the device states no range — 10 V, the DAC7718's hardware
    /// full scale on NQ3.
    /// </summary>
    public const double DefaultMaximumVoltage = 10.0;

    /// <summary>Smallest resolution accepted as physically plausible, in bits.</summary>
    public const int MinResolutionBits = 1;

    /// <summary>Largest resolution accepted as physically plausible, in bits.</summary>
    public const int MaxResolutionBits = 32;

    /// <summary>
    /// Largest absolute voltage accepted as a range endpoint. DAQiFi analog output tops out at
    /// 10 V; 50 leaves headroom for future hardware while still rejecting a corrupt value.
    /// </summary>
    public const double MaxRangeMagnitudeVolts = 50.0;

    private readonly object _lock = new();
    private IDataSample? _activeSample;
    private string _name;
    private bool _isEnabled;
    private int _resolutionBits;
    private double _minimumVoltage;
    private double _maximumVoltage;
    private bool _rangeIsAssumed;
    private double? _pendingVoltage;

    /// <inheritdoc />
    public int ChannelNumber { get; }

    /// <inheritdoc />
    public string Name
    {
        get { lock (_lock) { return _name; } }
        set { lock (_lock) { _name = value; } }
    }

    /// <summary>
    /// Gets or sets whether the channel is enabled. Carried for <see cref="IChannel"/>
    /// compatibility only — an analog output is driven by writing a voltage to it, and nothing in
    /// the acquisition path (the ADC enable bitmask, the stream decoder, the sample-rate model)
    /// consults this flag for an output channel.
    /// </summary>
    public bool IsEnabled
    {
        get { lock (_lock) { return _isEnabled; } }
        set { lock (_lock) { _isEnabled = value; } }
    }

    /// <summary>Gets the channel type (always <see cref="ChannelType.AnalogOutput"/>).</summary>
    public ChannelType Type => ChannelType.AnalogOutput;

    /// <summary>
    /// Gets the channel direction. Always <see cref="ChannelDirection.Output"/>; assigning
    /// anything else throws, because the direction of a DAC channel is a fact about the hardware
    /// rather than a setting.
    /// </summary>
    /// <exception cref="ArgumentException">A direction other than Output was assigned.</exception>
    public ChannelDirection Direction
    {
        get => ChannelDirection.Output;
        set
        {
            if (value != ChannelDirection.Output)
            {
                throw new ArgumentException(
                    $"An analog output channel is always {nameof(ChannelDirection.Output)}; it cannot be set to {value}.",
                    nameof(value));
            }
        }
    }

    /// <inheritdoc />
    public IDataSample? ActiveSample
    {
        get { lock (_lock) { return _activeSample; } }
    }

    /// <inheritdoc />
    public int ResolutionBits
    {
        get { lock (_lock) { return _resolutionBits; } }
    }

    /// <inheritdoc />
    public double MinimumVoltage
    {
        get { lock (_lock) { return _minimumVoltage; } }
    }

    /// <inheritdoc />
    public double MaximumVoltage
    {
        get { lock (_lock) { return _maximumVoltage; } }
    }

    /// <inheritdoc />
    public bool RangeIsAssumed
    {
        get { lock (_lock) { return _rangeIsAssumed; } }
    }

    /// <inheritdoc />
    public double? OutputVoltage
    {
        get { lock (_lock) { return _activeSample?.Value; } }
    }

    /// <inheritdoc />
    public double? PendingVoltage
    {
        get { lock (_lock) { return _pendingVoltage; } }
    }

    /// <inheritdoc />
    public event EventHandler<SampleReceivedEventArgs>? SampleReceived;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnalogOutputChannel"/> class.
    /// </summary>
    /// <param name="channelNumber">The device-facing channel number.</param>
    /// <param name="resolutionBits">The DAC resolution in bits.</param>
    /// <param name="minimumVoltage">The lowest voltage the channel accepts.</param>
    /// <param name="maximumVoltage">The highest voltage the channel accepts.</param>
    /// <param name="rangeIsAssumed">
    /// Whether the range is a Core fallback rather than a device-stated one.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The channel number is negative, the resolution is outside
    /// [<see cref="MinResolutionBits"/>, <see cref="MaxResolutionBits"/>], or a range endpoint is
    /// non-finite or beyond <see cref="MaxRangeMagnitudeVolts"/>.
    /// </exception>
    /// <exception cref="ArgumentException">The range is empty or inverted.</exception>
    public AnalogOutputChannel(
        int channelNumber,
        int resolutionBits = DefaultResolutionBits,
        double minimumVoltage = DefaultMinimumVoltage,
        double maximumVoltage = DefaultMaximumVoltage,
        bool rangeIsAssumed = false)
    {
        if (channelNumber < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(channelNumber), channelNumber, "Channel number must be non-negative.");
        }

        ValidateResolutionBits(resolutionBits, nameof(resolutionBits));
        ValidateRangeEndpoint(minimumVoltage, nameof(minimumVoltage));
        ValidateRangeEndpoint(maximumVoltage, nameof(maximumVoltage));

        if (minimumVoltage >= maximumVoltage)
        {
            throw new ArgumentException(
                $"The output range is empty: minimum ({minimumVoltage}) must be below maximum ({maximumVoltage}).",
                nameof(minimumVoltage));
        }

        ChannelNumber = channelNumber;
        _resolutionBits = resolutionBits;
        _minimumVoltage = minimumVoltage;
        _maximumVoltage = maximumVoltage;
        _rangeIsAssumed = rangeIsAssumed;
        _name = $"Analog Output {channelNumber}";
    }

    /// <inheritdoc />
    public bool IsInRange(double voltage)
    {
        if (!double.IsFinite(voltage))
        {
            return false;
        }

        lock (_lock)
        {
            return voltage >= _minimumVoltage && voltage <= _maximumVoltage;
        }
    }

    /// <summary>
    /// Atomically refreshes the resolution and range from a newly-read capability document, so a
    /// consumer holding this instance keeps it across a re-read rather than losing its identity.
    /// </summary>
    internal void UpdateFromCapabilities(
        int resolutionBits, double minimumVoltage, double maximumVoltage, bool rangeIsAssumed)
    {
        lock (_lock)
        {
            _resolutionBits = resolutionBits;
            _minimumVoltage = minimumVoltage;
            _maximumVoltage = maximumVoltage;
            _rangeIsAssumed = rangeIsAssumed;
        }
    }

    /// <summary>
    /// Records a voltage as staged on the device but not yet driven on the pin.
    /// </summary>
    internal void Stage(double voltage)
    {
        lock (_lock)
        {
            _pendingVoltage = voltage;
        }
    }

    /// <summary>
    /// Promotes the staged voltage (if any) to the driven value, raising
    /// <see cref="SampleReceived"/> so a consumer sees the change the same way it sees an input
    /// sample. Returns whether anything was pending.
    /// </summary>
    internal bool Latch(DateTime timestamp)
    {
        double staged;

        lock (_lock)
        {
            if (_pendingVoltage is null)
            {
                return false;
            }

            staged = _pendingVoltage.Value;
            _pendingVoltage = null;
        }

        SetActiveSample(new DataSample(timestamp, staged));
        return true;
    }

    /// <summary>
    /// Discards a staged voltage without driving it — used when the device rejects or never
    /// receives the latch.
    /// </summary>
    internal void ClearPending()
    {
        lock (_lock)
        {
            _pendingVoltage = null;
        }
    }

    /// <summary>
    /// Records a voltage as the value now driven on this channel, bypassing the staging step.
    /// Used by the device readback, which reports what the hardware currently holds.
    /// </summary>
    internal void SetOutputVoltage(double voltage, DateTime timestamp)
        => SetActiveSample(new DataSample(timestamp, voltage));

    /// <summary>
    /// Sets the value driven on this channel and raises <see cref="SampleReceived"/>. This is a
    /// record of what the host commanded, not a hardware measurement; the device has no DAC
    /// readback path.
    /// </summary>
    /// <param name="value">The voltage now driven on the channel.</param>
    /// <param name="timestamp">When the value took effect.</param>
    public void SetActiveSample(double value, DateTime timestamp)
        => SetActiveSample(new DataSample(timestamp, value));

    /// <summary>
    /// Sets the value driven on this channel from a fully-formed sample and raises
    /// <see cref="SampleReceived"/>.
    /// </summary>
    /// <param name="sample">The sample to record.</param>
    public void SetActiveSample(IDataSample sample)
    {
        Internal.ActiveSampleAssignment.Apply(this, _lock, sample, s => _activeSample = s, SampleReceived);
    }

    /// <summary>Returns the channel name.</summary>
    public override string ToString() => Name;

    private static void ValidateResolutionBits(int resolutionBits, string parameterName)
    {
        if (resolutionBits is < MinResolutionBits or > MaxResolutionBits)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                resolutionBits,
                $"DAC resolution must be between {MinResolutionBits} and {MaxResolutionBits} bits.");
        }
    }

    private static void ValidateRangeEndpoint(double volts, string parameterName)
    {
        if (!double.IsFinite(volts) || Math.Abs(volts) > MaxRangeMagnitudeVolts)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                volts,
                $"An output range endpoint must be finite and within +/-{MaxRangeMagnitudeVolts} V.");
        }
    }
}
