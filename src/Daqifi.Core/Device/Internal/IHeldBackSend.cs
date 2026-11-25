using System;

namespace Daqifi.Core.Device.Internal;

/// <summary>
/// What <see cref="DaqifiDevice.Send{T}"/> tells the sender of a message it holds back behind an
/// operation on another flow. Exactly one of the two members is called, or <see cref="NotSent"/>
/// after a <see cref="Replay"/> that threw.
/// </summary>
internal interface IHeldBackSend
{
    /// <summary>
    /// The message's turn has come. Runs <paramref name="send"/>, unless the message has no place
    /// any more.
    /// </summary>
    void Replay(Action send);

    /// <summary>
    /// The message will not be sent: the backlog overflowed, the connection or the device went away
    /// first, or the replay failed. Called on whichever thread found out, which may be tearing the
    /// device down or sending something else, so it must not block.
    /// </summary>
    /// <param name="reason">Why, or <c>null</c> when the connection ended.</param>
    void NotSent(Exception? reason);
}
