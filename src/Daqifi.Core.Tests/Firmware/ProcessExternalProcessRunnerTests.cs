using System.Diagnostics;
using System.Threading.Channels;
using Daqifi.Core.Firmware;
using Daqifi.Core.Firmware.Internal;

namespace Daqifi.Core.Tests.Firmware;

/// <summary>
/// Timeout, kill and output-drain behaviour of <see cref="ProcessExternalProcessRunner"/>
/// (issue #845).
/// </summary>
/// <remarks>
/// <para>
/// The runner had no tests at all, so nothing pinned either of the two things that can hang a
/// firmware update: the kill that a timeout performs, and the wait for the redirected output.
/// The cases below drive the runner through an injected <see cref="IProcessHandle"/>, so the
/// ordering is exercised on every platform - the defects themselves only appear on Windows,
/// where the WiFi flash tool is a <c>.cmd</c> launcher that starts a worker and returns.
/// </para>
/// <para>
/// Every case has a hard bound (<see cref="HardBound"/>). A regression therefore fails one test
/// after ten seconds instead of hanging the whole suite.
/// </para>
/// </remarks>
public class ProcessExternalProcessRunnerTests
{
    /// <summary>
    /// Wall-clock ceiling for one run. Far above every configured timeout below, so it is only
    /// ever reached by code that does not come back at all.
    /// </summary>
    private static readonly TimeSpan HardBound = TimeSpan.FromSeconds(10);

    /// <summary>Request timeout used by the cases that must time out.</summary>
    private static readonly TimeSpan ShortProcessTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>Output-drain bound injected into the runner, to keep the suite quick.</summary>
    private static readonly TimeSpan ShortDrainTimeout = TimeSpan.FromMilliseconds(250);

