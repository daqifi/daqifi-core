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

        [Theory]
        [InlineData("0,\"No error\"")]                    // clean queue — the common case
        [InlineData("-200,\"Execution error\"")]
        [InlineData("+0,\"No error\"")]                   // explicit positive sign
        [InlineData("-420, \"Query UNTERMINATED\"")]      // space after the comma
        [InlineData("  0,\"No error\"  \r\n")]            // leading/trailing whitespace + CRLF
        [InlineData("0,\"\"")]                            // empty message
        public void IsSystemErrorReplyLine_MatchesErrorQueueReplies(string line)
        {
            Assert.True(ScpiResponseClassifier.IsSystemErrorReplyLine(line));
        }

        [Theory]
        [InlineData("Daqifi/log_20240115_103000.bin 1024")]  // SD listing entry
        [InlineData("Daqifi/log_20240115_103000.bin")]       // listing entry with no size
        [InlineData("0,\"No error\" 1024")]                  // reply shape but with a trailing size
        [InlineData("**ERROR: -200, \"Execution error\"")]   // ERROR-prefixed, not a query reply
        [InlineData("Error !! No SD Card Detected")]
        [InlineData("0 1024")]                               // no comma
        [InlineData("0,No error")]                           // unquoted message
        [InlineData("0,\"")]                                 // unterminated quote
        [InlineData("-,\"No error\"")]                       // sign with no digits
        [InlineData("[Error:3]Failed to open directory")]
        [InlineData("")]
        public void IsSystemErrorReplyLine_DoesNotMatchListingOrOtherText(string line)
        {
            Assert.False(ScpiResponseClassifier.IsSystemErrorReplyLine(line));
        }

        [Theory]
        [InlineData("0,\"No error\"", 0)]
        [InlineData("+0,\"No error\"", 0)]
        [InlineData("-200,\"Execution error\"", -200)]
        [InlineData("-420, \"Query UNTERMINATED\"", -420)]
        [InlineData("  -113,\"Undefined header\"  \r\n", -113)]
        public void TryParseSystemErrorReplyCode_ReadsTheCode(string line, int expected)
        {
            Assert.True(ScpiResponseClassifier.TryParseSystemErrorReplyCode(line, out var code));
            Assert.Equal(expected, code);
        }

        [Theory]
        [InlineData("**ERROR: -200,\"Execution error\"")]  // the volunteered form, not a queue reply
        [InlineData("Error !! No SD Card Detected")]
        [InlineData("99999999999999,\"Overflows an int\"")]
        [InlineData("")]
        public void TryParseSystemErrorReplyCode_RejectsWhatIsNotACode(string line)
        {
            Assert.False(ScpiResponseClassifier.TryParseSystemErrorReplyCode(line, out var code));
            Assert.Equal(0, code);
        }

        [Theory]
        // The device reports the same error two ways: bare in a SYSTem:ERRor? reply, and prefixed
        // with an ERROR token when it volunteers one alongside a command's output. Both readers now
        // share one numeric-prefix parse, so this pins them to the same answer for the same code.
        [InlineData("-200,\"Execution error\"", "**ERROR: -200,\"Execution error\"", -200)]
        [InlineData("-113, \"Undefined header\"", "ERROR\t-113, \"Undefined header\"", -113)]
        [InlineData("  0,\"No error\"  \r\n", "  ERROR: 0, \"No error\"  \r\n", 0)]
        public void BothErrorFormsReadTheSameCode(string queueReply, string volunteeredLine, int expected)
        {
            Assert.True(ScpiResponseClassifier.TryParseSystemErrorReplyCode(queueReply, out var replyCode));
            Assert.True(ScpiResponseClassifier.TryExtractErrorCode(volunteeredLine, out var volunteeredCode));

            Assert.Equal(expected, replyCode);
            Assert.Equal(expected, volunteeredCode);
        }

        [Theory]
        // The shape captured off the bench in #537: a partial protobuf frame welded onto the front
        // of the first reply line, with the real key and value still attached behind it.
        [InlineData("\u0008\uFFFD\\3\uFFFD\u0004\u0012\u0003\u0008\u0000TotalSamplesStreamed=203")]
        [InlineData("\u0000TotalSamplesStreamed=203")]        // a lone NUL is enough
        [InlineData("STREAM: 2 (ceiling 3)\u0007")]           // junk trailing the line, not leading it
        [InlineData("\uFFFD3")]                               // UTF-8 could not decode the byte at all
        [InlineData("\u007F42")]                              // DEL
        [InlineData("\u009F42")]                              // a C1 control from a decoded 2-byte sequence
        public void IsBinaryCorruptedLine_DetectsNonTextBytes(string line)
        {
            Assert.True(ScpiResponseClassifier.IsBinaryCorruptedLine(line));
        }

        [Theory]
        [InlineData("TotalSamplesStreamed=203")]
        [InlineData("STREAM: 2 (ceiling 3)")]
        [InlineData("**ERROR: -200, \"Execution error\"")]
        [InlineData("ERROR\t-200, \"Execution error\"")]      // tab is real device output, not corruption
        [InlineData("DAQiFi/log_20260802_153435.bin 1539")]
        [InlineData("  padded  ")]
        [InlineData("")]
        [InlineData(null)]
        public void IsBinaryCorruptedLine_PassesRealDeviceText(string? line)
        {
            Assert.False(ScpiResponseClassifier.IsBinaryCorruptedLine(line));
        }

        [Fact]
        public void ContainsBinaryCorruptedLine_FindsCorruptionAnywhereInTheResponse()
        {
            var clean = new[] { "TotalSamplesStreamed=203", "QueueDroppedSamples=0" };
            var corrupted = new[] { "TotalSamplesStreamed=203", "\u0000QueueDroppedSamples=0" };

            Assert.False(ScpiResponseClassifier.ContainsBinaryCorruptedLine(clean));
            Assert.True(ScpiResponseClassifier.ContainsBinaryCorruptedLine(corrupted));
        }
    }
}
