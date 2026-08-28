using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Microsoft.Extensions.Logging;
using System;

#nullable enable

namespace Daqifi.Core.Device.Internal;

/// <summary>
/// The slice of <see cref="DaqifiDevice"/> that <see cref="OperationSerializer"/> needs: the
/// write it replays a parked message through, the outbound queue it waits on, and the two
/// test-facing timing seams the device already exposed.
/// </summary>
/// <remarks>
/// <para>
/// Every member forwards to something the device already had. The device implements this
/// explicitly, so none of it widens the public API — the same arrangement
/// <see cref="ITextExchangeHost"/> and <see cref="IDeviceOperationHost"/> use.
/// </para>
/// <para>
/// The two seams are properties rather than values handed over at construction because they are
/// <c>internal virtual</c> on the device precisely so a test subclass can override them, and an
/// override set through an <c>init</c> property runs after the constructor. Reading them once up
/// front would capture the defaults and ignore every override — the same reason
/// <see cref="LifecycleGate"/> takes its timeouts as delegates.
/// </para>
/// </remarks>
internal interface IOperationSerializationHost
{
    /// <summary>
    /// The device's logger. The serializer wraps every call to it, so a throwing logger cannot
    /// take down an operation.
    /// </summary>
    ILogger Logger { get; }

    /// <inheritdoc cref="DaqifiDevice.MaxDeferredSends"/>
    int MaxDeferredSends { get; }

    /// <inheritdoc cref="DaqifiDevice.OutboundDrainWait"/>
    TimeSpan OutboundDrainWait { get; }

    /// <summary>
    /// The device's outbound queue, or <c>null</c> on a device that has none. Read fresh on
    /// every access: teardown nulls the field, and the drain barrier snapshots it once for
    /// exactly that reason.
    /// </summary>
    IMessageProducer<string>? MessageProducer { get; }

    /// <summary>
    /// Puts a message on its way to the device immediately — the body a deferred send is
    /// replayed through once the operation that parked it has finished.
    /// </summary>
    void SendNow<T>(IOutboundMessage<T> message);
}
