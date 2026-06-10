using System.Collections.Generic;
using Mcp35.Core.Transport;
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

        // ---- http ----

        [Theory]
        [InlineData("https://api.test/v1")]
        [InlineData("http://example.com")]
        public void ValidateHttpUrl_accepts_http_and_https(string url)
        {
            Assert.Null(WebSearchTools.ValidateHttpUrl(url));
        }

        [Theory]
        [InlineData("", "required")]
        [InlineData("/relative/path", "absolute")]
        [InlineData("file:///etc/passwd", "http")]
        [InlineData("ftp://host/file", "http")]
        public void ValidateHttpUrl_rejects_non_http(string url, string fragment)
        {
            string err = WebSearchTools.ValidateHttpUrl(url);
            Assert.NotNull(err);
            Assert.Contains(fragment, err);
        }

        [Fact]
        public void NormalizeMethod_defaults_to_get_and_uppercases()
        {
            Assert.Equal("GET", WebSearchTools.NormalizeMethod(null));
            Assert.Equal("GET", WebSearchTools.NormalizeMethod(""));
            Assert.Equal("POST", WebSearchTools.NormalizeMethod("post"));
            Assert.Equal("DELETE", WebSearchTools.NormalizeMethod("  delete "));
        }

        [Theory]
        [InlineData("HEAD")]   // intentionally unsupported (curl -X HEAD hangs)
        [InlineData("TRACE")]
        [InlineData("bogus")]
        public void NormalizeMethod_rejects_unsupported(string method)
        {
            Assert.Null(WebSearchTools.NormalizeMethod(method));
        }

        [Fact]
        public void BuildHttpHeaders_maps_strings_and_skips_nulls()
        {
            JObject h = JObject.Parse("{\"Accept\":\"application/json\",\"X-Empty\":null}");
            IDictionary<string, string> map = WebSearchTools.BuildHttpHeaders(h);
            Assert.Equal("application/json", map["Accept"]);
            Assert.False(map.ContainsKey("X-Empty"));
        }

        [Fact]
        public void BuildHttpHeaders_null_is_empty()
        {
            Assert.Empty(WebSearchTools.BuildHttpHeaders(null));
        }

        [Fact]
        public void BuildHttpRequest_sets_fields_and_attaches_body_when_present()
        {
            JObject args = new JObject();
            args["headers"] = JObject.Parse("{\"Content-Type\":\"application/json\"}");
            args["body"] = "{\"a\":1}";
            CurlRequest req = WebSearchTools.BuildHttpRequest(Ctx(args), "https://api.test/", "POST");

            Assert.Equal("https://api.test/", req.Url);
            Assert.Equal("POST", req.Method);
            Assert.Equal("{\"a\":1}", req.BodyJson);
            Assert.Equal("application/json", req.Headers["Content-Type"]);
            Assert.Equal(30000, req.TimeoutMs); // default
        }

        [Fact]
        public void BuildHttpRequest_get_has_no_body_and_clamps_timeout()
        {
            JObject args = new JObject();
            args["timeout_ms"] = 999999; // over the 120000 cap
            CurlRequest req = WebSearchTools.BuildHttpRequest(Ctx(args), "https://api.test/", "GET");

            Assert.Null(req.BodyJson);
            Assert.Equal(120000, req.TimeoutMs);
        }

        [Fact]
        public void CondenseHttp_projects_status_headers_and_body()
        {
            CurlResult r = new CurlResult();
            r.HttpStatus = 201;
            r.Body = "created";
            r.Headers = new Dictionary<string, string> { { "Location", "https://api.test/1" } };

            JObject c = WebSearchTools.CondenseHttp(r);
            Assert.Equal(201, (int)c["status"]);
            Assert.Equal("created", (string)c["body"]);
            Assert.Equal("https://api.test/1", (string)c["headers"]["Location"]);
            Assert.Null(c["truncated"]);
        }

        [Fact]
        public void CondenseHttp_flags_truncation_for_large_body()
        {
            CurlResult r = new CurlResult();
            r.HttpStatus = 200;
            r.Body = new string('x', 100001); // one over the 100000 cap

            JObject c = WebSearchTools.CondenseHttp(r);
            Assert.True((bool)c["truncated"]);
            Assert.Equal(100000, ((string)c["body"]).Length);
        }
    }
}
