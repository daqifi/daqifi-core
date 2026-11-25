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
    private readonly Action<DeviceStateChangedEventArgs> _raise;

    /// <summary>A leaf lock: never held across <see cref="_raise"/> or any other call out.</summary>
    private readonly Lock _lock = new();

    private readonly Queue<Change> _pending = new();

    private bool _isDelivering;

    /// <summary>Creates a queue.</summary>
    /// <param name="raise">Raises the event for one change. Must not throw.</param>
    internal StateChangeQueue(Action<DeviceStateChangedEventArgs> raise)
    {
        _raise = raise ?? throw new ArgumentNullException(nameof(raise));
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
        lock (_lock)
        {
            change.IsPublished = true;
            if (_isDelivering)
            {
                return;
            }

            _isDelivering = true;
        }

        while (true)
        {
            Change next;
            lock (_lock)
            {
                // A head that is not published yet stops the line. Its author publishes it, and
                // delivers it along with everything that queued up behind it.
                if (!_pending.TryPeek(out var head) || !head.IsPublished)
                {
                    _isDelivering = false;
                    return;
                }

                next = _pending.Dequeue();
            }

            _raise(new DeviceStateChangedEventArgs(next.Previous, next.Current));
        }
    }
}
