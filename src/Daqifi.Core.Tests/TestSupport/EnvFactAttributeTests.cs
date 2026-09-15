using System.Reflection;
using Daqifi.Core.Tests.Communication.Transport;

namespace Daqifi.Core.Tests.TestSupport;

/// <summary>
/// Covers the environment gate that replaced the bare <c>return</c>s in
/// <see cref="SerialUnplugValidationTests"/>. The decision itself is pure —
/// <see cref="EnvFactAttribute.ShouldSkip"/> takes the variable value as an argument — so every
/// empty shape is exercised without needing hardware.
/// </summary>
public class EnvFactAttributeTests
{
    #region The skip decision, against every empty shape rather than the host's env

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ShouldSkip_ValueIsMissing_Skips(string? value)
    {
        Assert.True(EnvFactAttribute.ShouldSkip(value));
    }

    [Theory]
    [InlineData("COM3")]
    [InlineData("/dev/cu.usbmodem1101")]
    public void ShouldSkip_ValueIsPresent_Runs(string value)
    {
        Assert.False(EnvFactAttribute.ShouldSkip(value));
    }

    #endregion

    #region What the attribute hands xunit

    [Fact]
    public void Skip_VariableIsUnset_IsSetToTheReason()
    {
        var variable = UniqueVariable();
        Environment.SetEnvironmentVariable(variable, null);
        try
        {
            var attribute = new EnvFactAttribute(variable, "because reasons");

            Assert.Equal("because reasons", attribute.Skip);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void Skip_VariableIsSet_IsNullSoTheTestRuns()
    {
        var variable = UniqueVariable();
        Environment.SetEnvironmentVariable(variable, "COM3");
        try
        {
            var attribute = new EnvFactAttribute(variable, "because reasons");

            Assert.Null(attribute.Skip);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void Skip_VariableIsWhitespace_IsSetToTheReason()
    {
        var variable = UniqueVariable();
        Environment.SetEnvironmentVariable(variable, "   ");
        try
        {
            var attribute = new EnvFactAttribute(variable, "because reasons");

            Assert.Equal("because reasons", attribute.Skip);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void Variable_IsTheNameItWasGiven()
    {
        var variable = UniqueVariable();

        Assert.Equal(variable, new EnvFactAttribute(variable, "because reasons").Variable);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_VariableIsMissing_Throws(string? variable)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => new EnvFactAttribute(variable!, "because reasons"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_ReasonIsMissing_Throws(string? because)
    {
        // The reason becomes the skip message a reader of the results sees, so an empty one would
        // leave them with a skipped test and no explanation.
        // ThrowsAny: the null case surfaces as ArgumentNullException, a subclass.
        Assert.ThrowsAny<ArgumentException>(
            () => new EnvFactAttribute("DAQIFI_UNPLUG_PORT", because!));
    }

    #endregion

    #region The two gates it was introduced for

    [Theory]
    [InlineData(nameof(SerialUnplugValidationTests.UnpluggedSerialDevice_ReportsConnectionLost_WithinTheDocumentedBound))]
    [InlineData(nameof(SerialUnplugValidationTests.UnpluggedSerialDevice_IsPrunedFromTheRegistry))]
    public void UnplugValidationTests_CarryTheEnvFact_SoTheyCannotSilentlyReturnAgain(string testMethod)
    {
        // Pins the fix: each of these used to be a [Fact] whose body opened with
        // "if the env var is unset, log SKIPPED and return;", reporting passed while asserting nothing.
        var method = typeof(SerialUnplugValidationTests).GetMethod(testMethod);
        Assert.NotNull(method);

        var gate = method.GetCustomAttribute<EnvFactAttribute>();
        Assert.NotNull(gate);
        Assert.Equal("DAQIFI_UNPLUG_PORT", gate.Variable);
    }

    #endregion

    private static string UniqueVariable() => $"DAQIFI_TEST_ENVFACT_{Guid.NewGuid():N}";
}
