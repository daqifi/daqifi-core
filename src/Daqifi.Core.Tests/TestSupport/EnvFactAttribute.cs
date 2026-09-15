namespace Daqifi.Core.Tests.TestSupport;

/// <summary>
/// A <see cref="FactAttribute"/> that reports the test as <em>skipped</em> when a named
/// environment variable is missing or blank, rather than letting it run and assert nothing.
/// </summary>
/// <remarks>
/// <para>
/// Hardware-in-the-loop tests are meaningless on CI and on ordinary local runs. Handling that
/// with a bare <c>return</c> after logging SKIPPED makes the test report <em>passed</em> while
/// asserting nothing, so the run's skip count says nothing about it. A statically skipped
/// <c>[Fact(Skip = ...)]</c> cannot be turned on by the operator at all.
/// </para>
/// <para>
/// xunit 2.9.3 has no dynamic skip: <c>Assert.Skip</c> arrived in v3, and v2's execution engine
/// does not honour <c>Xunit.Sdk.SkipException</c>. What v2 does honour is
/// <see cref="FactAttribute.Skip"/>, which is read off the attribute instance at discovery time —
/// so the environment check is made in this constructor instead. Discovery and execution happen
/// in the same process on the same machine, so deciding at discovery is sound: set the variable
/// and the test runs; leave it unset and xUnit records Skipped.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class EnvFactAttribute : FactAttribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="EnvFactAttribute"/> class.
    /// </summary>
    /// <param name="variable">The environment variable that must be non-blank for the test to run.</param>
    /// <param name="because">
    /// Why the test cannot run without that variable. Required, and surfaced as the skip reason,
    /// so a reader of the test results learns how to enable it.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="variable"/> or <paramref name="because"/> is null, empty, or white space.
    /// </exception>
    public EnvFactAttribute(string variable, string because)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variable);
        ArgumentException.ThrowIfNullOrWhiteSpace(because);

        Variable = variable;

        if (ShouldSkip(Environment.GetEnvironmentVariable(variable)))
        {
            Skip = because;
        }
    }

    /// <summary>Gets the environment variable this test requires.</summary>
    public string Variable { get; }

    /// <summary>
    /// Decides whether a missing or blank <paramref name="value"/> should skip the test.
    /// </summary>
    /// <remarks>
    /// Split out from the constructor so the decision can be tested without mutating process
    /// environment, and against every empty shape rather than whichever the host happens to have.
    /// </remarks>
    internal static bool ShouldSkip(string? value)
        => string.IsNullOrWhiteSpace(value);
}
