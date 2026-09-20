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
    public void SafeTrace_WritesWithoutThrowing()
    {
        var ex = Record.Exception(() => DiagnosticGuard.SafeTrace("diagnostic"));
        Assert.Null(ex);
    }

    [Fact]
    public void SafeTrace_SwallowsAThrowingMessageFactory()
    {
        var ex = Record.Exception(() =>
            DiagnosticGuard.SafeTrace(static () => throw new InvalidOperationException("cannot render")));
        Assert.Null(ex);
    }
}
