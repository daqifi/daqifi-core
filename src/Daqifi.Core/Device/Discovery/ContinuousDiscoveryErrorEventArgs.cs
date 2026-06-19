using System;

namespace Daqifi.Core.Device.Discovery;

/// <summary>
/// Event args raised when a discovery pass inside <see cref="ContinuousDeviceFinder"/>
/// fails. The continuous scan loop survives the failure and continues with the next
/// pass; this event exists so callers can surface or log the underlying error.
/// </summary>
public class ContinuousDiscoveryErrorEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ContinuousDiscoveryErrorEventArgs"/> class.
    /// </summary>
    /// <param name="exception">The exception thrown by the discovery pass.</param>
    public ContinuousDiscoveryErrorEventArgs(Exception exception)
    {
        Exception = exception ?? throw new ArgumentNullException(nameof(exception));
    }

    /// <summary>
    /// Gets the exception thrown by the failed discovery pass.
    /// </summary>
    public Exception Exception { get; }
}
