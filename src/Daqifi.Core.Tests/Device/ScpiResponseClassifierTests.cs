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

        [Theory]
        [InlineData("**ERROR: -200, \"Execution error\"", -200)]
        [InlineData("**ERROR -200, \"Execution error\"", -200)]
        [InlineData("**ERROR\t-200, \"Execution error\"", -200)]
        [InlineData("ERROR: -113, \"Undefined header\"", -113)]
        [InlineData("ERROR -113", -113)]                          // no trailing comma
        [InlineData("  ERROR: -200, \"x\"  \r\n", -200)]          // leading/trailing whitespace + CRLF
        [InlineData("ERROR: 42, \"positive\"", 42)]               // positive code
        public void TryExtractErrorCode_ExtractsCode_AcrossDelimiterVariants(string line, int expected)
        {
            Assert.True(ScpiResponseClassifier.TryExtractErrorCode(line, out var code));
            Assert.Equal(expected, code);
        }

        [Theory]
        [InlineData("Error !! No SD Card Detected")]  // ERROR token but non-numeric follow
        [InlineData("error_log.bin")]                 // filename
        [InlineData("Errors.txt")]                    // filename
        [InlineData("OK")]                            // not an error line
        [InlineData("")]
        public void TryExtractErrorCode_ReturnsFalseAndZero_ForNonNumericOrNonError(string line)
        {
            Assert.False(ScpiResponseClassifier.TryExtractErrorCode(line, out var code));
            Assert.Equal(0, code);
        }
    }
}
