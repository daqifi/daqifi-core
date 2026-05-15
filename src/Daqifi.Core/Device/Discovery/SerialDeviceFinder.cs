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
/// Probes each port by sending SCPI commands and validating protobuf responses.
/// </summary>
public class SerialDeviceFinder : IDeviceFinder, IDisposable
{
    #region Constants

    private const int DefaultBaudRate = 9600;
    private const int ProbeTimeoutMs = 1000;
    // Tightened for the GetDeviceInfo-only probe (closes #157):
    //   - 200ms wake instead of 1s — USB CDC enumerates fast
    //   - 1s response timeout instead of 4s — DAQiFi devices reply within
    //     a few hundred ms on USB
    //   - 300ms retry interval instead of 1s
    //   - 2 attempts instead of 3 — combined with VID/PID prefilter we no
    //     longer need to be defensive against probing other vendors' ports
    private const int DeviceWakeUpDelayMs = 200;
    private const int ResponseTimeoutMs = 1000;
    private const int MaxRetries = 2;
    private const int RetryIntervalMs = 300;
    private const int PollIntervalMs = 50;
    // Upper bound on concurrent SerialPort opens during a single discovery
    // pass. Most OS serial stacks tolerate 4-8 simultaneous opens cleanly;
    // beyond that, IO failures and slow opens stack up. Common case (with
    // VID/PID classifier) leaves 0-1 candidates so this cap rarely engages.
    private const int MaxParallelProbes = 4;

    #endregion

    #region Private Fields

