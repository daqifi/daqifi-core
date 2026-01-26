using System.Net;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device.Discovery;

#nullable enable

namespace Daqifi.Core.Device;

/// <summary>
/// Default baud rate for serial connections.
/// </summary>
internal static class SerialDefaults
{
    public const int BaudRate = 9600;
}

/// <summary>
/// Factory class for creating and connecting to DAQiFi devices with a simplified API.
/// Provides a single-call connection interface that handles transport creation, connection,
/// and device initialization.
/// </summary>
public static class DaqifiDeviceFactory
{
    /// <summary>
    /// Connects to a DAQiFi device over TCP asynchronously using a hostname.
    /// </summary>
    /// <param name="host">The hostname or IP address string to connect to.</param>
    /// <param name="port">The TCP port to connect to.</param>
    /// <param name="options">Optional connection options. If null, uses default options.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <returns>A connected and optionally initialized <see cref="DaqifiDevice"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when host is null or empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when port is not between 1 and 65535.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
    public static async Task<DaqifiDevice> ConnectTcpAsync(
        string host,
        int port,
        DeviceConnectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentNullException(nameof(host), "Host cannot be null or empty.");
        }

        ValidatePort(port);

        cancellationToken.ThrowIfCancellationRequested();

        var effectiveOptions = options ?? DeviceConnectionOptions.Default;
        var transport = new TcpStreamTransport(host, port);

