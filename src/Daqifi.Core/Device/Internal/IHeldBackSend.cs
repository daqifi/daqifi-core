using System;

namespace Daqifi.Core.Device.Internal;

/// <summary>
/// What a message that <see cref="DaqifiDevice.Send{T}"/> held back behind an operation on another
/// flow is replayed through, when whether it should still go out depends on what went out before it.
/// </summary>
internal interface IHeldBackSend
{
    /// <summary>
    /// Whether something has overtaken the message, so that it will not be sent. Asked first, so
    /// that nothing is done on behalf of a message that goes nowhere.
    /// </summary>
    bool IsOvertaken { get; }

    /// <summary>Runs <paramref name="send"/>, unless something has overtaken the message.</summary>
    void Replay(Action send);
}
