using System;
using System.Collections.Generic;
using Mcp35.Core.Protocol;
using Mcp35.Core.Transport;
using Mcp35.Server;
using Newtonsoft.Json.Linq;

namespace WebSearchMcpServer
{
    /// <summary>
    /// The web server's three tools:
    ///   web__search  (ReadOnly)    — query the web (Tavily), condensed results + optional answer.
    ///   web__extract (ReadOnly)    — fetch and return clean page content for given URLs (Tavily).
    ///   web__http    (Destructive) — make a raw HTTP request (any method/headers/body) over curl,
    ///                                returning the status, response headers, and body verbatim.
    /// Request builders and response condensers are pure functions (unit-tested directly); the
    /// handlers add the network call. Failures are Error values; the Tavily API key never appears in
    /// output. See servers-spec §3.
    /// </summary>
    internal static class WebSearchTools
    {
        private const int DefaultMaxResults = 5;
        private const int MaxMaxResults = 20;

        // web__http limits. Body is capped so a huge response can't blow up the model context; the
        // timeout is the per-request curl --max-time (with a hard cap).
        private const int HttpBodyCap = 100000;          // chars returned to the model
        private const int HttpDefaultTimeoutMs = 30000;
        private const int HttpMaxTimeoutMs = 120000;

        // The methods web__http will issue. HEAD is intentionally omitted: `curl -X HEAD` waits for a
        // response body that never arrives and hangs until the timeout; callers wanting headers can use GET.
        private static readonly string[] HttpMethods = new string[] { "GET", "POST", "PUT", "PATCH", "DELETE" };

