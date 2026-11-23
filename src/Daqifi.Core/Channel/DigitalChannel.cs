namespace Daqifi.Core.Channel;

/// <summary>
/// Represents a digital input/output channel.
/// </summary>
/// <remarks>
/// Instance members are thread-safe. Property accessors and <see cref="SetActiveSample(IDataSample)"/>
/// serialize on a private lock so concurrent readers and writers observe consistent state.
/// </remarks>
public class DigitalChannel : IDigitalChannel, IChannelEnablementNotifier
{
    private readonly Lock _lock = new();
    private IDataSample? _activeSample;
    private string _name;
    private bool _isEnabled;
    private Action? _enablementChanged;
    private ChannelDirection _direction;
    private bool _isPwmEnabled;
    private int _pwmDutyCyclePercent;
    private bool _isPwmCapable;

    /// <summary>
    /// Lowest commandable PWM duty cycle, in whole percent. Duty 0 is a firmware trap (stored
    /// but never applied) handled by <c>IStreamingDevice.SetPwmDutyCycle</c>, not a valid
    /// bookkeeping value.
    /// </summary>
    private const int MinPwmDutyCyclePercent = 1;

    /// <summary>
    /// Highest commandable PWM duty cycle, in whole percent.
    /// </summary>
    private const int MaxPwmDutyCyclePercent = 100;

    /// <summary>
    /// Default PWM duty cycle, in whole percent, used until a value has been commanded.
    /// </summary>
    private const int DefaultPwmDutyCyclePercent = 50;

    /// <summary>
    /// Gets the channel number/index.
    /// </summary>
    public int ChannelNumber { get; }

