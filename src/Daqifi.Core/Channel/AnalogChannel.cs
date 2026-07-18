namespace Daqifi.Core.Channel;

/// <summary>
/// Represents an analog input/output channel with scaling and calibration capabilities.
/// </summary>
public class AnalogChannel : IAnalogChannel
{
    /// <summary>
    /// Smallest <see cref="Resolution"/> (maximum raw count) accepted as a physically plausible ADC
    /// resolution — 255, i.e. 2^8 - 1 for an 8-bit converter. <see cref="Resolution"/> is stored as
    /// the maximum raw count (2^bits - 1), not the bit depth, so this is the max-count for 8 bits.
    /// </summary>
    public const uint MinResolution = 255;

    /// <summary>
    /// Largest <see cref="Resolution"/> (maximum raw count) accepted as a physically plausible ADC
    /// resolution — 16,777,216, covering 24-bit converters whether reported as 2^24 or 2^24 - 1.
    /// </summary>
    public const uint MaxResolution = 16_777_216;

    /// <summary>
    /// Largest absolute <see cref="PortRange"/>, in volts, accepted as physically reasonable. DAQiFi
    /// hardware tops out at ±10V differential ranges; 50 leaves generous headroom while still
    /// rejecting nonsensical values.
    /// </summary>
    public const double MaxPortRangeVolts = 50.0;

    /// <summary>
    /// Largest absolute magnitude accepted for the multiplicative/offset calibration coefficients
    /// (<see cref="CalibrationM"/>, <see cref="CalibrationB"/>, <see cref="InternalScaleM"/>). Values
    /// beyond this indicate a corrupted coefficient rather than a real calibration.
    /// </summary>
    public const double MaxCalibrationMagnitude = 1_000_000.0;

    private readonly object _lock = new();
    private IDataSample? _activeSample;
    private string _name;
    private bool _isEnabled;
    private ChannelDirection _direction;
    private double _minValue;
    private double _maxValue;
    private double _calibrationM;
    private double _calibrationB;
    private double _internalScaleM;
    private double _portRange;
    private uint _resolution;
    private bool _resolutionIsAssumed;

    /// <summary>
    /// Gets the channel number/index.
    /// </summary>
    public int ChannelNumber { get; }

    /// <summary>
    /// Gets or sets the channel name.
    /// </summary>
    public string Name
    {
        get { lock (_lock) { return _name; } }
        set { lock (_lock) { _name = value; } }
    }

    /// <summary>
    /// Gets or sets whether the channel is enabled.
    /// </summary>
    public bool IsEnabled
    {
        get { lock (_lock) { return _isEnabled; } }
        set { lock (_lock) { _isEnabled = value; } }
    }

    /// <summary>
    /// Gets the channel type (always Analog for this class).
    /// </summary>
    public ChannelType Type => ChannelType.Analog;

    /// <summary>
    /// Gets or sets the channel direction (Input or Output).
    /// </summary>
    public ChannelDirection Direction
    {
        get { lock (_lock) { return _direction; } }
        set { lock (_lock) { _direction = value; } }
    }

    /// <summary>
    /// Gets the most recent data sample received on this channel.
    /// </summary>
    public IDataSample? ActiveSample
    {
        get
        {
            lock (_lock)
            {
                return _activeSample;
            }
        }
    }

    /// <summary>
    /// Gets or sets the minimum value for the channel range.
    /// </summary>
    public double MinValue
    {
        get { lock (_lock) { return _minValue; } }
        set
        {
            RequireFinite(value, nameof(MinValue));
            lock (_lock) { _minValue = value; }
        }
    }

    /// <summary>
    /// Gets or sets the maximum value for the channel range.
    /// </summary>
    public double MaxValue
    {
        get { lock (_lock) { return _maxValue; } }
        set
        {
            RequireFinite(value, nameof(MaxValue));
            lock (_lock) { _maxValue = value; }
        }
    }

    /// <summary>
    /// Gets whether the configured display range (<see cref="MinValue"/>..<see cref="MaxValue"/>) is
    /// bipolar — i.e. spans negative voltages, as the ±1V/±5V/±10V differential ranges do — rather
    /// than unipolar (0V-and-up). Derived purely from <see cref="MinValue"/>, which a consumer sets
    /// when selecting a range; lets range-selection UI branch on polarity without hardcoding
    /// per-device assumptions.
    /// </summary>
    /// <remarks>
    /// Whether the device actually emits signed two's-complement raw counts and per-range calibration
    /// for a given bipolar range is firmware-dependent and tracked separately (daqifi-core#297); this
    /// property reflects the configured range only.
    /// </remarks>
    public bool IsBipolar
    {
        get { lock (_lock) { return _minValue < 0.0; } }
    }

