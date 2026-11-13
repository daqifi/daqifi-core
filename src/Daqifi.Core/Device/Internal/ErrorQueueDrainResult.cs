using System.Collections.Generic;

namespace Daqifi.Core.Device.Internal;

/// <summary>
/// What one drain of the device's SCPI error queue found.
/// </summary>
/// <param name="Entries">The entries read off the queue, oldest first.</param>
/// <param name="ReachedEmpty">
/// True only when the drain read the queue down to <c>0,"No error"</c>. False when it stopped
/// early (a reply went missing or was unreadable, or it hit its read cap), in which case older
/// entries may still be on the queue.
/// </param>
internal readonly record struct ErrorQueueDrainResult(IReadOnlyList<string> Entries, bool ReachedEmpty);
