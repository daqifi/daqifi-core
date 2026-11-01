using System.Diagnostics;
using System.Globalization;
using Daqifi.Core.Firmware;
using Daqifi.Core.Firmware.Internal;

namespace Daqifi.Core.Tests.Firmware;

public sealed class NativeProcessFamilyTests
{
    [Fact]
    public async Task RunAsync_UnixWorkerOutlivesLauncher_UsesTheRequestedBudget()
    {
        if (OperatingSystem.IsWindows()) return;
        var runner = new ProcessExternalProcessRunner(SystemProcessHandleFactory.Instance, TimeSpan.FromMilliseconds(50));
        var result = await runner.RunAsync(new ExternalProcessRequest
        {
            FileName = "/bin/sh",
            Arguments = "-c \"(sleep 0.3; echo worker-done) &\"",
            Timeout = TimeSpan.FromSeconds(5)
        }).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(result.TimedOut);
        Assert.Contains("worker-done", result.StandardOutputLines);
    }

    [Fact]
    public async Task RunAsync_UnixTimeoutAfterLauncherExit_EndsTheOrphanedWorker()
    {
        if (OperatingSystem.IsWindows()) return;
        var runner = new ProcessExternalProcessRunner(SystemProcessHandleFactory.Instance, TimeSpan.FromMilliseconds(100));
        int? workerId = null;
        try
        {
            var result = await runner.RunAsync(new ExternalProcessRequest
            {
                FileName = "/bin/sh",
                Arguments = "-c \"sleep 30 & echo $!\"",
                Timeout = TimeSpan.FromMilliseconds(500)
            }).WaitAsync(TimeSpan.FromSeconds(10));
            workerId = int.Parse(Assert.Single(result.StandardOutputLines), CultureInfo.InvariantCulture);
            Assert.True(result.TimedOut);
            try
            {
                using var worker = Process.GetProcessById(workerId.Value);
                await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (ArgumentException)
            {
                // Already reaped by the operating system.
            }
        }
        finally
        {
            if (workerId is { } id)
            {
                try
                {
                    using var worker = Process.GetProcessById(id);
                    if (!worker.HasExited) worker.Kill();
                }
                catch (ArgumentException)
                {
                }
            }
        }
    }

    [Fact]
    public async Task RunAsync_UnixNativeBackend_PreservesInputErrorDirectoryAndExitCode()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), $"daqifi-process-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var script = Path.Combine(directory, "echo arguments.sh");
        File.WriteAllText(script, "echo ready\nread answer\nprintf '%s\\n' \"$answer\"\npwd\necho warning >&2\nexit 7\n");
        try
        {
            var result = await new ProcessExternalProcessRunner().RunAsync(new ExternalProcessRequest
            {
                FileName = "/bin/sh",
                Arguments = $"\"{script}\"",
                WorkingDirectory = directory,
                StandardInputResponseFactory = line => line == "ready" ? "answer with spaces" : null,
                Timeout = TimeSpan.FromSeconds(5)
            });
            Assert.Equal(7, result.ExitCode);
            Assert.False(result.TimedOut);
            Assert.Contains("answer with spaces", result.StandardOutputLines);
            Assert.Contains(result.StandardOutputLines, line => Path.GetFileName(line) == Path.GetFileName(directory));
            Assert.Contains("warning", result.StandardErrorLines);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("one two")]
    [InlineData("\"\" \"two words\" tail")]
    [InlineData("a\\\\b \"C:\\space dir\\\\\" \"a\\\"b\"")]
    [InlineData("\"a\"\"b\" 'literal-quotes'")]
    public async Task SplitArguments_MatchesTheRuntimeParser(string arguments)
    {
        if (OperatingSystem.IsWindows()) return;
        var script = Path.GetTempFileName();
        File.WriteAllText(script, "for arg do printf '<%s>\\n' \"$arg\"; done\n");
        try
        {
            using var reference = Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/sh",
                Arguments = $"\"{script}\" {arguments}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!;
            var actual = await reference.StandardOutput.ReadToEndAsync();
            await reference.WaitForExitAsync();
            var expected = string.Concat(UnixProcessGroupHandle.SplitArguments(arguments).Select(arg => $"<{arg}>\n"));
            Assert.Equal(expected, actual);
        }
        finally
        {
            File.Delete(script);
        }
    }
}