    /// <summary>
    /// Gets the resolution of the ADC, expressed as the maximum raw count (e.g., 65535 for 16-bit,
    /// 262143 for 18-bit) — i.e. 2^bits - 1, not 2^bits. This value is a direct divisor in
    /// <see cref="GetScaledValue"/>.
    /// </summary>
    /// <remarks>
    /// Updated only via <see cref="UpdateScalingFromStatus"/>, which
    /// <see cref="Device.DaqifiDevice.PopulateChannelsFromStatus"/> uses to refresh this value in
    /// place on a status re-population without recreating the channel instance, keeping
    /// <see cref="IChannel"/> references stable for consumers.
    /// </remarks>
    public uint Resolution
    {
        get { lock (_lock) { return _resolution; } }
    }

    /// <summary>
    /// Gets whether <see cref="Resolution"/> is a fallback guess rather than a value the device
    /// actually reported.
    /// </summary>
    public bool ResolutionIsAssumed
    {
        get { lock (_lock) { return _resolutionIsAssumed; } }
    }

    /// <summary>
    /// Gets or sets the calibration slope (M in the scaling formula).
    /// </summary>
    public double CalibrationM
    {
        get { lock (_lock) { return _calibrationM; } }
        set
        {
            ValidateScaleFactor(value, nameof(CalibrationM));
            lock (_lock) { _calibrationM = value; }
        }
    }

    /// <summary>
    /// Gets or sets the calibration offset (B in the scaling formula).
    /// </summary>
    public double CalibrationB
    {
        get { lock (_lock) { return _calibrationB; } }
        set
        {
            ValidateOffset(value, nameof(CalibrationB));
            lock (_lock) { _calibrationB = value; }
        }
    }

    /// <summary>
    /// Gets or sets the internal scale factor.
    /// </summary>
    public double InternalScaleM
    {
        get { lock (_lock) { return _internalScaleM; } }
        set
        {
            ValidateScaleFactor(value, nameof(InternalScaleM));
            lock (_lock) { _internalScaleM = value; }
        }
    }

    /// <summary>
    /// Gets or sets the port range (voltage range).
    /// </summary>
    public double PortRange
    {
        get { lock (_lock) { return _portRange; } }
        set
        {
            ValidatePortRange(value, nameof(PortRange));
            lock (_lock) { _portRange = value; }
        }
    }

    /// <summary>
    /// Event raised when a new sample is received on this channel.
    /// </summary>
    public event EventHandler<SampleReceivedEventArgs>? SampleReceived;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnalogChannel"/> class.
    /// </summary>
    /// <param name="channelNumber">The channel number/index.</param>
    /// <param name="resolution">The ADC resolution (e.g., 65535 for 16-bit).</param>
    /// <param name="resolutionIsAssumed">
    /// Whether <paramref name="resolution"/> is a fallback guess rather than a device-reported value.
    /// </param>
    public AnalogChannel(int channelNumber, uint resolution = 65535, bool resolutionIsAssumed = false)
    {
        if (channelNumber < 0)
            throw new ArgumentOutOfRangeException(nameof(channelNumber), "Channel number must be non-negative.");

        ValidateResolution(resolution, nameof(resolution));

        ChannelNumber = channelNumber;
        _resolution = resolution;
        _resolutionIsAssumed = resolutionIsAssumed;
        _name = $"Analog Channel {channelNumber}";
        _isEnabled = false;
        _direction = ChannelDirection.Input;
        _minValue = -10.0;
        _maxValue = 10.0;
        _calibrationM = 1.0;
        _calibrationB = 0.0;
        _internalScaleM = 1.0;
        _portRange = 1.0;
    }

    /// <summary>
    /// Atomically updates the resolution and calibration/scaling metadata under a single lock.
    /// </summary>
    /// <remarks>
    /// Used by <see cref="Device.DaqifiDevice.PopulateChannelsFromStatus"/> when refreshing a
    /// reused channel instance in place, so a concurrent reader (e.g. via
    /// <see cref="Device.DaqifiDevice.GetChannelsSnapshot"/> on another thread) can never observe
    /// a torn mix of old and new scaling values from <see cref="GetScaledValue"/>.
    /// </remarks>
    internal void UpdateScalingFromStatus(uint resolution, double calibrationB, double calibrationM, double internalScaleM, double portRange, bool resolutionIsAssumed = false)
    {
        lock (_lock)
        {
            _resolution = resolution;
            _resolutionIsAssumed = resolutionIsAssumed;
            _calibrationB = calibrationB;
            _calibrationM = calibrationM;
            _internalScaleM = internalScaleM;
            _portRange = portRange;
        }
    }

