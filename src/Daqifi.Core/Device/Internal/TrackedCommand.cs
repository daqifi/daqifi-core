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
internal sealed class TrackedCommand(Action wentOut, Action? notSent = null) : ITrackedSend
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
        send();
        WentOut();
    }

    void ITrackedSend.Replay(Action send)
    {
        send();
        WentOut();
    }

    void ITrackedSend.NotSent(Exception? reason) => NotSent();

    /// <summary>Sends <paramref name="message"/> as this command.</summary>
    internal void Send(IDeviceOperationHost host, IOutboundMessage<string> message)
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
internal sealed class TrackedMessage(IOutboundMessage<string> message, TrackedCommand command) : IOutboundMessage<string>
{
    /// <summary>What hears what becomes of the message.</summary>
    internal TrackedCommand Command => command;

    /// <inheritdoc />
    public string Data => message.Data;

    /// <inheritdoc />
    public byte[] GetBytes() => message.GetBytes();
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
internal sealed class CommandedState<T>(Func<T> get, Action<T> record)
{
    /// <summary>Guards the list below. Taken before the channels lock, and never held across a send.</summary>
    private readonly Lock _lock = new();

    /// <summary>
    /// The commands that have not gone out yet, in the order they were sent, with what they ask for.
    /// </summary>
    private readonly List<(TrackedCommand Command, T State)> _outstanding = new();

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
                return _outstanding.Count == 0 ? get() : _outstanding[^1].State;
            }
        }
    }

    /// <summary>Makes the command for <paramref name="state"/> the one sent last.</summary>
    internal TrackedCommand Command(T state)
    {
        TrackedCommand command = null!;
        command = new TrackedCommand(
            () =>
            {
                lock (_lock)
                {
                    record(state);

                    // What was sent before it is no longer what the next command builds on.
                    _outstanding.RemoveRange(0, _outstanding.FindIndex(other => other.Command == command) + 1);
                }
            },
            () =>
            {
                lock (_lock)
                {
                    _outstanding.RemoveAll(other => other.Command == command);
                }
            });

        lock (_lock)
        {
            _outstanding.Add((command, state));
        }

        return command;
    }
}