    /// <summary>
    /// Gets or sets the channel name.
    /// </summary>
    /// <exception cref="ArgumentNullException">The assigned value is <see langword="null"/>.</exception>
    public string Name
    {
        get { lock (_lock) { return _name; } }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_lock) { _name = value; }
        }
    }

    /// <inheritdoc />
    event Action? IChannelEnablementNotifier.EnablementChanged
    {
        add => Internal.ChannelEnablementNotification.AddHandler(_lock, ref _enablementChanged, value);
        remove => Internal.ChannelEnablementNotification.RemoveHandler(_lock, ref _enablementChanged, value);
    }

    /// <summary>
    /// Gets or sets whether the channel is enabled.
    /// </summary>
    public bool IsEnabled
    {
        get { lock (_lock) { return _isEnabled; } }
        set => Internal.ChannelEnablementNotification.SetAndNotify(_lock, value, ref _isEnabled, ref _enablementChanged);
    }

    /// <summary>
    /// Gets the channel type (always Digital for this class).
    /// </summary>
    public ChannelType Type => ChannelType.Digital;

    /// <summary>
    /// Gets the one mode the pin is in: input, output or PWM. This is the single place the mode
    /// is worked out; <see cref="Direction"/> and <see cref="IsPwmEnabled"/> agree with it.
    /// </summary>
    /// <remarks>
    /// Changed only by the <c>IStreamingDevice</c> commands (<c>SetDioDirection</c>,
    /// <c>SetPwmEnabled</c>), after the command has been sent, and by the device's own status
    /// message. Core records PWM as off when a connection ends, because it can no longer vouch
    /// for the device; enable PWM again after a reconnect (#945).
    /// </remarks>
    public DigitalPinMode Mode
    {
        get { lock (_lock) { return ModeUnderLock(); } }
    }

    /// <summary>
    /// Gets the channel direction (Input or Output). While PWM is enabled this reads
    /// <see cref="ChannelDirection.Output"/>, because the PWM timer is driving the pin; the
    /// direction the pin returns to when PWM is disabled is kept and reported again from then on.
    /// Use <c>IStreamingDevice.SetDioDirection</c> to change it.
    /// </summary>
    public ChannelDirection Direction
    {
        get
        {
            lock (_lock)
            {
                return _isPwmEnabled ? ChannelDirection.Output : _direction;
            }
        }
    }

    /// <summary>
    /// <see cref="IChannel"/> requires a setter. Assigning through it sends nothing to the device
    /// and only moves Core's view, which the next status message overwrites; it is kept for
    /// callers that carry a channel's state across a re-created object.
    /// </summary>
    ChannelDirection IChannel.Direction
    {
        get => Direction;
        set => ApplyDirection(value);
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
    /// Gets whether the pin is currently high (true) or low (false), as reported by the most
    /// recent streamed sample. Valid for an input pin and for a driven output pin alike: the
    /// firmware reads the pin back, not the direction register, so a driven output streams its
    /// true level (#901). Returns <see langword="false"/> until the first sample arrives.
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
    public bool IsPwmCapable
    {
        get { lock (_lock) { return _isPwmCapable; } }
    }

    /// <summary>
    /// Gets whether PWM output is enabled on this channel (local bookkeeping mirroring the last
    /// commanded state). The same fact as <see cref="Mode"/> being
    /// <see cref="DigitalPinMode.Pwm"/>. Use <c>IStreamingDevice.SetPwmEnabled</c> to change it.
    /// </summary>
    public bool IsPwmEnabled
    {
        get { lock (_lock) { return _isPwmEnabled; } }
    }

    /// <summary>
    /// Gets or sets the last commanded PWM duty cycle in whole percent. Local bookkeeping
    /// mirroring the last commanded state; defaults to <c>50</c> (a commandable value) until a
    /// duty cycle has been set, and clamps to <c>1-100</c> on assignment so this always holds a
    /// commandable value.
    /// </summary>
    public int PwmDutyCyclePercent
    {
        get { lock (_lock) { return _pwmDutyCyclePercent; } }
        set
        {
            lock (_lock)
            {
                _pwmDutyCyclePercent = Math.Clamp(value, MinPwmDutyCyclePercent, MaxPwmDutyCyclePercent);
            }
        }
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
        {
            throw new ArgumentOutOfRangeException(
                nameof(channelNumber), channelNumber, "Channel number must be non-negative.");
        }

        ChannelNumber = channelNumber;
        _isPwmCapable = isPwmCapable;
        _name = $"Digital Channel {channelNumber}";
        _isEnabled = false;
        _direction = ChannelDirection.Input;
        _isPwmEnabled = false;
        _pwmDutyCyclePercent = DefaultPwmDutyCyclePercent;
    }

    /// <summary>
    /// Records the direction the pin is in — commanded by <c>SetDioDirection</c> or reported by
    /// the device's status message. While PWM is enabled this is the direction the pin returns
    /// to when PWM stops, which is what the firmware keeps too.
    /// </summary>
    internal void ApplyDirection(ChannelDirection direction)
    {
        lock (_lock) { _direction = direction; }
    }

    /// <summary>
    /// Records whether the hardware can do PWM, from the device's status message. A pin that
    /// cannot do PWM cannot be in PWM mode, so losing the capability also leaves that mode.
    /// </summary>
    internal void ApplyPwmCapability(bool isPwmCapable)
    {
        lock (_lock)
        {
            _isPwmCapable = isPwmCapable;
            if (!isPwmCapable)
            {
                _isPwmEnabled = false;
            }
        }
    }

    /// <summary>
    /// Moves the pin into PWM mode, after the device has been told to start PWM on it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The channel is not PWM-capable.</exception>
    internal void EnterPwm()
    {
        lock (_lock)
        {
            if (!_isPwmCapable)
            {
                throw new InvalidOperationException(
                    $"Channel {ChannelNumber} does not support PWM and cannot be put in PWM mode.");
            }

            _isPwmEnabled = true;
        }
    }

    /// <summary>
    /// Moves the pin out of PWM mode, back to its stored direction.
    /// </summary>
    internal void ExitPwm()
    {
        lock (_lock)
        {
            _isPwmEnabled = false;
        }
    }

    /// <summary>
    /// The pin mode, derived from the PWM flag and the stored direction. Caller holds the lock.
    /// </summary>
    private DigitalPinMode ModeUnderLock()
    {
        if (_isPwmEnabled)
        {
            return DigitalPinMode.Pwm;
        }

        return _direction == ChannelDirection.Output ? DigitalPinMode.Output : DigitalPinMode.Input;
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
    /// <exception cref="ArgumentNullException"><paramref name="sample"/> is <see langword="null"/>.</exception>
    public void SetActiveSample(IDataSample sample)
    {
        Internal.ActiveSampleAssignment.StoreUnderLock(_lock, sample, ref _activeSample);
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
