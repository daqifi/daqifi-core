using System.Threading;

#nullable enable

namespace Daqifi.Core.Device.Internal;

/// <summary>
/// The single up-front connectivity guard every device operation runs before it touches the
/// wire.
/// </summary>
/// <remarks>
/// <para>
/// Before this existed the guard was written out by hand — <c>if (!_host.IsConnected) { throw
/// new DeviceNotConnectedException(); }</c> — at fifty call sites across nine files, usually
/// followed by a <see cref="CancellationToken.ThrowIfCancellationRequested"/>. Identical
/// boilerplate is not just noise: it means there is nowhere to change the guard. Enriching the
/// message with the device's name, or setting
/// <see cref="DeviceNotConnectedException.IsShuttingDown"/> when the device is mid-teardown
/// rather than never-connected, would otherwise be a fifty-site edit with fifty chances to miss
/// one.
/// </para>
/// <para>
/// The overloads that take a <see cref="CancellationToken"/> fold in the
/// <see cref="CancellationToken.ThrowIfCancellationRequested"/> that followed the guard at most
/// call sites, in that order: connectivity first, then cancellation, exactly as the hand-rolled
/// code did. Sites that did <em>not</em> check the token immediately after the guard call the
/// token-less overload, so no call site gains or loses a cancellation check.
/// </para>
/// </remarks>
internal static class ConnectionGuard
{
    /// <summary>
    /// Throws <see cref="DeviceNotConnectedException"/> when <paramref name="isConnected"/> is
    /// <c>false</c>.
    /// </summary>
    /// <param name="isConnected">The device's current connectivity state.</param>
    /// <exception cref="DeviceNotConnectedException">The device is not connected.</exception>
    internal static void EnsureConnected(bool isConnected)
    {
        if (!isConnected)
        {
            throw new DeviceNotConnectedException();
        }
    }

    /// <summary>
    /// Throws <see cref="DeviceNotConnectedException"/> when <paramref name="isConnected"/> is
    /// <c>false</c>, then throws <see cref="System.OperationCanceledException"/> if
    /// <paramref name="cancellationToken"/> has already been cancelled.
    /// </summary>
    /// <param name="isConnected">The device's current connectivity state.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <exception cref="DeviceNotConnectedException">The device is not connected.</exception>
    internal static void EnsureConnected(bool isConnected, CancellationToken cancellationToken)
    {
        EnsureConnected(isConnected);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <inheritdoc cref="EnsureConnected(bool)"/>
    /// <param name="host">The device seam the operation works through.</param>
    internal static void EnsureConnected(this IDeviceOperationHost host)
        => EnsureConnected(host.IsConnected);

    /// <inheritdoc cref="EnsureConnected(bool, CancellationToken)"/>
    /// <param name="host">The device seam the operation works through.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    internal static void EnsureConnected(this IDeviceOperationHost host, CancellationToken cancellationToken)
        => EnsureConnected(host.IsConnected, cancellationToken);

    /// <inheritdoc cref="EnsureConnected(bool)"/>
    /// <param name="host">The device seam the text exchange works through.</param>
    internal static void EnsureConnected(this ITextExchangeHost host)
        => EnsureConnected(host.IsConnected);

    /// <inheritdoc cref="EnsureConnected(bool, CancellationToken)"/>
    /// <param name="host">The device seam the text exchange works through.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    internal static void EnsureConnected(this ITextExchangeHost host, CancellationToken cancellationToken)
        => EnsureConnected(host.IsConnected, cancellationToken);
}
