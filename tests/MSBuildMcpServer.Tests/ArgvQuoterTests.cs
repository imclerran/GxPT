using System.Collections.Generic;
using MSBuildMcpServer;
using Xunit;

namespace MSBuildMcpServer.Tests
{
    /// <summary>
    /// Pure tests for the Windows argv quoter — the security core that keeps model-supplied values
    /// (project paths, property values, a "Any CPU" platform) from injecting extra tokens or flags.
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
            Assert.Equal("MyApp.sln /t:Build /nologo", Join("MyApp.sln", "/t:Build", "/nologo"));
        }

        [Fact]
        public void Property_value_with_spaces_is_quoted()
        {
            // /p:Platform=Any CPU contains a space → wrapped so MSBuild sees one token.
            Assert.Equal("\"/p:Platform=Any CPU\"", Join("/p:Platform=Any CPU"));
        }

        [Fact]
        public void Path_with_spaces_is_quoted()
        {
            Assert.Equal("\"C:\\My Projects\\App.csproj\"", Join("C:\\My Projects\\App.csproj"));
        }

        [Fact]
        public void Trailing_backslashes_before_closing_quote_are_doubled()
        {
            Assert.Equal("\"a b\\\\\"", Join("a b\\"));
        }

        [Fact]
        public void Backslash_not_before_quote_is_left_alone()
        {
            Assert.Equal("C:\\repo\\src", Join("C:\\repo\\src"));
        }
    }
}
