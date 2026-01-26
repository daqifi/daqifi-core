using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Communication.Transport;

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Discovers DAQiFi devices connected via USB/Serial ports.
/// </summary>
public class SerialDeviceFinder : IDeviceFinder, IDisposable
{
    #region Constants

    private const int DefaultBaudRate = 9600;
    private static readonly TimeSpan QuickProbeTimeout = TimeSpan.FromMilliseconds(500);

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
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to abort the operation.</param>
    /// <returns>A task containing the collection of discovered devices.</returns>
    public async Task<IEnumerable<IDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // Prevent concurrent discovery operations
        await _discoverySemaphore.WaitAsync(cancellationToken);
        try
        {
            var discoveredDevices = new List<IDeviceInfo>();
            var availablePorts = SerialStreamTransport.GetAvailablePortNames();

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
    /// Attempts to get device information from a serial port with a quick probe.
    /// </summary>
    /// <param name="portName">The serial port name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Device info if successful, null otherwise.</returns>
    private async Task<IDeviceInfo?> TryGetDeviceInfoAsync(string portName, CancellationToken cancellationToken)
    {
        // For now, we create a basic device info without probing
        // Full device info retrieval would require implementing SCPI command/response
        // This will be enhanced in Phase 6 when protocol implementation is complete

        // Create a minimal device info representing the discovered serial port
        var deviceInfo = new DeviceInfo
        {
            Name = portName,
            SerialNumber = "Unknown", // Will be populated when device is connected
            FirmwareVersion = "Unknown",
            ConnectionType = ConnectionType.Serial,
            PortName = portName,
            Type = DeviceType.Unknown,
            IsPowerOn = true
        };

        return await Task.FromResult(deviceInfo);
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
