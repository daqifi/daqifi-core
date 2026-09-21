using System.Diagnostics;

namespace Daqifi.Core.Internal;

/// <summary>
/// Isolates best-effort calls out to consumer code from device and transport operation. A
/// consumer-supplied logger, <see cref="TraceListener"/>, or event subscriber must never take
/// down a connect, reconnect, drop path, or frame pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Replaces a dozen byte-identical <c>SafeLog</c> / <c>SafeTrace</c> twins that had accumulated
/// across the device, producer, and transport types.
/// </para>
/// <para>
/// The empty <c>catch</c> is deliberate, and the contract that makes it defensible is worth
/// stating precisely, because it is narrower than "swallow exceptions" and wider than "logging".
/// What may be passed here is a <b>best-effort notification out to consumer code</b> — an
/// <see cref="Microsoft.Extensions.Logging.ILogger"/> call, a <see cref="Trace"/> write, or the
/// raise of a best-effort event such as <c>SendFailed</c> or <c>ErrorOccurred</c> — where the
/// only thing lost when it throws is that one notification. Consumer code is entitled to
/// misbehave; the producer's background thread, the reader loop, and the decode path are not
/// entitled to die because it did.
/// </para>
/// <para>
/// What may <em>not</em> be passed is anything the device's own state or correctness depends on.
/// A call whose failure has to change what happens next does not belong behind a guard that
/// cannot report it.
/// </para>
/// <para>
/// Nor does the guard log the failure it caught: logging from inside a catch was considered and
/// rejected for this codebase (issue #98), and would in any case often mean calling the very
/// logger that just threw.
/// </para>
/// </remarks>
internal static class DiagnosticGuard
{
    /// <summary>
    /// Runs a best-effort notification out to consumer code — a logging call, or the raise of an
    /// event whose subscribers must not be able to take down the caller — swallowing anything it
    /// throws. See the type-level remarks for what may and may not be passed here.
    /// </summary>
    /// <param name="action">The logging call or best-effort event raise to run.</param>
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
