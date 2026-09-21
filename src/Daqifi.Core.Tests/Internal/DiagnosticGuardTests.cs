using System.Diagnostics;
using Daqifi.Core.Internal;

namespace Daqifi.Core.Tests.Internal;

public class DiagnosticGuardTests
{
    [Fact]
    public void SafeLog_RunsTheAction()
    {
        var ran = false;
        DiagnosticGuard.SafeLog(() => ran = true);
        Assert.True(ran);
    }

    [Fact]
    public void SafeLog_SwallowsThrownExceptions()
    {
        var ex = Record.Exception(() =>
            DiagnosticGuard.SafeLog(() => throw new InvalidOperationException("boom")));
        Assert.Null(ex);
    }

    [Fact]
    public void SafeTrace_ActuallyWritesTheLine()
    {
        // "It didn't throw" is also true of a guard that quietly writes nothing, and a
        // diagnostic helper that silently stopped tracing is exactly the regression nobody
        // would notice. The listener is synchronized and asserted with Contains, so traffic
        // from tests running in parallel can neither corrupt it nor fail this -- the same
        // arrangement DropPathSubscriberIsolationTests uses, and the reason a *throwing*
        // listener is not installed here: Trace.Listeners is process-global.
        var marker = $"diagnostic-{Guid.NewGuid():N}";
        var traced = CaptureTrace(() => DiagnosticGuard.SafeTrace(marker));

        Assert.Contains(marker, traced, StringComparison.Ordinal);
    }

    [Fact]
    public void SafeTrace_ComposesTheLineInsideTheGuard_SoAThrowingFactoryCannotEscape()
    {
        // The overload exists precisely so a consumer-supplied exception whose ToString()
        // throws cannot escape the catch that is containing it (issue #494). Composing
        // eagerly at the call site would defeat that.
        var ex = Record.Exception(() =>
            DiagnosticGuard.SafeTrace(static () => throw new InvalidOperationException("cannot render")));

        Assert.Null(ex);
    }

    [Fact]
    public void SafeTrace_FactoryOverload_AlsoWritesTheLine()
    {
        var marker = $"composed-{Guid.NewGuid():N}";
        var traced = CaptureTrace(() => DiagnosticGuard.SafeTrace(() => marker));

        Assert.Contains(marker, traced, StringComparison.Ordinal);
    }

    private static string CaptureTrace(Action act)
    {
        var captured = new StringWriter();
        var listener = new TextWriterTraceListener(TextWriter.Synchronized(captured));
        Trace.Listeners.Add(listener);
        try
        {
            act();
            Trace.Flush();
        }
        finally
        {
            Trace.Listeners.Remove(listener);
            listener.Dispose();
        }

        return captured.ToString();
    }
}