        public static void Register(McpServer server, WebSearchConfig config)
        {
            TavilyClient client = new TavilyClient(config, null);

            server.AddTool("search",
                "Search the web and return relevant results, each with a title, URL, and content snippet (optionally a synthesized answer). " +
                "Start here when you need current information. The returned URLs can be passed to web__extract to read full page content.",
                BuildSearchSchema(),
                ToolAnnotations.ReadOnly(),
                delegate(ToolCallContext ctx) { return Search(config, client, ctx); });

            server.AddTool("extract",
                "Fetch the full text of specific web pages you ALREADY have URLs for (for example, URLs returned by web__search). " +
                "Requires the 'urls' argument: one or more absolute http(s) URLs. Do not call this without URLs — run web__search first to find them.",
                BuildExtractSchema(),
                ToolAnnotations.ReadOnly(),
                delegate(ToolCallContext ctx) { return Extract(config, client, ctx); });

            server.AddTool("http",
                "Make a raw HTTP request to a URL or API and get back the status code, response headers, and body VERBATIM. " +
                "Use this for REST/JSON APIs or any request that needs a specific method, custom headers, or a request body — " +
                "NOT for reading article or page text (use web__extract for that; it returns clean, readable content instead of raw HTML/JSON). " +
                "Only http(s) URLs are allowed and redirects are not followed automatically (a 3xx is returned to you with its Location header). " +
                "An HTTP error status (4xx/5xx) is reported as data, not a failure.",
                BuildHttpSchema(),
                ToolAnnotations.Destructive(),
                delegate(ToolCallContext ctx) { return Http(config, ctx); });
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

        private static JObject BuildHttpSchema()
        {
            JObject method = StrEnum("HTTP method (default GET).", HttpMethods);

            // A free-form header map: { "Accept": "application/json", ... }. SchemaBuilder has no
            // object-of-strings helper, so build the fragment directly (additionalProperties: string).
            JObject headers = new JObject();
            headers["type"] = "object";
            headers["description"] = "Optional request headers as a flat name->value map, e.g. {\"Accept\": \"application/json\"}.";
            JObject headerVal = new JObject();
            headerVal["type"] = "string";
            headers["additionalProperties"] = headerVal;

            return SchemaBuilder.Object()
                .Str("url", true, "Required. The absolute http(s) URL to request.")
                .Raw("method", method, false)
                .Raw("headers", headers, false)
                .Str("body", false, "Optional request body, sent verbatim (e.g. a JSON string). Set a matching Content-Type header.")
                .Int("timeout_ms", false, "Abort the request after this many milliseconds (default 30000, max 120000).")
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

        // ---- http ----

        private static CallToolResult Http(WebSearchConfig config, ToolCallContext ctx)
        {
            if (!config.HasCurl) return ToolResults.Error("curl path not configured.");

            string url = ctx.Arguments.Value<string>("url");
            string urlError = ValidateHttpUrl(url);
            if (urlError != null) return ToolResults.Error(urlError);

            string method = NormalizeMethod(ctx.Arguments.Value<string>("method"));
            if (method == null) return ToolResults.Error("unsupported method; use one of: " + string.Join(", ", HttpMethods));

            CurlRequest req = BuildHttpRequest(ctx, url, method);
            CurlRunner runner = new CurlRunner(config.CurlPath, config.CaBundle, null);

            CurlResult result;
            try
            {
                result = runner.Run(req);
            }
            catch (Exception ex)
            {
                return ToolResults.Error("request failed: " + ex.Message);
            }

            // No HTTP status AND curl wrote to stderr => a transport-level failure (DNS, TLS, connection
            // refused, timeout). A real HTTP error status (4xx/5xx) carries a status and is returned as
            // data, not an Error — the model often needs to read the error body.
            if (result.HttpStatus == 0 && !string.IsNullOrEmpty(result.Stderr))
                return ToolResults.Error("request failed: " + FirstLine(result.Stderr));

            return ToolResults.Json(CondenseHttp(result));
        }

        /// <summary>Validate that a URL is an absolute http(s) URL. Returns an error string, or null if ok. Pure.</summary>
        public static string ValidateHttpUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return "url is required";
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
                return "url must be an absolute http(s) URL";
            // Uri lowercases the scheme. Anything other than http/https (file://, ftp://, …) is rejected
            // so this tool can't be steered into reading local files or other protocols.
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return "only http and https URLs are allowed";
            return null;
        }

        /// <summary>Uppercase + allowlist the method (default GET). Returns null for an unsupported method. Pure.</summary>
        public static string NormalizeMethod(string method)
        {
            if (string.IsNullOrEmpty(method)) return "GET";
            string up = method.Trim().ToUpperInvariant();
            for (int i = 0; i < HttpMethods.Length; i++)
                if (HttpMethods[i] == up) return up;
            return null;
        }

        /// <summary>Map tool arguments onto a CurlRequest for web__http. Pure (no network).</summary>
        public static CurlRequest BuildHttpRequest(ToolCallContext ctx, string url, string method)
        {
            CurlRequest req = new CurlRequest();
            req.Url = url;
            req.Method = method;
            req.TimeoutMs = ClampInt(ctx, "timeout_ms", HttpDefaultTimeoutMs, 1, HttpMaxTimeoutMs);
            req.Headers = BuildHttpHeaders(ctx.Arguments["headers"]);

            string body = ctx.Arguments.Value<string>("body");
            // BodyJson is just the raw request body (written via --data-binary @file); the name is
            // historical. Only attach it when present so GET stays a bodyless request.
            if (!string.IsNullOrEmpty(body)) req.BodyJson = body;
            return req;
        }

        /// <summary>Turn the optional 'headers' object argument into a name->value map. Pure.</summary>
        public static IDictionary<string, string> BuildHttpHeaders(JToken headers)
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            JObject obj = headers as JObject;
            if (obj == null) return map;
            foreach (KeyValuePair<string, JToken> kv in obj)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                if (kv.Value == null || kv.Value.Type == JTokenType.Null) continue;
                string v = kv.Value.Type == JTokenType.String ? (string)kv.Value : kv.Value.ToString();
                map[kv.Key] = v;
            }
            return map;
        }

        /// <summary>Condense a CurlResult into {status, headers?, body, truncated?}. Pure.</summary>
        public static JObject CondenseHttp(CurlResult result)
        {
            JObject outp = new JObject();
            outp["status"] = result.HttpStatus;

            if (result.Headers != null && result.Headers.Count > 0)
            {
                JObject h = new JObject();
                foreach (KeyValuePair<string, string> kv in result.Headers)
                    h[kv.Key] = kv.Value;
                outp["headers"] = h;
            }

            bool truncated;
            outp["body"] = Cap(result.Body, HttpBodyCap, out truncated);
            if (truncated) outp["truncated"] = true;
            return outp;
        }

        private static string Cap(string s, int max, out bool truncated)
        {
            truncated = false;
            if (s == null) return string.Empty;
            if (s.Length <= max) return s;
            truncated = true;
            return s.Substring(0, max);
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            string t = s.Replace("\r\n", "\n").Trim();
            int nl = t.IndexOf('\n');
            return nl < 0 ? t : t.Substring(0, nl);
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
