using System.Diagnostics;

namespace Daqifi.Core.Internal;

/// <summary>
/// Isolates diagnostic side-effects from device and transport operation. A consumer-supplied
/// logger or <see cref="TraceListener"/> must never take down a connect, reconnect, drop path,
/// or frame pipeline.
/// </summary>
internal static class DiagnosticGuard
{
    /// <summary>
    /// Runs a logging call (or any similarly-isolated side-effect), swallowing anything it throws.
    /// </summary>
    /// <param name="action">The logging call or isolated side-effect to run.</param>
    internal static void SafeLog(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // A logger that throws is not permitted to take down device operation.
        }
    }

    /// <summary>
    /// Writes a diagnostic line, swallowing anything a misbehaving <see cref="TraceListener"/> throws.
    /// </summary>
    /// <param name="message">The diagnostic line to write.</param>
    internal static void SafeTrace(string message) => SafeLog(() => Trace.WriteLine(message));

    /// <summary>
    /// Composes and writes a diagnostic line, swallowing both a throwing listener and a message
    /// factory that throws (for example <see cref="object.ToString"/> on a consumer-supplied exception).
    /// </summary>
    /// <param name="message">
    /// Factory for the diagnostic line. Invoked inside the guard so a throwing
    /// <see cref="object.ToString"/> cannot escape the drop path.
    /// </param>
    internal static void SafeTrace(Func<string> message) => SafeLog(() => Trace.WriteLine(message()));
}
