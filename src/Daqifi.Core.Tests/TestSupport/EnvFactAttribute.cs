namespace Daqifi.Core.Tests.TestSupport;

/// <summary>
/// A <see cref="FactAttribute"/> that reports the test as <em>skipped</em> when a named
/// environment variable is missing or blank, rather than letting it run and assert nothing.
/// </summary>
/// <remarks>
/// A bare <c>return</c> after logging SKIPPED reports Passed while asserting nothing. This
/// constructor sets <see cref="FactAttribute.Skip"/> when the variable is empty so xUnit
/// records Skipped instead; set the variable and the test runs.
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

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
        {
            Skip = because;
        }
    }

    /// <summary>Gets the environment variable this test requires.</summary>
    public string Variable { get; }
}
