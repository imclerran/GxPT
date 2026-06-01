using System;
using Mcp35.Core.Protocol;
using Mcp35.Server;
using Newtonsoft.Json.Linq;

namespace WebSearchMcpServer
{
    /// <summary>
    /// The web server's two tools over the Tavily API:
    ///   web__search  (ReadOnly)    — query the web, condensed results + optional answer.
    ///   web__extract (Write/confirm) — fetch and return full page content for given URLs.
    /// Request builders and response condensers are pure functions (unit-tested directly); the
    /// handlers add the network call. Failures are Error values; the API key never appears in output.
    /// See servers-spec §3.
    /// </summary>
    internal static class WebSearchTools
    {
        private const int DefaultMaxResults = 5;
        private const int MaxMaxResults = 20;

        public static void Register(McpServer server, WebSearchConfig config)
        {
            TavilyClient client = new TavilyClient(config, null);

            server.AddTool("search",
                "Search the web and return relevant results, each with a title, URL, and content snippet (optionally a synthesized answer). " +
                "Start here when you need current information. The returned URLs can be passed to web__extract to read full page content.",
                BuildSearchSchema(),
                delegate(ToolCallContext ctx) { return Search(config, client, ctx); });

            server.AddTool("extract",
                "Fetch the full text of specific web pages you ALREADY have URLs for (for example, URLs returned by web__search). " +
                "Requires the 'urls' argument: one or more absolute http(s) URLs. Do not call this without URLs — run web__search first to find them.",
                BuildExtractSchema(),
                delegate(ToolCallContext ctx) { return Extract(config, client, ctx); });
        }

        // ---- schemas ----

        private static JObject BuildSearchSchema()
        {
            // Full Tavily search surface (resolved decision: rich schema).
            JObject topic = StrEnum("Search topic", new string[] { "general", "news", "finance" });
            JObject depth = StrEnum("Search depth: 'basic' (faster) or 'advanced' (deeper).", new string[] { "basic", "advanced" });

            return SchemaBuilder.Object()
                .Str("query", true, "The search query")
                .Int("max_results", false, "Maximum number of results (default 5, max 20)")
                .Raw("topic", topic, false)
                .Raw("search_depth", depth, false)
                .Int("chunks_per_source", false, "Number of content chunks to retrieve per source (advanced depth)")
                .Bool("include_answer", false, "Include a synthesized answer to the query")
                .Bool("include_raw_content", false, "Include the raw page content for each result")
                .Str("time_range", false, "Relative time filter, e.g. 'day', 'week', 'month', 'year'")
                .Str("start_date", false, "Earliest result date, YYYY-MM-DD")
                .Str("end_date", false, "Latest result date, YYYY-MM-DD")
                .Str("country", false, "Boost results from a country, e.g. 'united states'")
                .Arr("include_domains", "string", false, "Only include results from these domains")
                .Arr("exclude_domains", "string", false, "Exclude results from these domains")
                .Build();
        }

        private static JObject BuildExtractSchema()
        {
            JObject fmt = StrEnum("Output format: 'markdown' (default) or 'text'.", new string[] { "markdown", "text" });
            JObject depth = StrEnum("Extraction depth: 'basic' (faster) or 'advanced' (more thorough).", new string[] { "basic", "advanced" });

            return SchemaBuilder.Object()
                .Arr("urls", "string", true, "Required. One or more absolute http(s) page URLs to fetch (e.g. URLs returned by web__search). Must not be empty.")
                .Raw("extract_depth", depth, false)
                .Raw("format", fmt, false)
                .Bool("include_images", false, "Include image URLs found on the pages")
                .Build();
        }

        // ---- search ----

        private static CallToolResult Search(WebSearchConfig config, TavilyClient client, ToolCallContext ctx)
        {
            string query = ctx.Arguments.Value<string>("query");
            if (string.IsNullOrEmpty(query)) return ToolResults.Error("query is required");

            JObject body = BuildSearchRequest(ctx);
            TavilyResult result = client.Search(body);
            if (!result.Ok) return ToolResults.Error(result.ErrorMessage);

            return ToolResults.Json(CondenseSearch(result.Body));
        }

        /// <summary>Map tool arguments onto a Tavily /search request body. Pure.</summary>
        public static JObject BuildSearchRequest(ToolCallContext ctx)
        {
            JObject b = new JObject();
            b["query"] = ctx.Arguments.Value<string>("query");
            b["max_results"] = ClampInt(ctx, "max_results", DefaultMaxResults, 1, MaxMaxResults);

            CopyStr(ctx, b, "topic");
            CopyStr(ctx, b, "search_depth");
            CopyStr(ctx, b, "time_range");
            CopyStr(ctx, b, "start_date");
            CopyStr(ctx, b, "end_date");
            CopyStr(ctx, b, "country");
            CopyInt(ctx, b, "chunks_per_source");
            CopyBool(ctx, b, "include_answer");
            CopyBool(ctx, b, "include_raw_content");
            CopyArray(ctx, b, "include_domains");
            CopyArray(ctx, b, "exclude_domains");
            return b;
        }

