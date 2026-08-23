namespace Daqifi.Core.Channel.Internal;

/// <summary>
/// Shared implementation of the "validate the sample, then store it under the channel's own lock"
/// half of the sequence that <see cref="AnalogChannel"/>, <see cref="DigitalChannel"/>, and
/// <see cref="AnalogOutputChannel"/> each implement identically for their
/// <c>SetActiveSample(IDataSample)</c> overload.
/// </summary>
/// <remarks>
/// Deliberately does not also raise <c>SampleReceived</c>: that has to happen outside the lock (so a
/// subscriber that reads back from the channel can't self-deadlock), and — just as importantly — the
/// event field has to be read fresh at that point rather than snapshotted into a delegate parameter
/// before the lock is even taken, so each channel raises it itself immediately after this returns.
/// Taking the backing field by <c>ref</c> instead of via a store delegate also keeps this
/// allocation-free, which matters on the streaming decode hot path that calls
/// <c>SetActiveSample</c> once per enabled channel per frame.
/// </remarks>
internal static class ActiveSampleAssignment
{
    /// <summary>
    /// Validates <paramref name="sample"/> and stores it into <paramref name="activeSample"/> under
    /// <paramref name="syncLock"/>.
    /// </summary>
    /// <param name="syncLock">The lock guarding the channel's active-sample field.</param>
    /// <param name="sample">The sample to store; must not be <see langword="null"/>.</param>
    /// <param name="activeSample">The channel's backing field.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sample"/> is <see langword="null"/>.</exception>
    public static void StoreUnderLock(object syncLock, IDataSample sample, ref IDataSample? activeSample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        lock (syncLock)
        {
            activeSample = sample;
        }
    }
}
