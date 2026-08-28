using BenchmarkDotNet.Running;

namespace Daqifi.Core.Benchmarks;

/// <summary>
/// Entry point for the benchmark harness (issue #640).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BenchmarkSwitcher"/> rather than a hard-coded run, so the standard BenchmarkDotNet
/// command line works: <c>--filter *Decode*</c> to run one family, <c>--job short</c> for a quick
/// look, <c>--list flat</c> to see what is available. With no arguments it prompts for a family.
/// </para>
/// <para>
/// This project is never run by CI on a pull request. See the README for why, and
/// <c>.github/workflows/benchmarks.yml</c> for the on-demand job.
/// </para>
/// </remarks>
public static class Program
{
    /// <summary>
    /// Runs the benchmarks selected by <paramref name="args"/>.
    /// </summary>
    /// <param name="args">BenchmarkDotNet switcher arguments.</param>
    public static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