    /// <summary>
    /// Converts a raw ADC value to a scaled value using the calibration parameters.
    /// Formula: ScaledValue = (RawValue / Resolution * PortRange * CalibrationM + CalibrationB) * InternalScaleM
    /// </summary>
    /// <param name="rawValue">The raw ADC value from the device.</param>
    /// <returns>The scaled value.</returns>
    public double GetScaledValue(int rawValue)
    {
        lock (_lock)
        {
            double normalized = (double)rawValue / Resolution;
            double scaled = (normalized * _portRange * _calibrationM + _calibrationB) * _internalScaleM;
            return scaled;
        }
    }

    /// <summary>
    /// Sets the active sample for this channel and triggers the SampleReceived event.
    /// </summary>
    /// <param name="value">The raw or scaled value.</param>
    /// <param name="timestamp">The timestamp when the sample was taken.</param>
    public void SetActiveSample(double value, DateTime timestamp)
    {
        SetActiveSample(new DataSample(timestamp, value));
    }

    /// <summary>
    /// Sets the active sample for this channel to a fully-formed sample and triggers the
    /// SampleReceived event.
    /// </summary>
    /// <param name="sample">The sample to set as active.</param>
    public void SetActiveSample(IDataSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        lock (_lock)
        {
            _activeSample = sample;
        }

        SampleReceived?.Invoke(this, new SampleReceivedEventArgs(this, sample));
    }

    /// <summary>
    /// Returns a string representation of the channel.
    /// </summary>
    /// <returns>The channel name.</returns>
    public override string ToString()
    {
        return Name;
    }

    /// <summary>
    /// Validates that <paramref name="resolution"/> is a physically plausible ADC max-count, in
    /// <see cref="MinResolution"/>..<see cref="MaxResolution"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="resolution"/> is outside the valid range.</exception>
    internal static void ValidateResolution(uint resolution, string paramName)
    {
        if (resolution is < MinResolution or > MaxResolution)
        {
            throw new ArgumentOutOfRangeException(
                paramName, resolution,
                $"Resolution must be a plausible ADC max-count between {MinResolution} and {MaxResolution}.");
        }
    }

    /// <summary>
    /// Validates that <paramref name="value"/> is a physically reasonable port (voltage) range:
    /// finite, positive, and no larger than <see cref="MaxPortRangeVolts"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="value"/> is not a valid range.</exception>
    internal static void ValidatePortRange(double value, string paramName)
    {
        if (!double.IsFinite(value) || value <= 0.0 || value > MaxPortRangeVolts)
        {
            throw new ArgumentOutOfRangeException(
                paramName, value,
                $"Port range must be a finite value in (0, {MaxPortRangeVolts}] volts.");
        }
    }

    /// <summary>
    /// Validates a multiplicative scale factor (<see cref="CalibrationM"/>/<see cref="InternalScaleM"/>):
    /// finite, non-zero, and within ±<see cref="MaxCalibrationMagnitude"/>. Negative factors are allowed
    /// (they invert the signal); zero is not (it discards the measurement entirely).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="value"/> is not a valid scale factor.</exception>
    internal static void ValidateScaleFactor(double value, string paramName)
    {
        if (!double.IsFinite(value) || value == 0.0 || Math.Abs(value) > MaxCalibrationMagnitude)
        {
            throw new ArgumentOutOfRangeException(
                paramName, value,
                $"Scale factor must be a finite, non-zero value within ±{MaxCalibrationMagnitude}.");
        }
    }

    /// <summary>
    /// Validates a calibration offset (<see cref="CalibrationB"/>): finite and within
    /// ±<see cref="MaxCalibrationMagnitude"/>. Zero is a valid offset.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="value"/> is not a valid offset.</exception>
    internal static void ValidateOffset(double value, string paramName)
    {
        if (!double.IsFinite(value) || Math.Abs(value) > MaxCalibrationMagnitude)
        {
            throw new ArgumentOutOfRangeException(
                paramName, value,
                $"Calibration offset must be a finite value within ±{MaxCalibrationMagnitude}.");
        }
    }

    /// <summary>
    /// Validates that <paramref name="value"/> is finite (rejects NaN/Infinity).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="value"/> is not finite.</exception>
    internal static void RequireFinite(double value, string paramName)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(paramName, value, "Value must be a finite number.");
        }
    }
}
