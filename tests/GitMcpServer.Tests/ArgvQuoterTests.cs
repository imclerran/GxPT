using System.Collections.Generic;
using GitMcpServer;
using Xunit;

namespace GitMcpServer.Tests
{
    /// <summary>
    /// Pure tests for the Windows argv quoter — the security core of the Git server. The golden
    /// rule: each input token must round-trip back to itself under CommandLineToArgvW rules, and
    /// model-supplied values must never be able to inject extra tokens or flags.
    /// </summary>
    public class ArgvQuoterTests
    {
        private static string Join(params string[] tokens)
        {
            return ArgvQuoter.Join(new List<string>(tokens));
        }

        [Fact]
        public void Simple_tokens_are_unquoted()
        {
            Assert.Equal("status --porcelain=v1 -b", Join("status", "--porcelain=v1", "-b"));
        }

        [Fact]
        public void Tokens_with_spaces_are_quoted()
        {
            Assert.Equal("commit -F \"my message\"", Join("commit", "-F", "my message"));
        }

        [Fact]
        public void Embedded_quote_is_escaped()
        {
            // a"b  →  "a\"b"
            Assert.Equal("\"a\\\"b\"", Join("a\"b"));
        }

        [Fact]
        public void Trailing_backslashes_before_closing_quote_are_doubled()
        {
            // path\  (with a space forcing quotes)  →  "a b\\"
            Assert.Equal("\"a b\\\\\"", Join("a b\\"));
        }

        [Fact]
        public void Backslash_not_before_quote_is_left_alone()
        {
            // C:\repo\src has no spaces/quotes → emitted bare, backslashes untouched
            Assert.Equal("C:\\repo\\src", Join("C:\\repo\\src"));
        }

        [Fact]
        public void Empty_token_becomes_empty_quotes()
        {
            Assert.Equal("\"\"", Join(""));
        }

        [Fact]
        public void Injection_attempt_stays_one_token()
        {
            // A malicious "path" trying to add a flag must remain a single quoted argument.
            string joined = Join("diff", "--", "foo; rm -rf /");
            Assert.Equal("diff -- \"foo; rm -rf /\"", joined);
        }

        [Fact]
        public void Value_that_looks_like_a_flag_is_still_one_token_after_separator()
        {
            // After "--", a path that looks like a flag is preserved verbatim (quoted only if needed).
            Assert.Equal("diff -- --evil", Join("diff", "--", "--evil"));
        }
    }
}
