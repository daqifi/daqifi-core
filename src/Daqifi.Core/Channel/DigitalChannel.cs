namespace Daqifi.Core.Channel;

/// <summary>
/// Represents a digital input/output channel.
/// </summary>
public class DigitalChannel : IDigitalChannel
{
    private readonly object _lock = new();
    private IDataSample? _activeSample;
    private string _name;
    private bool _isEnabled;
    private ChannelDirection _direction;
    private bool _outputValue;
    private bool _isPwmEnabled;
    private int _pwmDutyCyclePercent;

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
    /// Gets the channel type (always Digital for this class).
    /// </summary>
    public ChannelType Type => ChannelType.Digital;

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
    /// Gets or sets the output value for digital output channels (true = high, false = low).
    /// </summary>
    public bool OutputValue
    {
        get { lock (_lock) { return _outputValue; } }
        set
        {
            lock (_lock)
            {
                _outputValue = value;
                // When output value changes, we could trigger an event or callback
                // to notify the device to update the physical output
            }
        }
    }

    /// <summary>
    /// Gets whether the digital input is currently high (true) or low (false).
    /// </summary>
    public bool IsHigh
    {
        get
        {
            lock (_lock)
            {
                return _activeSample?.Value > 0.5;
            }
        }
    }

    /// <summary>
    /// Gets whether this channel's hardware supports PWM output.
    /// </summary>
    public bool IsPwmCapable { get; }

    /// <summary>
    /// Gets or sets whether PWM output is enabled on this channel (local bookkeeping mirroring
    /// the last commanded state).
    /// </summary>
    public bool IsPwmEnabled
    {
        get { lock (_lock) { return _isPwmEnabled; } }
        set { lock (_lock) { _isPwmEnabled = value; } }
    }

    /// <summary>
    /// Gets or sets the last commanded PWM duty cycle in whole percent (0-100). Local
    /// bookkeeping mirroring the last commanded state.
    /// </summary>
    public int PwmDutyCyclePercent
    {
        get { lock (_lock) { return _pwmDutyCyclePercent; } }
        set { lock (_lock) { _pwmDutyCyclePercent = value; } }
    }

    /// <summary>
    /// Event raised when a new sample is received on this channel.
    /// </summary>
    public event EventHandler<SampleReceivedEventArgs>? SampleReceived;

    /// <summary>
    /// Initializes a new instance of the <see cref="DigitalChannel"/> class.
    /// </summary>
    /// <param name="channelNumber">The channel number/index.</param>
    /// <param name="isPwmCapable">Whether this channel's hardware supports PWM output.</param>
    public DigitalChannel(int channelNumber, bool isPwmCapable = false)
    {
        if (channelNumber < 0)
            throw new ArgumentOutOfRangeException(nameof(channelNumber), "Channel number must be non-negative.");

        ChannelNumber = channelNumber;
        IsPwmCapable = isPwmCapable;
        _name = $"Digital Channel {channelNumber}";
        _isEnabled = false;
        _direction = ChannelDirection.Input;
        _outputValue = false;
        _isPwmEnabled = false;
        _pwmDutyCyclePercent = 0;
    }

    /// <summary>
    /// Sets the active sample for this channel and triggers the SampleReceived event.
    /// </summary>
    /// <param name="value">The value (typically 0 for low, 1 for high).</param>
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
