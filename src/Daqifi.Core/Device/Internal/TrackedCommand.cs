using Daqifi.Core.Communication.Messages;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Daqifi.Core.Device.Internal;

/// <summary>
/// A command that records what it does when it goes out: at once, or when its turn comes if it is
/// held back behind another operation, and not at all if it is dropped.
/// </summary>
/// <param name="wentOut">Runs once the command has gone out.</param>
/// <param name="notSent">Runs when it will not be.</param>
/// <param name="goingOut">Runs when it is about to go out.</param>
internal sealed class TrackedCommand(Action wentOut, Action? notSent = null, Action? goingOut = null) : ITrackedSend
{
    private int _finished;

    /// <summary>Counts the command as gone out, unless it is already known what became of it.</summary>
    internal void WentOut()
    {
        if (Interlocked.Exchange(ref _finished, 1) == 0)
        {
            wentOut();
        }
    }

    /// <summary>Counts the command as not sent, unless it is already known what became of it.</summary>
    internal void NotSent()
    {
        if (Interlocked.Exchange(ref _finished, 1) == 0)
        {
            notSent?.Invoke();
        }
    }

    void ITrackedSend.GoOut(Action send)
    {
        goingOut?.Invoke();
        send();
        WentOut();
    }

    void ITrackedSend.Replay(Action send)
    {
        goingOut?.Invoke();
        send();
        WentOut();
    }

    void ITrackedSend.NotSent(Exception? reason) => NotSent();

    /// <summary>Sends <paramref name="message"/> as this command.</summary>
    internal void Send(IDeviceOperationHost host, IOutboundMessage<string> message) => Send(host, () => message);

    /// <summary>Sends what <paramref name="message"/> gives when the command goes out as this command.</summary>
    internal void Send(IDeviceOperationHost host, Func<IOutboundMessage<string>> message)
    {
        try
        {
            host.Send(new TrackedMessage(message, this));
        }
        catch
        {
            NotSent();
            throw;
        }

        // An override of Send can have sent it some other way, or not at all.
        if (!host.HasDeferredSends)
        {
            WentOut();
        }
    }
}

/// <summary>
/// The message of a <see cref="TrackedCommand"/>. It carries the command, so that an override of
/// <see cref="DaqifiDevice.Send{T}"/> that passes the message on passes that on too.
/// </summary>
internal sealed class TrackedMessage(Func<IOutboundMessage<string>> message, TrackedCommand command) : IOutboundMessage<string>
{
    /// <summary>What hears what becomes of the message.</summary>
    internal TrackedCommand Command => command;

    /// <inheritdoc />
    public string Data => message().Data;

    /// <inheritdoc />
    public byte[] GetBytes() => message().GetBytes();
}

/// <summary>
/// A piece of device state that Core records when the command for it goes out, and what has been
/// requested of it while one is held back.
/// </summary>
/// <remarks>
/// The two differ only meanwhile. What is recorded is what the device has, which is what the decoder
/// and every reader go by; what is requested is what the next command is worked out from, such as an
/// ADC mask from every enabled channel, unless it goes out ahead of what is held back. A command that
/// is dropped changes neither.
/// </remarks>
/// <param name="get">Reads the recorded state.</param>
/// <param name="record">Records the state.</param>
/// <param name="takesEffect">
/// Whether the device takes a command for this state as it goes out; <c>null</c> for always. One it
/// refuses is dropped, as if it had not been sent.
/// </param>
internal sealed class CommandedState<T>(Func<T> get, Action<T> record, Func<bool>? takesEffect = null)
{
    /// <summary>Guards the list below. Taken before the channels lock, and never held across a send.</summary>
    private readonly Lock _lock = new();

    /// <summary>The commands that have not gone out yet, in the order they were sent.</summary>
    private readonly List<Outstanding> _outstanding = new();

    /// <summary>A command that has not gone out yet.</summary>
    private sealed class Outstanding(T state)
    {
        /// <summary>What the command sets.</summary>
        internal T Sets { get; set; } = state;

        /// <summary>The state there will be once the command, and what went out ahead of it, have gone out.</summary>
        internal T Requested { get; set; } = state;

        /// <summary>Whether what it sets stays as it is.</summary>
        internal bool IsFixed { get; set; }
    }

    /// <summary>The recorded state.</summary>
    internal T Recorded => get();

    /// <summary>
    /// What the last command asked for, or the recorded state when none is outstanding: the state
    /// there will be when a command that is held back too goes out.
    /// </summary>
    internal T Requested
    {
        get
        {
            lock (_lock)
            {
                return _outstanding.Count == 0 ? get() : _outstanding[^1].Requested;
            }
        }
    }

    /// <summary>
    /// Makes the command for <paramref name="state"/> the one sent last.
    /// </summary>
    /// <param name="state">What the command sets.</param>
    /// <param name="change">
    /// What it changes, for a state that the next command is worked out from. It is applied to the
    /// commands this one goes out ahead of, so that they do not undo it.
    /// </param>
    internal TrackedCommand Command(T state, Func<T, T>? change = null)
        => Command(new Outstanding(state) { IsFixed = true }, change);

    /// <summary>
    /// Sends a command that sets what is there already with <paramref name="change"/> applied to it:
    /// to <paramref name="current"/>, and to what goes out ahead of the command if it is held back.
    /// </summary>
    internal void Send(IDeviceOperationHost host, T current, Func<T, T> change, Func<T, IOutboundMessage<string>> message)
    {
        var outstanding = new Outstanding(change(current));

        // Fails here if it cannot be sent at all.
        _ = message(outstanding.Sets);

        Command(outstanding, change).Send(host, () =>
        {
            lock (_lock)
            {
                return message(outstanding.Sets);
            }
        });
    }

    private TrackedCommand Command(Outstanding outstanding, Func<T, T>? change)
    {
        var command = new TrackedCommand(
            wentOut: () =>
            {
                lock (_lock)
                {
                    // A command the device refuses as it arrives changes nothing it has, so it is
                    // dropped as one that never went out: neither recorded nor built on.
                    if (takesEffect?.Invoke() == false)
                    {
                        _outstanding.Remove(outstanding);
                        return;
                    }

                    record(outstanding.Sets);

                    var index = _outstanding.IndexOf(outstanding);
                    _outstanding.RemoveAt(index);
                    for (var i = 0; change != null && i < index; i++)
                    {
                        var overtaken = _outstanding[i];
                        overtaken.Requested = change(overtaken.Requested);
                        if (!overtaken.IsFixed)
                        {
                            overtaken.Sets = overtaken.Requested;
                        }
                    }
                }
            },
            notSent: () =>
            {
                lock (_lock)
                {
                    _outstanding.Remove(outstanding);
                }
            },
            goingOut: () =>
            {
                lock (_lock)
                {
                    outstanding.IsFixed = true;
                }
            });

        lock (_lock)
        {
            _outstanding.Add(outstanding);
        }

        return command;
    }
}
