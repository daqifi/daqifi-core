using Daqifi.Core.Communication.Messages;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Daqifi.Core.Device.Internal;

/// <summary>
/// A text command that hears what becomes of it. The message itself carries that, so it survives an
/// override of <see cref="DaqifiDevice.Send{T}"/> that passes the message on.
/// </summary>
internal abstract class TrackedCommand(IOutboundMessage<string> message) : IOutboundMessage<string>, ITrackedSend
{
    /// <inheritdoc />
    public string Data => message.Data;

    /// <inheritdoc />
    public byte[] GetBytes() => message.GetBytes();

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

    /// <summary>The command has gone out.</summary>
    protected abstract void WentOut();

    /// <summary>The command was held back and will not be sent.</summary>
    protected abstract void NotSent();
}

/// <summary>
/// A piece of device state that Core records when it sends the command for it, and takes back when
/// that command is held back behind another operation and then dropped instead of sent.
/// </summary>
/// <remarks>
/// Recorded at once rather than when the command goes out, because the next command is worked out
/// from it: an ADC mask from every enabled channel, a digital write from whether PWM drives the pin.
/// Only the command sent last takes anything back, to what the device has, and only while nothing
/// else (a status message, a raw command) has moved the state since.
/// </remarks>
/// <param name="get">Reads the recorded state.</param>
/// <param name="set">Records the state.</param>
/// <param name="comparer">Compares two states; the default one when <c>null</c>.</param>
internal sealed class CommandedState<T>(Func<T> get, Action<T> set, IEqualityComparer<T>? comparer = null)
{
    private readonly IEqualityComparer<T> _comparer = comparer ?? EqualityComparer<T>.Default;

    /// <summary>
    /// Guards the two fields below. Taken before the channels lock, and released before a send.
    /// </summary>
    private readonly Lock _lock = new();

    /// <summary>The command sent last, until it has gone out.</summary>
    private Command? _last;

    /// <summary>What the device has while there is a <see cref="_last"/>.</summary>
    private T _onDevice = default!;

    /// <summary>Reads the recorded state.</summary>
    internal T Current => get();

    private void Record(T state) => set(state);

    /// <summary>
    /// Sends the command for <paramref name="after"/> and records it. A command that cannot be sent
    /// leaves the state as it was.
    /// </summary>
    /// <param name="send">Sends the command it is given.</param>
    /// <param name="message">The command.</param>
    /// <param name="before">The state before the call, which may have recorded the new one already.</param>
    /// <param name="after">The state the command commands.</param>
    internal void Send(Action<IOutboundMessage<string>> send, IOutboundMessage<string> message, T before, T after)
    {
        var command = new Command(this, message, after);
        Command? previous;
        lock (_lock)
        {
            previous = _last;
            if (previous == null)
            {
                _onDevice = before;
            }

            _last = command;
        }

        try
        {
            send(command);
        }
        catch
        {
            lock (_lock)
            {
                _last = previous;
                if (_comparer.Equals(Current, after))
                {
                    Record(before);
                }
            }

            throw;
        }

        lock (_lock)
        {
            if (!command.Dropped)
            {
                Record(after);
            }
        }
    }

    private sealed class Command(CommandedState<T> state, IOutboundMessage<string> message, T after)
        : TrackedCommand(message)
    {
        internal bool Dropped { get; private set; }

        protected override void WentOut()
        {
            lock (state._lock)
            {
                state._onDevice = after;
                if (ReferenceEquals(state._last, this))
                {
                    state._last = null;
                }
            }
        }

        protected override void NotSent()
        {
            lock (state._lock)
            {
                Dropped = true;
                if (!ReferenceEquals(state._last, this))
                {
                    return;
                }

                state._last = null;
                if (state._comparer.Equals(state.Current, after))
                {
                    state.Record(state._onDevice);
                }
            }
        }
    }
}
