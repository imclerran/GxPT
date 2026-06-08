using System.Collections.Generic;
using System.Linq;
using GxPT;
using Xunit;

namespace GxPT.Tests.Commands
{
    public class ClientCommandsTests
    {
        private static FakeSlashCommandContext CtxWithModels()
        {
            var ctx = new FakeSlashCommandContext("/work");
            ctx.Models.AddRange(new[]
            {
                "openai/gpt-4o", "openai/gpt-4o-mini", "anthropic/claude-3.5-sonnet"
            });
            return ctx;
        }

        // ---- /model completion (two-level: author/ then model) ----

        [Fact]
        public void Model_completes_distinct_authors_first()
        {
            var cmd = new ModelCommand();
            var items = cmd.CompleteArgument("", CtxWithModels());
            var inserts = items.Select(i => i.InsertArg).ToList();

            Assert.Contains("openai/", inserts);
            Assert.Contains("anthropic/", inserts);
            Assert.Equal(2, inserts.Count); // openai appears once despite two models
            Assert.All(items, i => Assert.True(i.ContinueCompleting)); // author level keeps completing
        }

        [Fact]
        public void Model_filters_authors_by_prefix()
        {
            var items = new ModelCommand().CompleteArgument("ant", CtxWithModels());
            Assert.Single(items);
            Assert.Equal("anthropic/", items[0].InsertArg);
        }

        [Fact]
        public void Model_completes_models_under_author()
        {
            var items = new ModelCommand().CompleteArgument("openai/", CtxWithModels());
            var inserts = items.Select(i => i.InsertArg).ToList();

            Assert.Contains("openai/gpt-4o", inserts);
            Assert.Contains("openai/gpt-4o-mini", inserts);
            Assert.DoesNotContain("anthropic/claude-3.5-sonnet", inserts);
            Assert.All(items, i => Assert.False(i.ContinueCompleting)); // model level is terminal
        }

        [Fact]
        public void Model_filters_models_by_partial()
        {
            var items = new ModelCommand().CompleteArgument("openai/gpt-4o-m", CtxWithModels());
            Assert.Single(items);
            Assert.Equal("openai/gpt-4o-mini", items[0].InsertArg);
        }

        [Fact]
        public void Model_invoke_sets_the_model()
        {
            var ctx = CtxWithModels();
            var result = new ModelCommand().Invoke("openai/gpt-4o", ctx);
            Assert.False(result.SendToModel);
            Assert.Null(result.Error);
            Assert.Equal("openai/gpt-4o", ctx.LastModelSet);
        }

        [Fact]
        public void Model_invoke_without_arg_fails()
        {
            var result = new ModelCommand().Invoke("", CtxWithModels());
            Assert.NotNull(result.Error);
        }

        // ---- /server completion + invoke ----

        private static FakeSlashCommandContext CtxWithServers()
        {
            var ctx = new FakeSlashCommandContext("/work");
            ctx.ServerStates["git"] = true;
            ctx.ServerStates["files"] = false;
            ctx.ServerStates["command"] = false;
            return ctx;
        }

        [Fact]
        public void Server_completes_names_then_onoff()
        {
            var cmd = new ServerCommand();
            var names = cmd.CompleteArgument("", CtxWithServers()).Select(i => i.InsertArg).ToList();
            Assert.Contains("git ", names);
            Assert.Contains("files ", names);

            var choices = cmd.CompleteArgument("git ", CtxWithServers()).Select(i => i.InsertArg).ToList();
            Assert.Contains("git on", choices);
            Assert.Contains("git off", choices);
        }

        [Fact]
        public void Server_invoke_explicit_on_off()
        {
            var ctx = CtxWithServers();
            new ServerCommand().Invoke("files on", ctx);
            Assert.True(ctx.ServerStates["files"]);

            new ServerCommand().Invoke("git off", ctx);
            Assert.False(ctx.ServerStates["git"]);
        }

        [Fact]
        public void Server_invoke_toggles_when_state_omitted()
        {
            var ctx = CtxWithServers(); // git starts on
            new ServerCommand().Invoke("git", ctx);
            Assert.False(ctx.ServerStates["git"]);
            new ServerCommand().Invoke("git", ctx);
            Assert.True(ctx.ServerStates["git"]);
        }

        [Fact]
        public void Server_invoke_unknown_name_fails()
        {
            var result = new ServerCommand().Invoke("bogus on", CtxWithServers());
            Assert.NotNull(result.Error);
        }

        // ---- /new and /export ----

        [Fact]
        public void New_opens_a_conversation()
        {
            var ctx = new FakeSlashCommandContext("/work");
            var result = new NewCommand().Invoke("", ctx);
            Assert.False(result.SendToModel);
            Assert.Equal(1, ctx.NewConversationCount);
        }

        [Fact]
        public void Export_triggers_export()
        {
            var ctx = new FakeSlashCommandContext("/work");
            new ExportCommand().Invoke("", ctx);
            Assert.Equal(1, ctx.ExportCount);
        }

        // ---- registration / dispatch through the processor ----

        [Fact]
        public void Client_commands_dispatch_through_the_processor()
        {
            var registry = new SlashCommandRegistry(ClientCommands.BuiltIns());
            var processor = new SlashCommandProcessor(registry);
            var ctx = CtxWithModels();

            var result = processor.Process("/model openai/gpt-4o", ctx);
            Assert.NotNull(result);
            Assert.False(result.SendToModel);     // client command: not sent to the model
            Assert.Equal("openai/gpt-4o", ctx.LastModelSet);
        }
    }
}
