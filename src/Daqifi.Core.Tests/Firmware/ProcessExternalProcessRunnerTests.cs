using System.Diagnostics;
using System.Globalization;
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

    /// <summary>
    /// Wall-clock ceiling for a case that starts a real process. Generous, because it has to
    /// cover whatever a loaded CI runner charges for process startup - it is a hang detector,
    /// not a performance assertion. Nothing a case asserts may depend on startup beating it.
    /// </summary>
    private static readonly TimeSpan RealProcessBound = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a real process the cases below start keeps running on its own. Long enough that
    /// a run which ends sooner can only have ended because the runner ended it.
    /// </summary>
    private static readonly TimeSpan RealProcessLifetime = TimeSpan.FromSeconds(120);

    /// <summary>
    /// How long the Windows case below waits for its process family to get into position.
    /// Deliberately well inside <see cref="RealProcessBound"/>, so a family that never gets
    /// there fails saying what state it reached instead of as a bare run-level timeout.
    /// </summary>
    private static readonly TimeSpan RealProcessSetUpBound = TimeSpan.FromSeconds(30);

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

        // A lower bound on Duration is deliberately not asserted. The timeout runs on a
        // CancellationTokenSource timer and Duration comes from a Stopwatch, and the two clocks
        // disagree by up to one timer tick, so such an assertion flakes rather than pins.
        Assert.True(
            result.Duration < HardBound,
            $"the run must be bounded; it reported {result.Duration.TotalSeconds:F1} s");
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
        var request = NewRequest(ShortProcessTimeout);
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
    public async Task RunAsync_WorkerFinishesInsideTheRequestTimeout_DoesNotUseTheCleanupDeadline()
    {
        using var handle = new FakeProcessHandle();
        var runner = NewRunner(handle);
        handle.Out.WriteLine("flashing");
        handle.ExitLeavingThePipesOpen(0);
        var run = runner.RunAsync(NewRequest(TimeSpan.FromSeconds(5)));
        await Task.Delay(ShortDrainTimeout + ShortDrainTimeout);
        handle.Out.WriteLine("verify passed");
        handle.Out.CloseStream();
        handle.Error.CloseStream();
        var result = await WithinBoundAsync(run, "a worker still inside the requested time budget");
        Assert.False(result.TimedOut);
        Assert.Equal(0, handle.KillAttempts);
        Assert.Contains("verify passed", result.StandardOutputLines);
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
        var request = NewRequest(ShortProcessTimeout);
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

        // RealProcessBound, not HardBound: this one pays for a real process start, and on a
        // loaded Windows runner that alone can take seconds.
        var result = await runner.RunAsync(request).WaitAsync(RealProcessBound);

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
        var result = await runner.RunAsync(request).WaitAsync(RealProcessBound);
        elapsed.Stop();

        Assert.True(result.TimedOut, "the process sleeps far longer than its timeout");

        // The bound is measured against the process's own lifetime, not against a fixed number
        // of seconds the runner's startup has to fit inside: ending less than half way through
        // the sleep can only mean the runner ended it.
        Assert.True(
            elapsed.Elapsed < RealProcessLifetime / 2,
            $"the run must be bounded; it took {elapsed.Elapsed.TotalSeconds:F1} s");
    }

    [Fact]
    public async Task RunAsync_OnWindows_KillsAWorkerAfterItsLauncherHasExited()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // The real Windows family from issue #845: a launcher starts a worker, publishes its
        // process id and exits, and the worker keeps the inherited output pipes open. The run
        // cannot drain them, so it times out, kills the job object, and the worker must die with
        // it - even though the process the runner started is long gone.
        using var family = new ScriptedWindowsProcessFamily(RealProcessSetUpBound, RealProcessLifetime);

        var result = await new ProcessExternalProcessRunner(family).RunAsync(new ExternalProcessRequest
        {
            FileName = "powershell.exe",
            Arguments = family.LauncherArguments,

            // Only the drain of the pipes the worker holds has to fit inside this, because the
            // family puts itself into position before the runner arms the timeout. The worker
            // never releases those pipes, so the timeout always fires.
            Timeout = TimeSpan.FromSeconds(1)
        }).WaitAsync(RealProcessBound);

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.TimedOut, "the surviving worker holds the pipes, so the run cannot drain them");
        await family.AssertTheWorkerWasTerminatedAsync();
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
            ? ("cmd.exe", $"/c ping -n {RealProcessLifetime.TotalSeconds:F0} 127.0.0.1")
            : ("/bin/sh", $"-c \"sleep {RealProcessLifetime.TotalSeconds:F0}\"");

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

    /// <summary>
    /// A real Windows process family for the launcher-exits-first case: a <c>powershell.exe</c>
    /// launcher that starts a <c>cmd.exe</c> worker, publishes the worker's process id and
    /// exits, leaving the worker holding the output pipes it inherited.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is also the runner's process factory, which is what makes the case deterministic. The
    /// runner arms the request timeout only once <see cref="Start"/> has returned, so holding
    /// Start until the family is in position charges none of powershell's startup, and none of
    /// <c>Start-Process</c>, to that timeout.
    /// </para>
    /// <para>
    /// The earlier version of this case read the worker's id from the launcher's stdout inside a
    /// five-second timeout, and failed roughly one Windows run in three: startup plus
    /// <c>Start-Process</c> can exceed five seconds on a loaded runner, so the launcher was
    /// killed before it ever reported the id and the assertion saw an empty collection. Nothing
    /// here races a timeout: the id travels through a file this class polls, and the launcher's
    /// exit is observed on the handle.
    /// </para>
    /// <para>
    /// Holding Start is safe because nothing in this family writes to the redirected output -
    /// the launcher prints nothing and the worker's own output goes to <c>nul</c> - so no pipe
    /// can fill while the runner's readers have not started yet.
    /// </para>
    /// </remarks>
    private sealed class ScriptedWindowsProcessFamily : IProcessHandleFactory, IDisposable
    {
        private readonly string _script = NewTempFilePath("ps1");
        private readonly string _workerIdFile = NewTempFilePath("pid");
        private readonly string _failureFile = NewTempFilePath("err");
        private readonly TimeSpan _bound;
        private int? _workerId;

        internal ScriptedWindowsProcessFamily(TimeSpan bound, TimeSpan workerLifetime)
        {
            _bound = bound;
            var staging = _workerIdFile + ".staging";
            File.WriteAllText(
                _script,
                $$"""
                  $ErrorActionPreference = 'Stop'
                  try {
                      $worker = Start-Process -FilePath cmd.exe -NoNewWindow -PassThru `
                          -ArgumentList '/d /c ping -n {{workerLifetime.TotalSeconds:F0}} 127.0.0.1 > nul'
                      Set-Content -LiteralPath '{{staging}}' -Value $worker.Id -Encoding ascii
                      Move-Item -LiteralPath '{{staging}}' -Destination '{{_workerIdFile}}'
                  }
                  catch {
                      Set-Content -LiteralPath '{{_failureFile}}' -Value $_.Exception.ToString() -Encoding ascii
                      exit 1
                  }
                  """);
        }

        /// <summary>Gets the arguments that run the launcher script.</summary>
        internal string LauncherArguments =>
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{_script}\"";

        /// <summary>
        /// Starts the launcher and returns only once it has published its worker's process id
        /// and exited, with the worker still running.
        /// </summary>
        public IProcessHandle Start(ExternalProcessRequest request)
        {
            var launcher = SystemProcessHandleFactory.Instance.Start(request);
            try
            {
                _workerId = WaitUntilTheWorkerOutlivesItsLauncher(launcher);
                return launcher;
            }
            catch
            {
                launcher.Dispose();
                throw;
            }
        }

        /// <summary>Asserts that the worker did not survive the run.</summary>
        internal async Task AssertTheWorkerWasTerminatedAsync()
        {
            var id = _workerId ?? throw new InvalidOperationException("The launcher was never started.");

            try
            {
                using var worker = Process.GetProcessById(id);
                await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (ArgumentException)
            {
                // The terminated worker has already left the process table.
            }
            catch (TimeoutException)
            {
                Assert.Fail(
                    $"worker {id} outlived the run. A worker whose launcher has already exited " +
                    "is exactly what the job object is there to kill.");
            }
        }

        public void Dispose()
        {
            if (_workerId is { } id)
            {
                try
                {
                    using var worker = Process.GetProcessById(id);
                    if (!worker.HasExited)
                    {
                        worker.Kill(entireProcessTree: true);
                    }
                }
                catch (ArgumentException)
                {
                    // Already gone, which is the expected outcome.
                }
                catch (InvalidOperationException)
                {
                    // Exited between the test and this check.
                }
            }

            Delete(_script);
            Delete(_workerIdFile);
            Delete(_workerIdFile + ".staging");
            Delete(_failureFile);
        }

        private int WaitUntilTheWorkerOutlivesItsLauncher(IProcessHandle launcher)
        {
            var waited = Stopwatch.StartNew();
            int? workerId = null;

            while (waited.Elapsed < _bound)
            {
                workerId ??= ReadWorkerId();
                if (workerId is { } id && launcher.HasExited)
                {
                    Assert.False(
                        HasExited(id),
                        $"worker {id} did not outlive its launcher, so the case would prove nothing");

                    return id;
                }

                Thread.Sleep(25);
            }

            throw new TimeoutException(
                $"The launcher did not start a worker and exit within {_bound.TotalSeconds:F0} s. " +
                $"Worker id: {workerId?.ToString(CultureInfo.InvariantCulture) ?? "never reported"}; " +
                $"launcher exited: {launcher.HasExited}; script error: {Read(_failureFile) ?? "none"}.");
        }

        /// <summary>
        /// Reads the published worker id, or <c>null</c> while there is none. The launcher writes
        /// the id to a staging path and renames it, so a poll never sees a half-written number.
        /// </summary>
        private int? ReadWorkerId() =>
            int.TryParse(Read(_workerIdFile)?.Trim(), CultureInfo.InvariantCulture, out var id) ? id : null;

        private static bool HasExited(int processId)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                return process.HasExited;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }

        private static string NewTempFilePath(string extension) =>
            Path.Combine(Path.GetTempPath(), $"daqifi-worker-{Guid.NewGuid():N}.{extension}");

        private static string? Read(string path)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static void Delete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A surviving process can still hold the file; the temp directory is cleaned up
                // by the runner image.
            }
            catch (UnauthorizedAccessException)
            {
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
