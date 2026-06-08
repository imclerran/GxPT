using System.Collections.Generic;
using GxPT;
using Xunit;

namespace GxPT.Tests.Commands
{
    public class SlashCommandProcessorTests
    {
        private static SlashCommandProcessor BuildProcessor()
        {
            var cmds = SlashCommandConfig.ParseCommands(SlashCommandConfig.DefaultsJson, null);
            return new SlashCommandProcessor(new SlashCommandRegistry(cmds));
        }

        private static ISlashCommandContext AllServers()
        {
            return new FakeSlashCommandContext("/work", "git", "files", "msbuild", "command");
        }

        [Theory]
        [InlineData("hello world")]
        [InlineData("what does /usr/bin/env do?")]
        [InlineData("")]
        [InlineData(null)]
        public void Non_leading_slash_is_not_a_command(string raw)
        {
            var result = BuildProcessor().Process(raw, AllServers());
            Assert.Null(result);
        }

        [Fact]
        public void Bare_slash_is_literal_text()
        {
            Assert.Null(BuildProcessor().Process("/", AllServers()));
        }

        [Fact]
        public void Unknown_command_is_sent_literally()
        {
            // Leading slash but unregistered token -> not hijacked.
            Assert.Null(BuildProcessor().Process("/notacommand do things", AllServers()));
        }

        [Fact]
        public void Known_command_expands_its_template()
        {
            var result = BuildProcessor().Process("/commit", AllServers());
            Assert.NotNull(result);
            Assert.True(result.SendToModel);
            Assert.Contains("commit", result.TextToSend);
            Assert.Null(result.Error);
        }

        [Fact]
        public void Path_argument_is_substituted_into_the_template()
        {
            var result = BuildProcessor().Process("/explain src/Foo.cs", AllServers());
            Assert.True(result.SendToModel);
            Assert.Contains("src/Foo.cs", result.TextToSend);
            Assert.DoesNotContain("{args}", result.TextToSend);
        }

        [Fact]
        public void Absolute_path_argument_is_refused_before_send()
        {
            var result = BuildProcessor().Process("/explain /etc/passwd", AllServers());
            Assert.False(result.SendToModel);
            Assert.NotNull(result.Error);
        }

        [Fact]
        public void Parent_traversal_argument_is_refused()
        {
            var result = BuildProcessor().Process("/explain ../secrets", AllServers());
            Assert.False(result.SendToModel);
            Assert.NotNull(result.Error);
        }

        [Fact]
        public void Command_is_gated_when_required_server_is_missing()
        {
            var ctx = new FakeSlashCommandContext("/work", "files"); // no git
            var result = BuildProcessor().Process("/commit", ctx);
            Assert.False(result.SendToModel);
            Assert.NotNull(result.Error);
            Assert.Contains("git", result.Error);
        }

        [Fact]
        public void RequiresAny_is_satisfied_by_either_server()
        {
            var msbuildOnly = new FakeSlashCommandContext("/work", "msbuild");
            var commandOnly = new FakeSlashCommandContext("/work", "command");

            Assert.True(BuildProcessor().Process("/test", msbuildOnly).SendToModel);
            Assert.True(BuildProcessor().Process("/test", commandOnly).SendToModel);
        }

        [Fact]
        public void RequiresAny_is_gated_when_neither_server_present()
        {
            var ctx = new FakeSlashCommandContext("/work", "git");
            var result = BuildProcessor().Process("/test", ctx);
            Assert.False(result.SendToModel);
            Assert.NotNull(result.Error);
        }
    }
}
