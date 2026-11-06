namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Implemented by a finder that can answer a pass with only part of what it set out to probe, and
/// say so afterwards.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ContinuousDeviceFinder"/> treats a device's absence from a pass result as evidence
/// that the device went away. That inference only holds if the result is a complete census.
/// <see cref="SerialDeviceFinder"/> can break it: when
/// <see cref="ContinuousDiscoveryOptions.PassTimeout"/> fires mid-sweep it catches the
/// cancellation and returns the port probes that already finished, so the ports it never reached
/// are missing from a list that otherwise looks whole.
/// </para>
/// <para>
/// This must be reported by the finder, because it cannot be inferred from the outside. In
/// particular it cannot be inferred from the pass deadline having elapsed:
/// <see cref="WiFiDeviceFinder"/> and <see cref="MDnsDeviceFinder"/> have no natural end, so
/// their <see cref="IDeviceFinder.DiscoverAsync(System.Threading.CancellationToken)"/> listens
/// until the caller's token fires and then returns a COMPLETE answer. Reading an elapsed deadline
/// as "incomplete" would mark every one of their passes partial, and a pass that is always
/// partial can never age a device out of the live set at all.
/// </para>
/// <para>
/// Internal for the same reason <see cref="IBusyPortReporter"/> is: a one-shot discovery has no
/// prior live set to protect, so it has nothing to do with the answer. Only continuous discovery
/// does.
/// </para>
/// </remarks>
internal interface IPartialResultReporter
{
    /// <summary>
    /// Whether the most recent discovery pass returned before it had probed everything it
    /// enumerated, making its result a partial view of the system rather than a census.
    /// </summary>
    /// <remarks>
    /// Reset at the start of every pass, so it describes that pass alone and never accumulates.
    /// Taking it is single-use per pass, matching
    /// <see cref="IBusyPortReporter.TakeBusyPortsFromLastPass"/>.
    /// </remarks>
    bool TakeResultsWerePartialFromLastPass();
}
