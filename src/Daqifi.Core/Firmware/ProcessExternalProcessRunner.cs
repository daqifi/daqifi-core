using System.Diagnostics;
using Daqifi.Core.Firmware.Internal;

namespace Daqifi.Core.Firmware;

/// <summary>
/// Default <see cref="IExternalProcessRunner"/> implementation backed by
/// <see cref="Process"/>.
/// </summary>
public sealed class ProcessExternalProcessRunner : IExternalProcessRunner
{
    /// <summary>
    /// How long the runner waits for the redirected output, and for a killed process to end,
    /// before it gives up. Both waits used to have no bound at all (issue #845).
    /// </summary>
    internal static readonly TimeSpan DefaultOutputDrainTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Reported exit code when the process never exited, so the operating system has no code
    /// to give. <see cref="Process.ExitCode"/> throws in that state.
    /// </summary>
    private const int UnknownExitCode = -1;

    private readonly IProcessHandleFactory _processFactory;
    private readonly TimeSpan _outputDrainTimeout;

    /// <summary>
    /// Initializes a runner that starts real operating-system processes.
    /// </summary>
    public ProcessExternalProcessRunner()
        : this(SystemProcessHandleFactory.Instance)
    {
    }

    internal ProcessExternalProcessRunner(
        IProcessHandleFactory processFactory,
        TimeSpan? outputDrainTimeout = null)
    {
        _processFactory = processFactory;
        _outputDrainTimeout = outputDrainTimeout ?? DefaultOutputDrainTimeout;
    }

    /// <summary>
    /// Executes an external process request.
    /// </summary>
    /// <param name="request">Process execution request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Captured execution result.</returns>
    /// <remarks>
    /// Every wait in the run has a bound, so the method always returns or throws. A process that
    /// starts a worker and exits is the case that used to hang: the worker inherits the
    /// redirected output and holds it open after its launcher is gone. When the runner gives up
    /// on the output it kills the tree again and reports
    /// <see cref="ExternalProcessResult.TimedOut"/>, because the run did not finish inside its
    /// bounds and the captured output can be incomplete.
    /// </remarks>
    public async Task<ExternalProcessResult> RunAsync(
        ExternalProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.FileName))
        {
            throw new ArgumentException("Process file name cannot be empty.", nameof(request));
        }

        if (request.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Timeout,
                "Process timeout must be greater than zero.");
        }

        var standardOutputLines = new LineSink();
        var standardErrorLines = new LineSink();
        var stopwatch = Stopwatch.StartNew();

        using var process = _processFactory.Start(request);

        var outputTask = PumpOutputAsync(
            process,
            standardOutputLines,
            request.OnStandardOutputLine,
            request.StandardInputResponseFactory);

        var errorTask = PumpErrorAsync(
            process,
            standardErrorLines,
            request.OnStandardErrorLine);

        var timedOut = false;

        using var timeoutCts = new CancellationTokenSource(request.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            TryKillProcessTree(process);

            // A process can survive its own kill, so this wait needs a bound too. It had none.
            await WaitForExitWithinAsync(process, _outputDrainTimeout).ConfigureAwait(false);
        }
        catch
        {
            TryKillProcessTree(process);
            throw;
        }

        // The output readers are not finished just because the process is. Anything the process
        // started inherits the redirected pipes and holds them open, so this wait is what used
        // to let a run outlive its timeout by as long as that worker lived.
        if (!await TryDrainOutputAsync(outputTask, errorTask, _outputDrainTimeout).ConfigureAwait(false))
        {
            // Something still holds the pipes. Reach for the whole tree, even when the process
            // the runner started has already exited: the holder is a descendant of a launcher
            // that is gone.
            TryKillProcessTree(process);
            timedOut = true;
        }

        stopwatch.Stop();

        cancellationToken.ThrowIfCancellationRequested();

        return new ExternalProcessResult(
            ReadExitCode(process),
            timedOut,
            stopwatch.Elapsed,
            standardOutputLines.Snapshot(),
            standardErrorLines.Snapshot());
    }

    private static async Task PumpOutputAsync(
        IProcessHandle process,
        LineSink sink,
        Action<string>? lineHandler,
        Func<string, string?>? inputResponder)
    {
        try
        {
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                if (line == null)
                {
                    break;
                }

                sink.Add(line);
                lineHandler?.Invoke(line);

                if (inputResponder == null)
                {
                    continue;
                }

                var response = inputResponder(line);
                if (response == null)
                {
                    continue;
                }

                try
                {
                    await process.StandardInput.WriteLineAsync(response).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync().ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    break;
                }
            }
        }
        catch (Exception exception) when (IsPipeShutdown(exception))
        {
            // The run has already returned and disposed the process, which closes this end of
            // the pipe. The reader is only still here because a survivor held the other end.
        }
    }

    private static async Task PumpErrorAsync(
        IProcessHandle process,
        LineSink sink,
        Action<string>? lineHandler)
    {
        try
        {
            while (true)
            {
                var line = await process.StandardError.ReadLineAsync().ConfigureAwait(false);
                if (line == null)
                {
                    break;
                }

                sink.Add(line);
                lineHandler?.Invoke(line);
            }
        }
        catch (Exception exception) when (IsPipeShutdown(exception))
        {
            // See PumpOutputAsync.
        }
    }

    private static bool IsPipeShutdown(Exception exception) =>
        exception is IOException or ObjectDisposedException or OperationCanceledException;

    /// <summary>
    /// Waits for both output readers, for at most <paramref name="timeout"/>.
    /// </summary>
    /// <returns><c>true</c> when the readers finished; <c>false</c> when the wait gave up.</returns>
    private static async Task<bool> TryDrainOutputAsync(
        Task outputTask,
        Task errorTask,
        TimeSpan timeout)
    {
        var readers = Task.WhenAll(outputTask, errorTask);

        try
        {
            await readers.WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            // The readers keep running until the process handle is disposed. Observe whatever
            // they end with, so an abandoned reader cannot raise an unobserved task exception.
            _ = readers.ContinueWith(
                static finished => _ = finished.Exception,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return false;
        }
    }

    private static async Task WaitForExitWithinAsync(IProcessHandle process, TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The process did not answer the kill inside the grace period. The caller still gets
            // a result that says the run timed out.
        }
    }

    private static void TryKillProcessTree(IProcessHandle process)
    {
        try
        {
            // No HasExited test first. The launcher exiting while its worker runs on is exactly
            // the case this has to cover, and a test for HasExited turns that case into "kill
            // nothing". Process.Kill(entireProcessTree: true) throws when the tree is already
            // gone, which is why every failure here is swallowed.
            process.KillProcessTree();
        }
        catch
        {
            // Ignore kill failures. The run reports the timeout; it cannot report the kill.
        }
    }

    private static int ReadExitCode(IProcessHandle process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            // The process never exited, so there is no exit code. TimedOut carries the meaning.
            return UnknownExitCode;
        }
    }

    /// <summary>
    /// Collects captured lines. The readers can still be running when the run gives up on them,
    /// so the list needs a lock around both the writes and the snapshot the result is built from.
    /// </summary>
    private sealed class LineSink
    {
        private readonly List<string> _lines = [];
        private readonly object _gate = new();

        internal void Add(string line)
        {
            lock (_gate)
            {
                _lines.Add(line);
            }
        }

        internal IReadOnlyList<string> Snapshot()
        {
            lock (_gate)
            {
                return _lines.ToArray();
            }
        }
    }
}
