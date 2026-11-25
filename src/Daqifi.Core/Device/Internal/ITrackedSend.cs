using System;

namespace Daqifi.Core.Device.Internal;

/// <summary>
/// A message whose sender keeps a record of what it does to the device, and so has to know when it
/// goes out: <see cref="DaqifiDevice.Send{T}"/> may hold it back behind an operation on another flow.
/// </summary>
/// <remarks>
/// <see cref="GoOut"/> is called when it was not held back. Otherwise <see cref="Replay"/> is called
/// when its turn comes, or <see cref="NotSent"/> when it has none, or when the replay threw.
/// </remarks>
internal interface ITrackedSend
{
    /// <summary>The message goes out as it is sent. Runs <paramref name="send"/>.</summary>
    void GoOut(Action send);

    /// <summary>
    /// The turn of a message that was held back has come. Runs <paramref name="send"/>, unless the
    /// message has no place any more.
    /// </summary>
    void Replay(Action send);

    /// <summary>
    /// A message that was held back will not be sent: the backlog overflowed, the connection or the
    /// device went away first, or the replay failed. Called on whichever thread found out, which may
    /// be tearing the device down or sending something else, so it must not block.
    /// </summary>
    /// <param name="reason">Why, or <c>null</c> when the connection ended.</param>
    void NotSent(Exception? reason);
}