    [Fact]
    public async Task RunAsync_WhenTheProcessExitsNormally_ReturnsItsOutputAndKillsNothing()
    {
        using var handle = new FakeProcessHandle();
        var runner = NewRunner(handle);
        var request = NewRequest(TimeSpan.FromSeconds(30));

        handle.Out.WriteLine("verify passed");
        handle.Error.WriteLine("a warning");
        handle.Exit(0);

        var result = await RunWithinBoundAsync(runner, request, "a process that exits at once");

        Assert.False(result.TimedOut, "the process exited inside its timeout");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["verify passed"], result.StandardOutputLines);
        Assert.Equal(["a warning"], result.StandardErrorLines);
        Assert.Equal(0, handle.KillAttempts);
    }

    [Fact]
    public async Task RunAsync_WhenTheProcessOutlivesItsTimeout_ReportsTheTimeoutAndKillsTheTree()
    {
        using var handle = new FakeProcessHandle();
        var runner = NewRunner(handle);

        // The process answers the kill, which is the well-behaved case.
        handle.OnKill = () => handle.Exit(-1);

        var result = await RunWithinBoundAsync(
            runner,
            NewRequest(ShortProcessTimeout),
            "a process that never exits on its own");

        Assert.True(result.TimedOut, "the process outlived its timeout");
        Assert.Equal(1, handle.KillAttempts);
        Assert.True(
            result.Duration >= ShortProcessTimeout,
            $"the run must last at least its timeout; it reported {result.Duration.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public async Task RunAsync_WhenTheLauncherExitsAndAChildHoldsThePipes_ReturnsInsteadOfWaitingForTheChild()
    {
        // Defect 1 and 2 together, and the case that actually hangs a WiFi update: the .cmd
        // launcher starts a worker and returns. WaitForExitAsync therefore completes, no timeout
        // ever fires, and the worker keeps the inherited stdout/stderr open. Before the fix the
        // run waited on those pipes for as long as the worker lived - with no error and no
        // result for the caller.
        using var handle = new FakeProcessHandle();
        var runner = NewRunner(handle);

        var sawOutput = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = NewRequest(TimeSpan.FromSeconds(30));
        request.OnStandardOutputLine = _ => sawOutput.TrySetResult();
        handle.Out.WriteLine("flashing");

        var run = runner.RunAsync(request);
        await sawOutput.Task.WaitAsync(HardBound);
        handle.ExitLeavingThePipesOpen(0);

        var result = await WithinBoundAsync(run, "a launcher that exited while its worker holds the pipes");

        Assert.True(result.TimedOut, "the run gave up on the output, so the caller needs an error");
        Assert.Equal(["flashing"], result.StandardOutputLines);
        Assert.True(
            handle.KillAttempts > 0,
            "the surviving worker must be killed even though the launcher has already exited");
    }

    [Fact]
    public async Task RunAsync_WhenTheProcessWritesAfterTheKill_StillReturnsWithinItsBound()
    {
        // The process ignores the kill and keeps talking. Before the fix the run waited for its
        // exit with no timeout at all, so this never came back.
        using var handle = new FakeProcessHandle();
        var runner = NewRunner(handle);

        handle.OnKill = () => handle.Out.WriteLine("worker is still running");

        var result = await RunWithinBoundAsync(
            runner,
            NewRequest(ShortProcessTimeout),
            "a process that survives its own kill");

        Assert.True(result.TimedOut, "the process never exited");

        // The timeout kills the tree, and so does giving up on the output, because the survivor
        // is what still holds the pipes.
        Assert.True(handle.KillAttempts > 0, "the timed-out process must be killed");

        // The process never exited, so there is no exit code to read. The runner reports -1
        // rather than letting Process.ExitCode throw out of a timed-out run.
        Assert.Equal(-1, result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_WhenTheTreeIsAlreadyGoneAndTheKillThrows_StillReturnsAResult()
    {
        // Process.Kill(entireProcessTree: true) throws when the process, or a member of its
        // tree, is already gone. The runner still has to attempt it - the launcher being gone is
        // exactly when a worker survives - and must not let the failure escape.
        using var handle = new FakeProcessHandle();
        var runner = NewRunner(handle);

        handle.OnKill = () => throw new InvalidOperationException(
            "No process is associated with this object.");

        var sawOutput = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = NewRequest(TimeSpan.FromSeconds(30));
        request.OnStandardOutputLine = _ => sawOutput.TrySetResult();
        handle.Out.WriteLine("flashing");

        var run = runner.RunAsync(request);
        await sawOutput.Task.WaitAsync(HardBound);
        handle.ExitLeavingThePipesOpen(0);

        var result = await WithinBoundAsync(run, "a kill of a tree that is already gone");

        Assert.True(result.TimedOut);
        Assert.True(handle.KillAttempts > 0, "the kill must be attempted, not skipped");
    }

    [Fact]
    public async Task RunAsync_WhenTheCallerCancels_KillsTheTreeAndReportsTheCancellation()
    {
        using var handle = new FakeProcessHandle();
        var runner = NewRunner(handle);
        using var cancellation = new CancellationTokenSource();

        var run = runner.RunAsync(NewRequest(TimeSpan.FromSeconds(30)), cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(HardBound));
        Assert.True(handle.KillAttempts > 0, "a cancelled run must not leave the process running");
    }

    [Fact]
    public async Task RunAsync_WithARealProcess_CapturesTheOutputAndTheExitCode()
    {
        // The seam above proves the ordering. This one proves the real Process path still works,
        // on whichever operating system runs it - CI runs the suite on Windows, macOS and Ubuntu.
        var runner = new ProcessExternalProcessRunner();
        var (fileName, arguments) = EchoCommand("daqifi");
        var request = new ExternalProcessRequest
        {
            FileName = fileName,
            Arguments = arguments,
            Timeout = TimeSpan.FromSeconds(30)
        };

        var result = await runner.RunAsync(request).WaitAsync(HardBound);

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("daqifi", string.Join('\n', result.StandardOutputLines));
    }

    [Fact]
    public async Task RunAsync_WithARealProcessThatOutlivesItsTimeout_ReportsTheTimeoutAndEndsIt()
    {
        var runner = new ProcessExternalProcessRunner();
        var (fileName, arguments) = SleepCommand();
        var request = new ExternalProcessRequest
        {
            FileName = fileName,
            Arguments = arguments,
            Timeout = TimeSpan.FromMilliseconds(500)
        };

        var elapsed = Stopwatch.StartNew();
        var result = await runner.RunAsync(request).WaitAsync(HardBound);
        elapsed.Stop();

        Assert.True(result.TimedOut, "the process sleeps far longer than its timeout");
        Assert.True(
            elapsed.Elapsed < HardBound,
            $"the run must be bounded; it took {elapsed.Elapsed.TotalSeconds:F1} s");
    }

    private static ProcessExternalProcessRunner NewRunner(IProcessHandle handle) =>
        new(new FakeProcessHandleFactory(handle), ShortDrainTimeout);

    private static ExternalProcessRequest NewRequest(TimeSpan timeout) =>
        new() { FileName = "flash-tool.cmd", Timeout = timeout };

    private static Task<ExternalProcessResult> RunWithinBoundAsync(
        ProcessExternalProcessRunner runner,
        ExternalProcessRequest request,
        string what) =>
        WithinBoundAsync(runner.RunAsync(request), what);

    private static async Task<ExternalProcessResult> WithinBoundAsync(
        Task<ExternalProcessResult> run,
        string what)
    {
        try
        {
            return await run.WaitAsync(HardBound);
        }
        catch (TimeoutException)
        {
            Assert.Fail(
                $"RunAsync did not return within {HardBound.TotalSeconds:F0} s for {what}. " +
                "The caller of a firmware update gets no error and no result.");
            throw;
        }
    }

    private static (string FileName, string Arguments) EchoCommand(string text) =>
        OperatingSystem.IsWindows()
            ? ("cmd.exe", $"/c echo {text}")
            : ("/bin/sh", $"-c \"echo {text}\"");

    private static (string FileName, string Arguments) SleepCommand() =>
        OperatingSystem.IsWindows()
            ? ("cmd.exe", "/c ping -n 30 127.0.0.1")
            : ("/bin/sh", "-c \"sleep 30\"");

    /// <summary>
    /// A process the test drives directly: when it exits, whether the exit closes the redirected
    /// pipes, and what a kill of its tree does.
    /// </summary>
    private sealed class FakeProcessHandle : IProcessHandle
    {
        private readonly TaskCompletionSource _exited =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly StringWriter _standardInput = new();
        private int _killAttempts;
        private int? _exitCode;

        /// <summary>Gets the writer for the process's stdout pipe.</summary>
        public ScriptedPipeReader Out { get; } = new();

        /// <summary>Gets the writer for the process's stderr pipe.</summary>
        public ScriptedPipeReader Error { get; } = new();

        /// <summary>Gets or sets what a kill of the process tree does.</summary>
        public Action? OnKill { get; set; }

        /// <summary>Gets how many times the runner tried to kill the tree.</summary>
        public int KillAttempts => Volatile.Read(ref _killAttempts);

        public bool HasExited { get; private set; }

        public int ExitCode => _exitCode ?? throw new InvalidOperationException(
            "Process must exit before you can get the requested information.");

        public TextReader StandardOutput => Out;

        public TextReader StandardError => Error;

        public TextWriter StandardInput => _standardInput;

        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            _exited.Task.WaitAsync(cancellationToken);

        public void KillProcessTree()
        {
            Interlocked.Increment(ref _killAttempts);
            OnKill?.Invoke();
        }

        /// <summary>Ends the process and closes its ends of the pipes, as a real exit does.</summary>
        public void Exit(int exitCode)
        {
            _exitCode = exitCode;
            HasExited = true;
            Out.CloseStream();
            Error.CloseStream();
            _exited.TrySetResult();
        }

        /// <summary>
        /// Ends the launcher while a worker it started keeps the inherited pipes open. This is the
        /// Windows case from issue #845.
        /// </summary>
        public void ExitLeavingThePipesOpen(int exitCode)
        {
            _exitCode = exitCode;
            HasExited = true;
            _exited.TrySetResult();
        }

        public void Dispose()
        {
            // Process.Dispose() closes this side of the pipes. That is what lets a reader the
            // runner has given up on finish, instead of waiting on a survivor for ever.
            Out.CloseStream();
            Error.CloseStream();
            _exited.TrySetResult();
            _standardInput.Dispose();
        }
    }

    /// <summary>
    /// A redirected pipe the test writes lines into. Leaving it open models a process that still
    /// holds the other end.
    /// </summary>
    private sealed class ScriptedPipeReader : TextReader
    {
        private readonly Channel<string> _lines =
            System.Threading.Channels.Channel.CreateUnbounded<string>();

        /// <summary>Puts one line on the pipe.</summary>
        public void WriteLine(string line) => _lines.Writer.TryWrite(line);

        /// <summary>Closes the pipe, so the next read reports end of stream.</summary>
        public void CloseStream() => _lines.Writer.TryComplete();

        public override async Task<string?> ReadLineAsync()
        {
            try
            {
                return await _lines.Reader.ReadAsync();
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }
    }

    private sealed class FakeProcessHandleFactory : IProcessHandleFactory
    {
        private readonly IProcessHandle _handle;

        public FakeProcessHandleFactory(IProcessHandle handle)
        {
            _handle = handle;
        }

        public IProcessHandle Start(ExternalProcessRequest request) => _handle;
    }
}
