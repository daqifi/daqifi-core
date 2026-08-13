using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Device.Internal;
using Daqifi.Core.Firmware;

#nullable enable

namespace Daqifi.Core.Device
{
    /// <summary>
    /// The <see cref="ILanChipInfoProvider"/> implementation, extracted from
    /// <see cref="DaqifiStreamingDevice"/> so the device delegates rather than hosts it.
    /// </summary>
    internal sealed class LanChipInfoOperations : ILanChipInfoProvider
    {
        private readonly IDeviceOperationHost _host;

        internal LanChipInfoOperations(IDeviceOperationHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }

        /// <inheritdoc />
        public async Task<LanChipInfo?> GetLanChipInfoAsync(CancellationToken cancellationToken = default)
        {
            _host.EnsureConnected();

            var lines = await _host.ExecuteTextCommandAsync(
                () => _host.Send(ScpiMessageProducer.GetLanChipInfo),
                responseTimeoutMs: 2000,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (LanChipInfoParser.TryParseLines(lines, out var info))
            {
                return info;
            }

            // Closes #203: LAN:ENAbled=1 in saved settings but the WINC1500 state
            // machine hasn't reached INITIALIZED yet (steady-state, not the
            // post-reboot transient #144 already retries for) makes GETChipInfo?
            // return this specific SCPI error instead of JSON. Surface it distinctly
            // so the caller's retry loop can react (kick LAN:APPLY) instead of just
            // waiting out a blind delay.
            var errorLine = lines.LastOrDefault(ScpiResponseClassifier.IsScpiErrorLine);
            if (errorLine != null && ScpiResponseClassifier.TryExtractErrorCode(errorLine, out var errorCode) && errorCode == -200)
            {
                throw new LanNotInitializedException(errorLine.Trim());
            }

            return null;
        }
    }
}
