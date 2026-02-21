using System.Diagnostics;

namespace Daqifi.Core.Firmware;

/// <summary>
/// Default <see cref="IExternalProcessRunner"/> implementation backed by <see cref="Process"/>.
/// </summary>
public sealed class ProcessExternalProcessRunner : IExternalProcessRunner
{
    /// <inheritdoc />
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

        var standardOutputLines = new List<string>();
        var standardErrorLines = new List<string>();
        var stopwatch = Stopwatch.StartNew();

        using var process = CreateProcess(request);
        if (!process.Start())
        {
            throw new InvalidOperationException($"Unable to start process '{request.FileName}'.");
        }

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
            TryTerminateProcess(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            TryTerminateProcess(process);
            throw;
        }

        await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
        stopwatch.Stop();

        cancellationToken.ThrowIfCancellationRequested();

        return new ExternalProcessResult(
            process.ExitCode,
            timedOut,
            stopwatch.Elapsed,
            standardOutputLines,
            standardErrorLines);
    }

    private static Process CreateProcess(ExternalProcessRequest request)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            Arguments = request.Arguments ?? string.Empty,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = request.StandardInputResponseFactory != null,
            CreateNoWindow = true
        };

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }

        return new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
    }

    private static async Task PumpOutputAsync(
        Process process,
        ICollection<string> sink,
        Action<string>? lineHandler,
        Func<string, string?>? inputResponder)
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

    private static async Task PumpErrorAsync(
        Process process,
        ICollection<string> sink,
        Action<string>? lineHandler)
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

    private static void TryTerminateProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Ignore kill failures.
        }
    }
}
