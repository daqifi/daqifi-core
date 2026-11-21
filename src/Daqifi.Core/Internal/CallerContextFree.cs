namespace Daqifi.Core.Internal;

/// <summary>
/// Starts background work without the caller's <see cref="ExecutionContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>Task.Run</c>, <c>new Timer</c> and <c>Thread.Start</c> all capture the starting flow's
/// execution context, and with it every <see cref="AsyncLocal{T}"/> value that flow holds. The
/// device keeps flow-scoped state that way — "this flow is inside a text exchange", "this flow
/// holds the operation lock", "this flow is inside a lifecycle operation" — and work started from
/// inside one of those (a reader restarted at the end of a raw capture, a pump or timer created
/// inside a connect, a reconnect loop started from a reader thread) would carry the flag for its
/// whole life. A text query from that work is then refused as re-entrant, and a reconnect walks
/// straight past gates it does not hold.
/// </para>
/// <para>
/// Long-lived and fire-and-forget work therefore starts from an empty context: nothing it does
/// is part of the operation that happened to start it.
/// </para>
/// </remarks>
internal static class CallerContextFree
{
    /// <summary>Queues <paramref name="work"/> to the thread pool with no caller context.</summary>
    internal static Task Run(Func<Task> work)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(work);
        }
    }

    /// <summary>Creates a timer whose callbacks run with no caller context.</summary>
    internal static Timer CreateTimer(TimerCallback callback, TimeSpan dueTime, TimeSpan period)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return new Timer(callback, null, dueTime, period);
        }
    }
}
