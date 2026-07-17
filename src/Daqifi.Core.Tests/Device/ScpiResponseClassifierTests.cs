using Daqifi.Core.Device;
using Xunit;

namespace Daqifi.Core.Tests.Device
{
    public class ScpiResponseClassifierTests
    {
        [Theory]
        [InlineData("**ERROR: -200, \"Execution error\"")]
        [InlineData("**ERROR -200, \"Execution error\"")]
        [InlineData("**ERROR\t-200, \"Execution error\"")]
        [InlineData("ERROR: -200, \"Execution error\"")]
        [InlineData("ERROR -200, \"Execution error\"")]
        [InlineData("ERROR\t-200, \"Execution error\"")]
        [InlineData("  ERROR: -200, \"Execution error\"  \r\n")]
        public void IsScpiErrorLine_MatchesAllDelimiterVariants(string line)
        {
            Assert.True(ScpiResponseClassifier.IsScpiErrorLine(line));
        }

        [Theory]
        [InlineData("Error !! No SD Card Detected")]
        [InlineData("Error!! No SD Card Detected")]
        [InlineData("error_log.bin")]
        [InlineData("Errors.txt")]
        [InlineData("OK")]
        [InlineData("")]
        public void IsScpiErrorLine_DoesNotMatchNonScpiText(string line)
        {
            Assert.False(ScpiResponseClassifier.IsScpiErrorLine(line));
        }
    }
}