    private readonly int _baudRate;
    private readonly SemaphoreSlim _discoverySemaphore = new(1, 1);
    private readonly IUsbPortDescriptorProvider _usbPortDescriptorProvider;
    private readonly Func<string[]>? _portNameProvider;
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
    public SerialDeviceFinder() : this(DefaultBaudRate, usbPortDescriptorProvider: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the SerialDeviceFinder class.
    /// </summary>
    /// <param name="baudRate">The baud rate to use for serial connections.</param>
    public SerialDeviceFinder(int baudRate) : this(baudRate, usbPortDescriptorProvider: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the SerialDeviceFinder class with an
    /// explicit USB descriptor provider — primarily for tests that mock the
    /// platform-specific WMI / sysfs lookup.
    /// </summary>
    /// <param name="baudRate">The baud rate to use for serial connections.</param>
    /// <param name="usbPortDescriptorProvider">
    /// Provider used to resolve a port's USB Vendor / Product ID before
    /// opening it. When null, a platform-default provider is used (Windows
    /// → WMI, Linux → sysfs, others → no-op fallback). Pass
    /// <see cref="NullUsbPortDescriptorProvider.Instance"/> explicitly to
    /// force the legacy probe-everything behavior.
    /// </param>
    /// <param name="portNameProvider">
    /// Test seam: when non-null, supplies the candidate port-name list in
    /// place of <see cref="SerialStreamTransport.GetAvailablePortNames"/>.
    /// Lets unit tests deterministically exercise the descriptor / probe
    /// path on hosts (CI containers) that have no real serial ports.
    /// </param>
    internal SerialDeviceFinder(
        int baudRate,
        IUsbPortDescriptorProvider? usbPortDescriptorProvider,
        Func<string[]>? portNameProvider = null)
    {
        _baudRate = baudRate;
        _usbPortDescriptorProvider = usbPortDescriptorProvider
            ?? UsbPortDescriptorProviderFactory.CreateForCurrentPlatform();
        _portNameProvider = portNameProvider;
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
            // Pre-filter by USB VID/PID where the platform supports it
            // (closes #157). This drops discovery time from ~1 minute on a
            // typical Windows system (10+ COM ports each timing out at ~5s)
            // to <1s by skipping every non-DAQiFi port without opening it.
            // It also stops the previous behavior of sending SCPI commands
            // to other vendors' COM ports (Bluetooth radios, GPS, etc.).
            // Ports the descriptor provider can't classify fall through to
            // legacy probing, matched only by name-pattern in FilterProbableDaqifiPorts.
            var allPorts = _portNameProvider?.Invoke()
                ?? SerialStreamTransport.GetAvailablePortNames();
            var nameFilteredPorts = FilterProbableDaqifiPorts(allPorts);
            var availablePorts = FilterByUsbDescriptor(nameFilteredPorts).ToList();

            // Probe candidate ports in parallel (issue #157). Each SerialPort
            // is its own physical resource so concurrent opens are safe; the
            // VID/PID pre-filter typically leaves only 0-1 candidates anyway,
            // but the legacy fallback (null descriptor) can still surface
            // multiple unclassifiable ports — probing them sequentially adds
            // ~1.2s per port (DeviceWakeUpDelayMs + ResponseTimeoutMs).
            //
            // Cap concurrency at MaxParallelProbes so a 20-port system with
            // a missing descriptor provider doesn't open 20 serial handles at
            // once — most platforms throttle quietly above ~8 concurrent opens.
            var maxConcurrency = Math.Max(1, Math.Min(MaxParallelProbes, availablePorts.Count));
            using var probeGate = new SemaphoreSlim(maxConcurrency, maxConcurrency);

            var probeTasks = availablePorts.Select(async portName =>
            {
                await probeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return await ProbeSafelyAsync(portName, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    probeGate.Release();
                }
            }).ToList();

            // Catch OCE here so the DiscoveryCompleted event always fires
            // (callers expect a "discovery pass terminated" signal regardless
            // of how it ended). ProbeSafelyAsync rethrows OCE on caller
            // cancellation so WhenAll short-circuits the rest of the set —
            // we then settle for whatever probes already finished cleanly.
            IDeviceInfo?[]? probeResults = null;
            try
            {
                probeResults = await Task.WhenAll(probeTasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Drain any tasks that did finish successfully before cancellation.
                probeResults = probeTasks
                    .Where(t => t.Status == TaskStatus.RanToCompletion)
                    .Select(t => t.Result)
                    .ToArray();
            }

            foreach (var deviceInfo in probeResults)
            {
                if (deviceInfo != null)
                {
                    discoveredDevices.Add(deviceInfo);
                    OnDeviceDiscovered(deviceInfo);
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
    /// <returns>Device info if a DAQiFi device responds, null otherwise. Swallows
    /// non-cancellation exceptions so a single failing port doesn't tear down the
    /// whole concurrent probe pass; cancellation propagates so Task.WhenAll
    /// short-circuits when the caller's token is canceled.</returns>
    private async Task<IDeviceInfo?> ProbeSafelyAsync(string portName, CancellationToken cancellationToken)
    {
        try
        {
            return await TryGetDeviceInfoAsync(portName, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller asked to stop — propagate so Task.WhenAll observes the
            // cancellation and short-circuits the rest of the probe set.
            throw;
        }
        catch
        {
            // Probe failure (port locked, no response, IO error, etc.) is
            // expected for non-DAQiFi serial devices — keep the rest of the
            // concurrent probe set going and return no device for this port.
            return null;
        }
    }

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

            // Identity-only probe: just GetDeviceInfo (closes #157). The
            // previous DisableEcho / StopStreaming / TurnDeviceOn /
            // SetProtobufStreamFormat sequence was for connection setup,
            // not identification — a healthy DAQiFi answers SYSTem:SYSInfoPB?
            // immediately regardless of stream format or power state. Caller
            // (the consumer setting up an actual connection) is responsible
            // for any setup commands they need.
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
    /// Filters the post-name-filter ports down to those whose USB descriptor
    /// matches a known DAQiFi VID/PID. Ports the provider can't classify
    /// (returns null) fall through to probing — preserves cross-platform
    /// behavior where the provider doesn't have a real impl.
    /// </summary>
    private IEnumerable<string> FilterByUsbDescriptor(IEnumerable<string> ports)
    {
        foreach (var port in ports)
        {
            UsbPortDescriptor? descriptor;
            try
            {
                descriptor = _usbPortDescriptorProvider.GetDescriptor(port);
            }
            catch
            {
                // A misbehaving descriptor provider must never block discovery.
                // The shipped providers already swallow their own errors, but
                // a custom IUsbPortDescriptorProvider could throw — fall back
                // to legacy probing rather than aborting the whole scan.
                descriptor = null;
            }

            if (descriptor == null)
            {
                // No classification available — preserve legacy behavior
                // and probe the port (caller's name filter has already
                // removed the obvious non-candidates).
                yield return port;
                continue;
            }
            if (DaqifiUsbIds.IsDaqifiCdcDevice(descriptor))
            {
                yield return port;
            }
            // else: not a DAQiFi device — skip without opening / probing.
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
