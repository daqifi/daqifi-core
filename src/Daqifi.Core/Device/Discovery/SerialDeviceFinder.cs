using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Communication.Consumers;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device.Protocol;

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Discovers DAQiFi devices connected via USB/Serial ports.
/// Probes each port by sending SCPI commands and validating protobuf responses.
/// </summary>
public class SerialDeviceFinder : IDeviceFinder, IDisposable
{
    #region Constants

    private const int DefaultBaudRate = 9600;
    private const int ProbeTimeoutMs = 1000;
    private const int DeviceWakeUpDelayMs = 1000;
    private const int ResponseTimeoutMs = 4000;
    private const int MaxRetries = 3;
    private const int RetryIntervalMs = 1000;
    private const int PollIntervalMs = 100;

    #endregion

    #region Private Fields

    private readonly int _baudRate;
    private readonly SemaphoreSlim _discoverySemaphore = new(1, 1);
    private bool _disposed;

    #endregion

    #region Events

    /// <summary>
    /// Occurs when a device is discovered.
    /// </summary>
    public event EventHandler<DeviceDiscoveredEventArgs>? DeviceDiscovered;

    /// <summary>
    /// Occurs when device discovery completes.
    /// </summary>
    public event EventHandler? DiscoveryCompleted;

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new instance of the SerialDeviceFinder class with default baud rate.
    /// </summary>
    public SerialDeviceFinder() : this(DefaultBaudRate)
    {
    }

