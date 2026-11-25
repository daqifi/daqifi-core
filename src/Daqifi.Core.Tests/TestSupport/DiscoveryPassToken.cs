namespace Daqifi.Core.Tests.TestSupport;

/// <summary>
/// Gives a fake <see cref="Daqifi.Core.Device.Discovery.IDeviceFinder"/> one token that fires when
/// its pass must end, for either reason the interface allows.
/// </summary>
internal static class DiscoveryPassToken
{
    /// <summary>A source cancelled by <paramref name="cancellationToken"/> or after <paramref name="timeout"/>.</summary>
    public static CancellationTokenSource Create(TimeSpan? timeout, CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout ?? Timeout.InfiniteTimeSpan);
        return source;
    }
}
