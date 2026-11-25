using System;
using System.Collections.Generic;
using System.Threading;

namespace Daqifi.Core.Device.Internal;

/// <summary>
/// Delivers a device's state changes to its subscribers in the order they happened, whichever
/// threads they happened on, and with no lock held (issue #1035).
/// </summary>
/// <remarks>
/// <para>
/// Several threads legitimately move one device: a caller, the reader, a watchdog, the reconnect
/// loop. Each used to raise its own event after leaving the lock it changed the state under, so a
/// drop that landed just after an initialization finished could be announced before it, and a
/// subscriber that believes the last event it saw was left believing <c>Ready</c> on a dead device.
/// A handler that changed the state itself did the same to every subscriber after it.
/// </para>
/// <para>
/// A change takes its place with <see cref="Enqueue"/>, under the lock that makes the change, and is
/// released for delivery with <see cref="Publish"/>, once its author has settled whatever has to be
/// settled before a handler may run. One thread delivers at a time; a change published while another
/// thread is delivering, or from inside a handler, is delivered by that thread, after what it is
/// delivering now. Nobody ever waits for a delivery, so a slow handler delays later events and
/// nothing else.
/// </para>
/// </remarks>
internal sealed class StateChangeQueue
{
    private readonly object _sender;
    private readonly Func<EventHandler<DeviceStateChangedEventArgs>?> _subscribers;
    private readonly Action<Exception> _onSubscriberError;

    /// <summary>A leaf lock: never held across a subscriber or any other call out.</summary>
    private readonly Lock _lock = new();

    private readonly Queue<Change> _pending = new();

    private bool _isDelivering;

    /// <summary>Creates a queue.</summary>
    /// <param name="sender">The device the events are raised for.</param>
    /// <param name="subscribers">Reads the event's current subscribers.</param>
    /// <param name="onSubscriberError">Reports a subscriber that threw. Must not throw.</param>
    internal StateChangeQueue(
        object sender,
        Func<EventHandler<DeviceStateChangedEventArgs>?> subscribers,
        Action<Exception> onSubscriberError)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _subscribers = subscribers ?? throw new ArgumentNullException(nameof(subscribers));
        _onSubscriberError = onSubscriberError ?? throw new ArgumentNullException(nameof(onSubscriberError));
    }

    /// <summary>
    /// One state change and its place in line. Every one handed out must reach
    /// <see cref="Publish"/>: until it does, the changes behind it wait.
    /// </summary>
    internal sealed class Change(DeviceState previous, DeviceState current)
    {
        internal DeviceState Previous { get; } = previous;

        internal DeviceState Current { get; } = current;

        /// <summary>Guarded by the queue's lock.</summary>
        internal bool IsPublished { get; set; }
    }

    /// <summary>
    /// Gives a change its place in line. Call it under the lock the state is written under, so the
    /// order here is the order the changes were made in.
    /// </summary>
    internal Change Enqueue(DeviceState previous, DeviceState current)
    {
        var change = new Change(previous, current);
        lock (_lock)
        {
            _pending.Enqueue(change);
        }

        return change;
    }

    /// <summary>
    /// Releases a change for delivery and delivers everything that is ready, unless another caller
    /// is already doing that. Call it with no lock held.
    /// </summary>
    internal void Publish(Change change)
    {
        MarkPublished(change);
        Deliver();
    }

    /// <summary>
    /// As <see cref="Publish"/>, but stops after <paramref name="change"/>: for a caller in the middle
    /// of an operation, which must not deliver what others publish meanwhile. The caller calls
    /// <see cref="Deliver"/> once that operation is over.
    /// </summary>
    internal void PublishAlone(Change change)
    {
        MarkPublished(change);
        DeliverThrough(change);
    }

    /// <summary>Delivers everything that is ready, unless another caller is already doing that.</summary>
    internal void Deliver() => DeliverThrough(last: null);

    private void MarkPublished(Change change)
    {
        lock (_lock)
        {
            change.IsPublished = true;
        }
    }

    private void DeliverThrough(Change? last)
    {
        lock (_lock)
        {
            if (_isDelivering)
            {
                return;
            }

            _isDelivering = true;
        }

        Change? delivered = null;
        while (true)
        {
            lock (_lock)
            {
                // A head that is not published yet stops the line. Its author publishes it, and
                // delivers it along with everything that queued up behind it.
                if ((last != null && delivered == last) || !_pending.TryPeek(out var head) || !head.IsPublished)
                {
                    _isDelivering = false;
                    return;
                }

                delivered = _pending.Dequeue();
            }

            Raise(new DeviceStateChangedEventArgs(delivered.Previous, delivered.Current));
        }
    }

    /// <summary>
    /// Calls each subscriber on its own, so one that throws costs neither the others nor whoever
    /// made the change (issue #494).
    /// </summary>
    private void Raise(DeviceStateChangedEventArgs args)
    {
        foreach (var subscriber in Delegate.EnumerateInvocationList(_subscribers()))
        {
            try
            {
                subscriber(_sender, args);
            }
            catch (Exception ex)
            {
                _onSubscriberError(ex);
            }
        }
    }
}