    /// <summary>
    /// Initializes a new instance of the SerialDeviceFinder class.
    /// </summary>
    /// <param name="baudRate">The baud rate to use for serial connections.</param>
    public SerialDeviceFinder(int baudRate)
    {
        _baudRate = baudRate;
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Discovers devices asynchronously with a cancellation token.
    /// Probes each serial port to identify DAQiFi devices.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to abort the operation.</param>
    /// <returns>A task containing the collection of discovered DAQiFi devices.</returns>
    public async Task<IEnumerable<IDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // Prevent concurrent discovery operations
        await _discoverySemaphore.WaitAsync(cancellationToken);
        try
        {
            var discoveredDevices = new List<IDeviceInfo>();
            var availablePorts = FilterProbableDaqifiPorts(SerialStreamTransport.GetAvailablePortNames());

            foreach (var portName in availablePorts)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    var deviceInfo = await TryGetDeviceInfoAsync(portName, cancellationToken);
                    if (deviceInfo != null)
                    {
                        discoveredDevices.Add(deviceInfo);
                        OnDeviceDiscovered(deviceInfo);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    // Skip ports that fail to open or respond
                    // This is normal as not all serial ports are DAQiFi devices
                }
            }

            OnDiscoveryCompleted();
            return discoveredDevices;
        }
        finally
        {
            _discoverySemaphore.Release();
        }
    }

    /// <summary>
    /// Discovers devices asynchronously with a timeout.
    /// </summary>
    /// <param name="timeout">The timeout for discovery.</param>
    /// <returns>A task containing the collection of discovered devices.</returns>
    public async Task<IEnumerable<IDeviceInfo>> DiscoverAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await DiscoverAsync(cts.Token);
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// Attempts to probe a serial port and retrieve device information.
    /// Opens the port, sends GetDeviceInfo command, and waits for a status response.
    /// </summary>
    /// <param name="portName">The serial port name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Device info if a DAQiFi device responds, null otherwise.</returns>
    private async Task<IDeviceInfo?> TryGetDeviceInfoAsync(string portName, CancellationToken cancellationToken)
    {
        SerialPort? port = null;
        MessageProducer<string>? producer = null;
        StreamMessageConsumer<DaqifiOutMessage>? consumer = null;

        try
        {
            // Create and configure the serial port
            port = new SerialPort(portName, _baudRate)
            {
                ReadTimeout = ProbeTimeoutMs,
                WriteTimeout = ProbeTimeoutMs
            };

            // Try to open the port
            port.Open();
            port.DtrEnable = true;

            // Wait for device to wake up (devices need time after DTR is enabled)
            await Task.Delay(DeviceWakeUpDelayMs, cancellationToken);

            // Set up message producer and consumer
            var stream = port.BaseStream;
            producer = new MessageProducer<string>(stream);
            producer.Start();

            var parser = new ProtobufMessageParser();
            consumer = new StreamMessageConsumer<DaqifiOutMessage>(stream, parser);

            // Track status message reception
            DaqifiOutMessage? statusMessage = null;
            var messageReceived = new TaskCompletionSource<bool>();

            consumer.MessageReceived += (_, args) =>
            {
                if (args.Message.Data is DaqifiOutMessage message)
                {
                    var messageType = ProtobufProtocolHandler.DetectMessageType(message);
                    if (messageType == ProtobufMessageType.Status)
                    {
                        statusMessage = message;
                        messageReceived.TrySetResult(true);
                    }
                }
            };

            consumer.Start();

            // Initialize device - send commands to prepare for communication
            // These are required for the device to respond properly
            producer.Send(ScpiMessageProducer.DisableDeviceEcho);
            await Task.Delay(100, cancellationToken);

            producer.Send(ScpiMessageProducer.StopStreaming);
            await Task.Delay(100, cancellationToken);

            producer.Send(ScpiMessageProducer.TurnDeviceOn);
            await Task.Delay(100, cancellationToken);

            producer.Send(ScpiMessageProducer.SetProtobufStreamFormat);
            await Task.Delay(100, cancellationToken);

            // Send GetDeviceInfo command with retry logic
            var timeout = DateTime.UtcNow.AddMilliseconds(ResponseTimeoutMs);
            var lastRequestTime = DateTime.MinValue;
            var retryCount = 0;

            while (statusMessage == null && DateTime.UtcNow < timeout && !cancellationToken.IsCancellationRequested)
            {
                // Send request every RetryIntervalMs, up to MaxRetries times
                if ((DateTime.UtcNow - lastRequestTime).TotalMilliseconds >= RetryIntervalMs && retryCount < MaxRetries)
                {
                    producer.Send(ScpiMessageProducer.GetDeviceInfo);
                    lastRequestTime = DateTime.UtcNow;
                    retryCount++;
                }

                // Wait a bit for response
                var remainingTime = Math.Min(PollIntervalMs, (int)(timeout - DateTime.UtcNow).TotalMilliseconds);
                if (remainingTime > 0)
                {
                    await Task.WhenAny(
                        messageReceived.Task,
                        Task.Delay(remainingTime, cancellationToken));
                }
            }

            if (statusMessage == null)
            {
                return null; // Not a DAQiFi device or device not responding
            }

            // Extract device information from the status message
            var deviceInfo = new DeviceInfo
            {
                Name = !string.IsNullOrWhiteSpace(statusMessage.DevicePn)
                    ? statusMessage.DevicePn
                    : portName,
                SerialNumber = statusMessage.DeviceSn.ToString(),
                FirmwareVersion = statusMessage.DeviceFwRev ?? "Unknown",
                ConnectionType = ConnectionType.Serial,
                PortName = portName,
                Type = ConvertDeviceType(DeviceTypeDetector.DetectFromPartNumber(statusMessage.DevicePn)),
                IsPowerOn = true
            };

            return deviceInfo;
        }
        catch (UnauthorizedAccessException)
        {
            // Port is in use by another application
            return null;
        }
        catch (Exception)
        {
            // Any other error - not a valid DAQiFi device or port unavailable
            return null;
        }
        finally
        {
            // Clean up resources
            try
            {
                consumer?.StopSafely(500);
                consumer?.Dispose();
            }
            catch
            {
                // Ignore cleanup errors
            }

            try
            {
                producer?.StopSafely(500);
                producer?.Dispose();
            }
            catch
            {
                // Ignore cleanup errors
            }

            try
            {
                if (port is { IsOpen: true })
                {
                    port.DtrEnable = false;
                    await Task.Delay(50); // Give DTR time to be processed
                    port.Close();
                }

                port?.Dispose();
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    /// <summary>
    /// Filters the list of available ports to only include those likely to be DAQiFi devices.
    /// Excludes debug ports, Bluetooth ports, and on macOS prefers /dev/cu.* over /dev/tty.*.
    /// </summary>
    /// <param name="allPorts">All available serial port names.</param>
    /// <returns>Filtered list of ports to probe.</returns>
    private static IEnumerable<string> FilterProbableDaqifiPorts(string[] allPorts)
    {
        // Skip debug and bluetooth ports which are unlikely to be DAQiFi devices
        var excludePatterns = new[]
        {
            "debug",
            "Bluetooth",
            "wlan"
        };

        var filteredPorts = allPorts
            .Where(port => !excludePatterns.Any(pattern =>
                port.Contains(pattern, StringComparison.OrdinalIgnoreCase)));

        // On macOS, prefer /dev/cu.* ports (for outgoing connections) over /dev/tty.* ports
        // If we have both cu and tty versions of the same port, only use cu
        if (Environment.OSVersion.Platform == PlatformID.Unix ||
            Environment.OSVersion.Platform == PlatformID.MacOSX)
        {
            var cuPorts = filteredPorts.Where(p => p.StartsWith("/dev/cu.")).ToList();
            var ttyPorts = filteredPorts.Where(p => p.StartsWith("/dev/tty.")).ToList();

            // For each tty port, check if there's a corresponding cu port
            // If so, skip the tty port
            var portsToProbe = new List<string>(cuPorts);
            foreach (var ttyPort in ttyPorts)
            {
                var cuEquivalent = ttyPort.Replace("/dev/tty.", "/dev/cu.");
                if (!cuPorts.Contains(cuEquivalent))
                {
                    portsToProbe.Add(ttyPort);
                }
            }

            // Add any non-tty/cu ports (like /dev/ttyUSB0, /dev/ttyACM0)
            portsToProbe.AddRange(filteredPorts.Where(p =>
                !p.StartsWith("/dev/cu.") && !p.StartsWith("/dev/tty.")));

            return portsToProbe;
        }

        return filteredPorts;
    }

    /// <summary>
    /// Converts from the Device.DeviceType enum to the Discovery.DeviceType enum.
    /// </summary>
    /// <param name="deviceType">The Device namespace DeviceType.</param>
    /// <returns>The Discovery namespace DeviceType.</returns>
    private static DeviceType ConvertDeviceType(Device.DeviceType deviceType)
    {
        return deviceType switch
        {
            Device.DeviceType.Nyquist1 => DeviceType.Nyquist1,
            Device.DeviceType.Nyquist3 => DeviceType.Nyquist3,
            _ => DeviceType.Unknown
        };
    }

    /// <summary>
    /// Raises the DeviceDiscovered event.
    /// </summary>
    protected virtual void OnDeviceDiscovered(IDeviceInfo deviceInfo)
    {
        DeviceDiscovered?.Invoke(this, new DeviceDiscoveredEventArgs(deviceInfo));
    }

    /// <summary>
    /// Raises the DiscoveryCompleted event.
    /// </summary>
    protected virtual void OnDiscoveryCompleted()
    {
        DiscoveryCompleted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Throws ObjectDisposedException if this instance has been disposed.
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SerialDeviceFinder));
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes the device finder.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _discoverySemaphore.Dispose();
            _disposed = true;
        }
    }

    #endregion
}
