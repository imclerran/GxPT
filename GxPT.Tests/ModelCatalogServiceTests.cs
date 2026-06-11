using System.Collections.Generic;
using GxPT;
using Xunit;

namespace GxPT.Tests
{
    // Pure-logic coverage for the model context-size catalog: the /models JSON parse, the
    // "id<TAB>tokens" file format round-trip, and the alias/variant-tolerant lookup that the
    // status bar's context meter depends on. (The curl fetch itself needs a network and is
    // exercised manually.)
    public class ModelCatalogServiceTests
    {
        // ---- ParseModelsJson ----

        [Fact]
        public void Parses_ids_and_context_lengths_from_models_payload()
        {
            string json = @"{ ""data"": [
                { ""id"": ""anthropic/claude-sonnet-4.5"", ""context_length"": 200000, ""name"": ""x"" },
                { ""id"": ""openai/gpt-4o"", ""context_length"": 128000 }
            ] }";
            var map = ModelCatalogService.ParseModelsJson(json);
            Assert.NotNull(map);
            Assert.Equal(2, map.Count);
            Assert.Equal(200000, map["anthropic/claude-sonnet-4.5"]);
            Assert.Equal(128000, map["openai/gpt-4o"]);
        }

        [Fact]
        public void Skips_entries_without_a_usable_context_length()
        {
            string json = @"{ ""data"": [
                { ""id"": ""a/no-length"" },
                { ""id"": ""b/null-length"", ""context_length"": null },
                { ""id"": ""c/zero"", ""context_length"": 0 },
                { ""id"": ""d/ok"", ""context_length"": 32768 },
                { ""context_length"": 9999 },
                ""not-an-object""
            ] }";
            var map = ModelCatalogService.ParseModelsJson(json);
            Assert.NotNull(map);
            Assert.Single(map);
            Assert.Equal(32768, map["d/ok"]);
        }

        [Fact]
        public void Returns_null_for_malformed_or_unexpected_payloads()
        {
            Assert.Null(ModelCatalogService.ParseModelsJson("not json"));
            Assert.Null(ModelCatalogService.ParseModelsJson(@"{ ""error"": ""nope"" }"));
            Assert.Null(ModelCatalogService.ParseModelsJson(@"{ ""data"": ""not-an-array"" }"));
        }

        // ---- catalog file format ----

        [Fact]
        public void Catalog_file_round_trips_and_is_sorted()
        {
            var map = new Dictionary<string, int>
            {
                { "openai/gpt-4o", 128000 },
                { "anthropic/claude-sonnet-4.5", 200000 }
            };
            string text = ModelCatalogService.FormatCatalogFile(map);
            // Sorted by id, one tab-separated line each.
            Assert.Equal("anthropic/claude-sonnet-4.5\t200000\nopenai/gpt-4o\t128000\n", text);

            var back = ModelCatalogService.ParseCatalogFile(text);
            Assert.Equal(2, back.Count);
            Assert.Equal(200000, back["anthropic/claude-sonnet-4.5"]);
            Assert.Equal(128000, back["openai/gpt-4o"]);
        }

        [Fact]
        public void Catalog_file_parse_tolerates_comments_blanks_and_garbage()
        {
            string text = "# hand-edited\r\n\r\nno-tab-here\nbad/number\tNaN\nok/model\t100000\n\tmissing-id\n";
            var map = ModelCatalogService.ParseCatalogFile(text);
            Assert.Single(map);
            Assert.Equal(100000, map["ok/model"]);
        }

        // ---- TryGetContextLength lookup ladder ----

        [Fact]
        public void Lookup_matches_verbatim_alias_and_variant_forms()
        {
            ModelCatalogService.SetMapForTests(new Dictionary<string, int>
            {
                { "anthropic/claude-sonnet-latest", 200000 },
                { "deepseek/deepseek-v4-pro", 164000 },
                { "meta-llama/llama-3-8b:free", 8192 }
            });
            try
            {
                int ctx;
                // Verbatim.
                Assert.True(ModelCatalogService.TryGetContextLength("deepseek/deepseek-v4-pro", out ctx));
                Assert.Equal(164000, ctx);
                // ":free" entries exist in the catalog and match verbatim.
                Assert.True(ModelCatalogService.TryGetContextLength("meta-llama/llama-3-8b:free", out ctx));
                Assert.Equal(8192, ctx);
                // "~" alias marker stripped.
                Assert.True(ModelCatalogService.TryGetContextLength("~anthropic/claude-sonnet-latest", out ctx));
                Assert.Equal(200000, ctx);
                // ":variant" routing suffix stripped when the suffixed id isn't listed.
                Assert.True(ModelCatalogService.TryGetContextLength("deepseek/deepseek-v4-pro:nitro", out ctx));
                Assert.Equal(164000, ctx);
                // Unknown model: false and zero (status bar falls back to a bare token count).
                Assert.False(ModelCatalogService.TryGetContextLength("nobody/ever-heard-of-it", out ctx));
                Assert.Equal(0, ctx);
                Assert.False(ModelCatalogService.TryGetContextLength(null, out ctx));
                Assert.False(ModelCatalogService.TryGetContextLength("  ", out ctx));
            }
            finally
            {
                ModelCatalogService.SetMapForTests(new Dictionary<string, int>());
            }
        }
    }
}
