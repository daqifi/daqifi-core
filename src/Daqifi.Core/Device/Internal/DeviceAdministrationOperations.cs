using Daqifi.Core.Communication.Producers;
using System;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace Daqifi.Core.Device.Internal
{
    /// <summary>
    /// The device-administration half of <see cref="IStreamingDevice"/> — reboot, the ADC
    /// calibration banks, voltage-precision persistence, and the friendly-name write — extracted
    /// from <see cref="DaqifiStreamingDevice"/> (#344) so the device delegates rather than hosts it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are fire-and-forget SCPI commands with no reply to parse: each validates its arguments,
    /// checks the connection, and sends. They are grouped here because they share that shape and
    /// because none of them touches the channel collection, the streaming session, or any device
    /// state — the two exceptions being <see cref="Reboot"/>'s local teardown and
    /// <see cref="SetFriendlyNameAsync"/>'s optimistic metadata write, both of which go back through
    /// the host rather than being done here.
    /// </para>
    /// <para>
    /// Everything reaches the device through <see cref="IDeviceOperationHost"/>, so each command
    /// still passes through the device's own virtual <c>Send</c> and any subclass override of it.
    /// </para>
    /// </remarks>
    internal sealed class DeviceAdministrationOperations
    {
        private readonly IDeviceOperationHost _host;

        internal DeviceAdministrationOperations(IDeviceOperationHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        /// <inheritdoc cref="DaqifiStreamingDevice.SetFriendlyNameAsync(string, CancellationToken)" />
        internal Task SetFriendlyNameAsync(string name, CancellationToken cancellationToken = default)
        {
            if (name is null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            if (!ScpiMessageProducer.IsFriendlyNameValid(name))
            {
                throw new ArgumentException(
                    $"Device name must be 1-{ScpiMessageProducer.MaxFriendlyNameLength} printable ASCII characters and cannot contain '\"' or '\\'.",
                    nameof(name));
            }

            if (!_host.IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            cancellationToken.ThrowIfCancellationRequested();

            _host.Send(ScpiMessageProducer.SetDeviceName(name));
            _host.Send(ScpiMessageProducer.SaveDeviceName);
            _host.Metadata.FriendlyName = name;

            return Task.CompletedTask;
        }

        /// <inheritdoc cref="IStreamingDevice.Reboot" />
        internal void Reboot()
        {
            if (!_host.IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            _host.Send(ScpiMessageProducer.RebootDevice);

            // The device drops its link while restarting, so tear down the local
            // connection rather than leaving a stale one that reports Connected.
            _host.Disconnect();
        }

        /// <inheritdoc cref="IStreamingDevice.SaveAdcCalibration" />
        internal void SaveAdcCalibration()
        {
            if (!_host.IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            _host.Send(ScpiMessageProducer.SaveAdcCalibration);
        }

        /// <inheritdoc cref="IStreamingDevice.LoadAdcCalibration" />
        internal void LoadAdcCalibration()
        {
            if (!_host.IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            _host.Send(ScpiMessageProducer.LoadAdcCalibration);
        }

        /// <inheritdoc cref="IStreamingDevice.SetAdcCalibrationSlope" />
        internal void SetAdcCalibrationSlope(int channelNumber, double calM)
        {
            if (channelNumber < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(channelNumber), channelNumber, "Channel number cannot be negative.");
            }

            if (!_host.IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            _host.Send(ScpiMessageProducer.SetAdcCalibrationSlope(channelNumber, calM));
        }

        /// <inheritdoc cref="IStreamingDevice.SetAdcCalibrationOffset" />
        internal void SetAdcCalibrationOffset(int channelNumber, double calB)
        {
            if (channelNumber < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(channelNumber), channelNumber, "Channel number cannot be negative.");
            }

            if (!_host.IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            _host.Send(ScpiMessageProducer.SetAdcCalibrationOffset(channelNumber, calB));
        }

        /// <inheritdoc cref="IStreamingDevice.SaveFactoryAdcCalibration" />
        internal void SaveFactoryAdcCalibration()
        {
            if (!_host.IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            _host.Send(ScpiMessageProducer.SaveFactoryAdcCalibration);
        }

        /// <inheritdoc cref="IStreamingDevice.LoadFactoryAdcCalibration" />
        internal void LoadFactoryAdcCalibration()
        {
            if (!_host.IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            _host.Send(ScpiMessageProducer.LoadFactoryAdcCalibration);
        }

        /// <inheritdoc cref="IStreamingDevice.UseAdcCalibration" />
        internal void UseAdcCalibration(int bank)
        {
            if (bank is < 0 or > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(bank), bank, "Calibration bank must be 0 (factory) or 1 (user).");
            }

            if (!_host.IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            _host.Send(ScpiMessageProducer.UseAdcCalibration(bank));
        }

        /// <inheritdoc cref="IStreamingDevice.SaveVoltagePrecision" />
        internal void SaveVoltagePrecision()
        {
            if (!_host.IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            _host.Send(ScpiMessageProducer.SaveVoltagePrecision);
        }

        /// <inheritdoc cref="IStreamingDevice.LoadVoltagePrecision" />
        internal void LoadVoltagePrecision()
        {
            if (!_host.IsConnected)
            {
                throw new DeviceNotConnectedException();
            }

            _host.Send(ScpiMessageProducer.LoadVoltagePrecision);
        }
    }
}
