using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Runtime.InteropServices;
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
/// Filters ports by USB VID/PID, then probes candidates in parallel using SCPI commands.
/// </summary>
public class SerialDeviceFinder : IDeviceFinder, IDisposable
{
    #region Constants

    private const int DefaultBaudRate = 9600;
    private const int ProbeTimeoutMs = 1000;

    /// <summary>
    /// Discovery-specific timeouts (shorter than connection timeouts for fast scanning).
    /// </summary>
    private const int DiscoveryWakeUpDelayMs = 200;
    private const int DiscoveryResponseTimeoutMs = 1000;
    private const int DiscoveryMaxRetries = 2;
    private const int DiscoveryRetryIntervalMs = 300;
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
    /// Filters ports by USB VID/PID, then probes candidates in parallel.
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
            var allPorts = FilterProbableDaqifiPorts(SerialStreamTransport.GetAvailablePortNames());

            // Filter by USB VID/PID before probing (avoids sending SCPI to non-DAQiFi devices)
            var candidatePorts = FilterByUsbVidPid(allPorts).ToList();

            // Probe candidate ports in parallel
            var probeTasks = candidatePorts.Select(portName =>
                TryGetDeviceInfoAsync(portName, cancellationToken));

            var results = await Task.WhenAll(probeTasks);
            var discoveredDevices = results.Where(d => d != null).Cast<IDeviceInfo>().ToList();

            foreach (var device in discoveredDevices)
            {
                OnDeviceDiscovered(device);
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
    /// Uses reduced timeouts and minimal commands optimized for fast discovery.
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

            // Brief delay for device to wake up after DTR is enabled
            await Task.Delay(DiscoveryWakeUpDelayMs, cancellationToken);

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

            // Discovery only needs GetDeviceInfo — no setup commands required.
            // The full initialization sequence (DisableEcho, StopStreaming, TurnDeviceOn,
            // SetProtobufStreamFormat) is handled during connection, not discovery.
            var timeout = DateTime.UtcNow.AddMilliseconds(DiscoveryResponseTimeoutMs);
            var lastRequestTime = DateTime.MinValue;
            var retryCount = 0;

            while (statusMessage == null && DateTime.UtcNow < timeout && !cancellationToken.IsCancellationRequested)
            {
                if ((DateTime.UtcNow - lastRequestTime).TotalMilliseconds >= DiscoveryRetryIntervalMs &&
                    retryCount < DiscoveryMaxRetries)
                {
                    producer.Send(ScpiMessageProducer.GetDeviceInfo);
                    lastRequestTime = DateTime.UtcNow;
                    retryCount++;
                }

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
    /// Filters ports by USB VID/PID using the system's USB device information.
    /// </summary>
    /// <param name="ports">Ports to filter.</param>
    /// <returns>Ports that are candidates for DAQiFi devices.</returns>
    internal static IEnumerable<string> FilterByUsbVidPid(IEnumerable<string> ports)
    {
        return FilterByUsbVidPid(ports, SerialPortUsbDetector.GetPortUsbInfo());
    }

    /// <summary>
    /// Filters ports by USB VID/PID, keeping only ports that belong to known DAQiFi vendors.
    /// Ports with unknown USB identity (non-USB or detection failed) are included as candidates.
    /// If VID/PID detection is entirely unavailable (empty dictionary), all ports are returned.
    /// </summary>
    /// <param name="ports">Ports to filter.</param>
    /// <param name="usbInfo">USB VID/PID info per port from <see cref="SerialPortUsbDetector"/>.</param>
    /// <returns>Ports that are candidates for DAQiFi devices.</returns>
    internal static IEnumerable<string> FilterByUsbVidPid(
        IEnumerable<string> ports, Dictionary<string, SerialPortUsbDetector.UsbId> usbInfo)
    {
        var portList = ports.ToList();

        if (usbInfo.Count == 0)
        {
            // VID/PID detection unavailable — probe all ports
            return portList;
        }

        var candidates = new List<string>();
        foreach (var port in portList)
        {
            if (usbInfo.TryGetValue(port, out var id))
            {
                // USB info available — only include if VID matches DAQiFi
                if (SerialPortUsbDetector.IsDaqifiVendor(id.VendorId))
                {
                    candidates.Add(port);
                }
            }
            else
            {
                // No USB info for this port — include to be safe
                // (could be a non-USB serial port or detection missed it)
                candidates.Add(port);
            }
        }

        return candidates;
    }

    /// <summary>
    /// Filters the list of available ports to only include those likely to be DAQiFi devices.
    /// Excludes debug ports, Bluetooth ports, and on macOS prefers /dev/cu.* over /dev/tty.*.
    /// </summary>
    /// <param name="allPorts">All available serial port names.</param>
    /// <returns>Filtered list of ports to probe.</returns>
    internal static IEnumerable<string> FilterProbableDaqifiPorts(string[] allPorts)
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

        // On macOS/Linux, prefer /dev/cu.* ports (for outgoing connections) over /dev/tty.* ports
        // If we have both cu and tty versions of the same port, only use cu
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
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