        return await ConnectWithTransportAsync(transport, effectiveOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Connects to a DAQiFi device over TCP asynchronously using an IP address.
    /// </summary>
    /// <param name="ipAddress">The IP address to connect to.</param>
    /// <param name="port">The TCP port to connect to.</param>
    /// <param name="options">Optional connection options. If null, uses default options.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <returns>A connected and optionally initialized <see cref="DaqifiDevice"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when ipAddress is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when port is not between 1 and 65535.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
    public static async Task<DaqifiDevice> ConnectTcpAsync(
        IPAddress ipAddress,
        int port,
        DeviceConnectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (ipAddress == null)
        {
            throw new ArgumentNullException(nameof(ipAddress));
        }

        ValidatePort(port);

        cancellationToken.ThrowIfCancellationRequested();

        var effectiveOptions = options ?? DeviceConnectionOptions.Default;
        var transport = new TcpStreamTransport(ipAddress, port);

        return await ConnectWithTransportAsync(transport, effectiveOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Connects to a DAQiFi device over TCP synchronously using a hostname.
    /// </summary>
    /// <param name="host">The hostname or IP address string to connect to.</param>
    /// <param name="port">The TCP port to connect to.</param>
    /// <param name="options">Optional connection options. If null, uses default options.</param>
    /// <returns>A connected and optionally initialized <see cref="DaqifiDevice"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when host is null or empty.</exception>
    public static DaqifiDevice ConnectTcp(
        string host,
        int port,
        DeviceConnectionOptions? options = null)
    {
        return ConnectTcpAsync(host, port, options, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Connects to a DAQiFi device over TCP synchronously using an IP address.
    /// </summary>
    /// <param name="ipAddress">The IP address to connect to.</param>
    /// <param name="port">The TCP port to connect to.</param>
    /// <param name="options">Optional connection options. If null, uses default options.</param>
    /// <returns>A connected and optionally initialized <see cref="DaqifiDevice"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when ipAddress is null.</exception>
    public static DaqifiDevice ConnectTcp(
        IPAddress ipAddress,
        int port,
        DeviceConnectionOptions? options = null)
    {
        return ConnectTcpAsync(ipAddress, port, options, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Connects to a DAQiFi device over a serial port asynchronously using the default baud rate (9600).
    /// </summary>
    /// <param name="portName">The name of the serial port (e.g., "COM3", "/dev/ttyUSB0").</param>
    /// <param name="options">Optional connection options. If null, uses default options.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <returns>A connected and optionally initialized <see cref="DaqifiDevice"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when portName is null or empty.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
    public static Task<DaqifiDevice> ConnectSerialAsync(
        string portName,
        DeviceConnectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return ConnectSerialAsync(portName, SerialDefaults.BaudRate, options, cancellationToken);
    }

    /// <summary>
    /// Connects to a DAQiFi device over a serial port asynchronously with a specified baud rate.
    /// </summary>
    /// <param name="portName">The name of the serial port (e.g., "COM3", "/dev/ttyUSB0").</param>
    /// <param name="baudRate">The baud rate for the serial connection.</param>
    /// <param name="options">Optional connection options. If null, uses default options.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <returns>A connected and optionally initialized <see cref="DaqifiDevice"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when portName is null or empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when baudRate is less than or equal to zero.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
    public static async Task<DaqifiDevice> ConnectSerialAsync(
        string portName,
        int baudRate,
        DeviceConnectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (portName == null)
        {
            throw new ArgumentNullException(nameof(portName));
        }

        if (string.IsNullOrWhiteSpace(portName))
        {
            throw new ArgumentException("Port name cannot be empty or whitespace.", nameof(portName));
        }

        if (baudRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baudRate), baudRate, "Baud rate must be greater than zero.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var effectiveOptions = options ?? DeviceConnectionOptions.Default;
        var transport = new SerialStreamTransport(portName, baudRate);

        return await ConnectWithTransportAsync(transport, effectiveOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Connects to a DAQiFi device over a serial port synchronously using the default baud rate (9600).
    /// </summary>
    /// <param name="portName">The name of the serial port (e.g., "COM3", "/dev/ttyUSB0").</param>
    /// <param name="options">Optional connection options. If null, uses default options.</param>
    /// <returns>A connected and optionally initialized <see cref="DaqifiDevice"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when portName is null or empty.</exception>
    public static DaqifiDevice ConnectSerial(
        string portName,
        DeviceConnectionOptions? options = null)
    {
        return ConnectSerialAsync(portName, SerialDefaults.BaudRate, options, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Connects to a DAQiFi device over a serial port synchronously with a specified baud rate.
    /// </summary>
    /// <param name="portName">The name of the serial port (e.g., "COM3", "/dev/ttyUSB0").</param>
    /// <param name="baudRate">The baud rate for the serial connection.</param>
    /// <param name="options">Optional connection options. If null, uses default options.</param>
    /// <returns>A connected and optionally initialized <see cref="DaqifiDevice"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when portName is null or empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when baudRate is less than or equal to zero.</exception>
    public static DaqifiDevice ConnectSerial(
        string portName,
        int baudRate,
        DeviceConnectionOptions? options = null)
    {
        return ConnectSerialAsync(portName, baudRate, options, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Connects to a DAQiFi device asynchronously using device discovery information.
    /// </summary>
    /// <param name="deviceInfo">The device information from discovery.</param>
    /// <param name="options">Optional connection options. If null, uses default options.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <returns>A connected and optionally initialized <see cref="DaqifiDevice"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when deviceInfo is null.</exception>
    /// <exception cref="ArgumentException">Thrown when deviceInfo is missing required fields (IPAddress or Port for WiFi connections).</exception>
    /// <exception cref="NotSupportedException">Thrown when the connection type is not supported (e.g., Serial, HID).</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
    public static async Task<DaqifiDevice> ConnectFromDeviceInfoAsync(
        IDeviceInfo deviceInfo,
        DeviceConnectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (deviceInfo == null)
        {
            throw new ArgumentNullException(nameof(deviceInfo));
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Handle connection type
        switch (deviceInfo.ConnectionType)
        {
            case ConnectionType.WiFi:
                return await ConnectWiFiDeviceAsync(deviceInfo, options, cancellationToken)
                    .ConfigureAwait(false);

            case ConnectionType.Serial:
                return await ConnectSerialDeviceAsync(deviceInfo, options, cancellationToken)
                    .ConfigureAwait(false);

            case ConnectionType.Hid:
                throw new NotSupportedException(
                    "HID device connections are not supported by the factory. " +
                    "HID is only used for bootloader mode.");

            default:
                throw new NotSupportedException(
                    $"Connection type '{deviceInfo.ConnectionType}' is not supported.");
        }
    }

    /// <summary>
    /// Connects to a DAQiFi device synchronously using device discovery information.
    /// </summary>
    /// <param name="deviceInfo">The device information from discovery.</param>
    /// <param name="options">Optional connection options. If null, uses default options.</param>
    /// <returns>A connected and optionally initialized <see cref="DaqifiDevice"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when deviceInfo is null.</exception>
    /// <exception cref="ArgumentException">Thrown when deviceInfo is missing required fields (IPAddress or Port for WiFi connections).</exception>
    /// <exception cref="NotSupportedException">Thrown when the connection type is not supported (e.g., Serial, HID).</exception>
    public static DaqifiDevice ConnectFromDeviceInfo(
        IDeviceInfo deviceInfo,
        DeviceConnectionOptions? options = null)
    {
        return ConnectFromDeviceInfoAsync(deviceInfo, options, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Connects to a WiFi device using the provided device info.
    /// </summary>
    private static async Task<DaqifiDevice> ConnectWiFiDeviceAsync(
        IDeviceInfo deviceInfo,
        DeviceConnectionOptions? options,
        CancellationToken cancellationToken)
    {
        if (deviceInfo.IPAddress == null)
        {
            throw new ArgumentException(
                "DeviceInfo must have an IPAddress for WiFi connections.",
                nameof(deviceInfo));
        }

        if (!deviceInfo.Port.HasValue)
        {
            throw new ArgumentException(
                "DeviceInfo must have a Port for WiFi connections.",
                nameof(deviceInfo));
        }

        var effectiveOptions = options ?? DeviceConnectionOptions.Default;

        // Use the device name from the discovery info if not overridden in options
        var deviceName = effectiveOptions.DeviceName == DeviceConnectionOptions.Default.DeviceName
            && !string.IsNullOrWhiteSpace(deviceInfo.Name)
            ? deviceInfo.Name
            : effectiveOptions.DeviceName;

        var modifiedOptions = new DeviceConnectionOptions
        {
            DeviceName = deviceName,
            ConnectionRetry = effectiveOptions.ConnectionRetry,
            InitializeDevice = effectiveOptions.InitializeDevice
        };

        return await ConnectTcpAsync(
            deviceInfo.IPAddress,
            deviceInfo.Port.Value,
            modifiedOptions,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Connects to a serial device using the provided device info.
    /// </summary>
    private static async Task<DaqifiDevice> ConnectSerialDeviceAsync(
        IDeviceInfo deviceInfo,
        DeviceConnectionOptions? options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deviceInfo.PortName))
        {
            throw new ArgumentException(
                "DeviceInfo must have a PortName for Serial connections.",
                nameof(deviceInfo));
        }

        var effectiveOptions = options ?? DeviceConnectionOptions.Default;

        // Use the device name from the discovery info if not overridden in options
        var deviceName = effectiveOptions.DeviceName == DeviceConnectionOptions.Default.DeviceName
            && !string.IsNullOrWhiteSpace(deviceInfo.Name)
            ? deviceInfo.Name
            : effectiveOptions.DeviceName;

        var modifiedOptions = new DeviceConnectionOptions
        {
            DeviceName = deviceName,
            ConnectionRetry = effectiveOptions.ConnectionRetry,
            InitializeDevice = effectiveOptions.InitializeDevice
        };

        return await ConnectSerialAsync(
            deviceInfo.PortName,
            modifiedOptions,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates that a port number is within the valid TCP port range.
    /// </summary>
    /// <param name="port">The port number to validate.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when port is not between 1 and 65535.</exception>
    private static void ValidatePort(int port)
    {
        if (port < 1 || port > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be between 1 and 65535.");
        }
    }

    /// <summary>
    /// Internal method that handles the actual connection logic with a transport.
    /// </summary>
    private static async Task<DaqifiDevice> ConnectWithTransportAsync(
        IStreamTransport transport,
        DeviceConnectionOptions options,
        CancellationToken cancellationToken)
    {
        DaqifiDevice? device = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Step 1: Connect the transport
            await transport.ConnectAsync(options.ConnectionRetry).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            // Step 2: Create the device with the transport
            // Note: Once created, the device owns the transport and will dispose it
            device = new DaqifiDevice(options.DeviceName, transport);

            // Step 3: Connect the device (starts message producers/consumers)
            device.Connect();

            cancellationToken.ThrowIfCancellationRequested();

            // Step 4: Initialize the device if requested
            if (options.InitializeDevice)
            {
                await device.InitializeAsync().ConfigureAwait(false);
            }

            return device;
        }
        catch
        {
            // Clean up on failure
            // If device was created, it owns the transport and will dispose it
            if (device != null)
            {
                device.Dispose();
            }
            else
            {
                // Device was not created, so we must dispose the transport
                transport.Dispose();
            }
            throw;
        }
    }
}
