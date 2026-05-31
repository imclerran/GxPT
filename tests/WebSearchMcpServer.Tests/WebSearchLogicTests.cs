using Mcp35.Server;
using Newtonsoft.Json.Linq;
using WebSearchMcpServer;
using Xunit;

namespace WebSearchMcpServer.Tests
{
    /// <summary>Pure-function tests for the request builders, condensers, and URL normalization.</summary>
    public class WebSearchLogicTests
    {
        private static ToolCallContext Ctx(JObject args)
        {
            return new ToolCallContext("search", args, null);
        }

        // ---- search request building ----

        [Fact]
        public void Search_request_includes_query_and_defaults_max_results()
        {
            JObject args = new JObject();
            args["query"] = "hello world";
            JObject body = WebSearchTools.BuildSearchRequest(Ctx(args));

            Assert.Equal("hello world", (string)body["query"]);
            Assert.Equal(5, (int)body["max_results"]); // default
        }

        [Fact]
        public void Search_request_clamps_max_results_to_20()
        {
            JObject args = new JObject();
            args["query"] = "x";
            args["max_results"] = 999;
            JObject body = WebSearchTools.BuildSearchRequest(Ctx(args));
            Assert.Equal(20, (int)body["max_results"]);
        }

        [Fact]
        public void Search_request_passes_through_optional_params_only_when_present()
        {
            JObject args = new JObject();
            args["query"] = "x";
            args["topic"] = "news";
            args["include_answer"] = true;
            args["include_domains"] = new JArray("a.com", "b.com");
            JObject body = WebSearchTools.BuildSearchRequest(Ctx(args));

            Assert.Equal("news", (string)body["topic"]);
            Assert.True((bool)body["include_answer"]);
            Assert.Equal(2, ((JArray)body["include_domains"]).Count);
            // absent ones are not emitted
            Assert.Null(body["country"]);
            Assert.Null(body["time_range"]);
            Assert.Null(body["exclude_domains"]); // empty/absent array omitted
        }

        // ---- search condensing ----

        [Fact]
        public void CondenseSearch_projects_results_and_answer()
        {
            JObject raw = JObject.Parse(
                "{\"answer\":\"Leo Messi is a footballer.\",\"results\":[" +
                "{\"title\":\"Messi\",\"url\":\"https://w.test\",\"content\":\"snippet\",\"score\":0.9,\"extra\":\"drop\"}]}");

            JObject c = WebSearchTools.CondenseSearch(raw);
            Assert.Equal("Leo Messi is a footballer.", (string)c["answer"]);
            JArray results = (JArray)c["results"];
            Assert.Single(results);
            Assert.Equal("Messi", (string)results[0]["title"]);
            Assert.Equal("https://w.test", (string)results[0]["url"]);
            Assert.NotNull(results[0]["score"]);
            Assert.Null(results[0]["extra"]); // unknown field dropped
        }

        [Fact]
        public void CondenseSearch_handles_no_answer_and_empty_results()
        {
            JObject raw = JObject.Parse("{\"results\":[]}");
            JObject c = WebSearchTools.CondenseSearch(raw);
            Assert.Null(c["answer"]);
            Assert.Empty((JArray)c["results"]);
        }

        // ---- extract ----

        [Fact]
        public void NormalizeUrls_accepts_single_string()
        {
            JArray urls = WebSearchTools.NormalizeUrls(new JValue("https://a.test"));
            Assert.Single(urls);
            Assert.Equal("https://a.test", (string)urls[0]);
        }

        [Fact]
        public void NormalizeUrls_accepts_array_and_drops_empties()
        {
            JArray input = new JArray("https://a.test", "", "https://b.test");
            JArray urls = WebSearchTools.NormalizeUrls(input);
            Assert.Equal(2, urls.Count);
        }

        [Fact]
        public void NormalizeUrls_null_is_empty()
        {
            Assert.Empty(WebSearchTools.NormalizeUrls(null));
            Assert.Empty(WebSearchTools.NormalizeUrls(JValue.CreateNull()));
        }

        [Fact]
        public void Extract_request_carries_urls_and_optional_params()
        {
            JObject args = new JObject();
            args["extract_depth"] = "advanced";
            args["format"] = "markdown";
            JArray urls = new JArray("https://a.test");
            JObject body = WebSearchTools.BuildExtractRequest(Ctx(args), urls);

            Assert.Equal(1, ((JArray)body["urls"]).Count);
            Assert.Equal("advanced", (string)body["extract_depth"]);
            Assert.Equal("markdown", (string)body["format"]);
        }

        [Fact]
        public void CondenseExtract_projects_results_and_failures()
        {
            JObject raw = JObject.Parse(
                "{\"results\":[{\"url\":\"https://a.test\",\"raw_content\":\"# Title\\nbody\"}]," +
                "\"failed_results\":[{\"url\":\"https://bad.test\",\"error\":\"timeout\"}]}");

            JObject c = WebSearchTools.CondenseExtract(raw);
            Assert.Single((JArray)c["results"]);
            Assert.Equal("https://a.test", (string)c["results"][0]["url"]);
            Assert.Contains("Title", (string)c["results"][0]["raw_content"]);
            Assert.Single((JArray)c["failed_results"]);
        }

        [Fact]
        public void CondenseExtract_omits_failures_when_none()
        {
            JObject raw = JObject.Parse("{\"results\":[{\"url\":\"https://a.test\",\"raw_content\":\"x\"}]}");
            JObject c = WebSearchTools.CondenseExtract(raw);
            Assert.Null(c["failed_results"]);
        }
    }
}
