namespace Daqifi.Core.Channel;

/// <summary>
/// Represents an analog input/output channel with scaling and calibration capabilities.
/// </summary>
public class AnalogChannel : IAnalogChannel
{
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
        set { lock (_lock) { _minValue = value; } }
    }

    /// <summary>
    /// Gets or sets the maximum value for the channel range.
    /// </summary>
    public double MaxValue
    {
        get { lock (_lock) { return _maxValue; } }
        set { lock (_lock) { _maxValue = value; } }
    }

    /// <summary>
    /// Gets the resolution of the ADC (e.g., 65535 for 16-bit).
    /// </summary>
    /// <remarks>
    /// The setter is internal so that <see cref="Device.DaqifiDevice.PopulateChannelsFromStatus"/>
    /// can refresh this value in place on a status re-population without recreating the channel
    /// instance, keeping <see cref="IChannel"/> references stable for consumers.
    /// </remarks>
    public uint Resolution
    {
        get { lock (_lock) { return _resolution; } }
        internal set { lock (_lock) { _resolution = value; } }
    }

    /// <summary>
    /// Gets or sets the calibration slope (M in the scaling formula).
    /// </summary>
    public double CalibrationM
    {
        get { lock (_lock) { return _calibrationM; } }
        set { lock (_lock) { _calibrationM = value; } }
    }

    /// <summary>
    /// Gets or sets the calibration offset (B in the scaling formula).
    /// </summary>
    public double CalibrationB
    {
        get { lock (_lock) { return _calibrationB; } }
        set { lock (_lock) { _calibrationB = value; } }
    }

    /// <summary>
    /// Gets or sets the internal scale factor.
    /// </summary>
    public double InternalScaleM
    {
        get { lock (_lock) { return _internalScaleM; } }
        set { lock (_lock) { _internalScaleM = value; } }
    }

    /// <summary>
    /// Gets or sets the port range (voltage range).
    /// </summary>
    public double PortRange
    {
        get { lock (_lock) { return _portRange; } }
        set { lock (_lock) { _portRange = value; } }
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
    public AnalogChannel(int channelNumber, uint resolution = 65535)
    {
        if (channelNumber < 0)
            throw new ArgumentOutOfRangeException(nameof(channelNumber), "Channel number must be non-negative.");

        if (resolution == 0)
            throw new ArgumentOutOfRangeException(nameof(resolution), "Resolution must be greater than zero.");

        ChannelNumber = channelNumber;
        _resolution = resolution;
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
}
