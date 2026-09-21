using System.Diagnostics;

namespace Daqifi.Core.Internal;

/// <summary>
/// Isolates diagnostic side-effects from device and transport operation. A consumer-supplied
/// logger or <see cref="TraceListener"/> must never take down a connect, reconnect, drop path,
/// or frame pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Replaces a dozen byte-identical <c>SafeLog</c> / <c>SafeTrace</c> twins that had accumulated
/// across the device, producer, and transport types.
/// </para>
/// <para>
/// The empty <c>catch</c> is deliberate, and it is the reason this type is narrow. Swallowing an
/// exception is only defensible when the work being swallowed is <em>purely</em> diagnostic —
/// when dropping it costs a log line and nothing else. That is the whole contract here: pass a
/// logger call or a <see cref="Trace"/> write, nothing that the device's own correctness depends
/// on. Nor does the guard log the failure it caught: logging from inside a catch was considered
/// and rejected for this codebase (issue #98), and would in any case mean calling the very
/// logger that just threw.
/// </para>
/// </remarks>
internal static class DiagnosticGuard
{
    /// <summary>
    /// Runs a logging call, swallowing anything it throws. See the type-level remarks for what
    /// may and may not be passed here.
    /// </summary>
    /// <param name="action">The logging call to run.</param>
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