        /// <summary>Condense a Tavily /search response to results[] + optional answer. Pure.</summary>
        public static JObject CondenseSearch(JObject raw)
        {
            JObject outp = new JObject();

            if (raw["answer"] != null && raw["answer"].Type != JTokenType.Null)
                outp["answer"] = StringOf(raw["answer"]);

            JArray results = new JArray();
            JToken arr = raw["results"];
            if (arr != null && arr.Type == JTokenType.Array)
            {
                foreach (JToken item in (JArray)arr)
                {
                    JObject o = item as JObject;
                    if (o == null) continue;
                    JObject r = new JObject();
                    r["title"] = StringOf(o["title"]);
                    r["url"] = StringOf(o["url"]);
                    r["content"] = StringOf(o["content"]);
                    if (o["score"] != null && o["score"].Type != JTokenType.Null) r["score"] = o["score"];
                    if (o["raw_content"] != null && o["raw_content"].Type != JTokenType.Null)
                        r["raw_content"] = StringOf(o["raw_content"]);
                    results.Add(r);
                }
            }
            outp["results"] = results;
            return outp;
        }

        // ---- extract ----

        private static CallToolResult Extract(WebSearchConfig config, TavilyClient client, ToolCallContext ctx)
        {
            JArray urls = NormalizeUrls(ctx.Arguments["urls"]);
            if (urls.Count == 0) return ToolResults.Error("at least one url is required");

            JObject body = BuildExtractRequest(ctx, urls);
            TavilyResult result = client.Extract(body);
            if (!result.Ok) return ToolResults.Error(result.ErrorMessage);

            return ToolResults.Json(CondenseExtract(result.Body));
        }

        /// <summary>Map tool arguments onto a Tavily /extract request body. Pure.</summary>
        public static JObject BuildExtractRequest(ToolCallContext ctx, JArray urls)
        {
            JObject b = new JObject();
            b["urls"] = urls;
            CopyStr(ctx, b, "extract_depth");
            CopyStr(ctx, b, "format");
            CopyBool(ctx, b, "include_images");
            return b;
        }

        /// <summary>Condense a Tavily /extract response to results[] {url, raw_content, images?} + failures. Pure.</summary>
        public static JObject CondenseExtract(JObject raw)
        {
            JObject outp = new JObject();

            JArray results = new JArray();
            JToken arr = raw["results"];
            if (arr != null && arr.Type == JTokenType.Array)
            {
                foreach (JToken item in (JArray)arr)
                {
                    JObject o = item as JObject;
                    if (o == null) continue;
                    JObject r = new JObject();
                    r["url"] = StringOf(o["url"]);
                    r["raw_content"] = StringOf(o["raw_content"]);
                    if (o["images"] != null && o["images"].Type == JTokenType.Array) r["images"] = o["images"];
                    results.Add(r);
                }
            }
            outp["results"] = results;

            // Surface per-URL failures so the model knows what couldn't be fetched.
            JToken failed = raw["failed_results"];
            if (failed != null && failed.Type == JTokenType.Array && ((JArray)failed).Count > 0)
                outp["failed_results"] = failed;

            return outp;
        }

        /// <summary>Accept a single URL string or an array; always return a JArray of strings.</summary>
        public static JArray NormalizeUrls(JToken urls)
        {
            JArray result = new JArray();
            if (urls == null || urls.Type == JTokenType.Null) return result;

            if (urls.Type == JTokenType.String)
            {
                string s = (string)urls;
                if (!string.IsNullOrEmpty(s)) result.Add(s);
            }
            else if (urls.Type == JTokenType.Array)
            {
                foreach (JToken t in (JArray)urls)
                {
                    if (t == null || t.Type == JTokenType.Null) continue;
                    string s = t.Type == JTokenType.String ? (string)t : t.ToString();
                    if (!string.IsNullOrEmpty(s)) result.Add(s);
                }
            }
            return result;
        }

        // ---- helpers ----

        private static JObject StrEnum(string description, string[] values)
        {
            JObject o = new JObject();
            o["type"] = "string";
            o["description"] = description;
            JArray e = new JArray();
            for (int i = 0; i < values.Length; i++) e.Add(values[i]);
            o["enum"] = e;
            return o;
        }

        private static void CopyStr(ToolCallContext ctx, JObject body, string name)
        {
            string v = ctx.Arguments.Value<string>(name);
            if (!string.IsNullOrEmpty(v)) body[name] = v;
        }

        private static void CopyInt(ToolCallContext ctx, JObject body, string name)
        {
            JToken t = ctx.Arguments[name];
            if (t == null || t.Type == JTokenType.Null) return;
            try { body[name] = t.Value<int>(); }
            catch { }
        }

        private static void CopyBool(ToolCallContext ctx, JObject body, string name)
        {
            JToken t = ctx.Arguments[name];
            if (t == null || t.Type == JTokenType.Null) return;
            try { body[name] = t.Value<bool>(); }
            catch { }
        }

        private static void CopyArray(ToolCallContext ctx, JObject body, string name)
        {
            JToken t = ctx.Arguments[name];
            if (t != null && t.Type == JTokenType.Array && ((JArray)t).Count > 0)
                body[name] = t;
        }

        private static int ClampInt(ToolCallContext ctx, string name, int fallback, int min, int max)
        {
            JToken t = ctx.Arguments[name];
            if (t == null || t.Type == JTokenType.Null) return fallback;
            int n;
            try { n = t.Value<int>(); }
            catch { return fallback; }
            if (n < min) return min;
            if (n > max) return max;
            return n;
        }

        private static string StringOf(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            return t.Type == JTokenType.String ? (string)t : t.ToString();
        }
    }
}
