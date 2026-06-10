using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Mcp35.Core.Protocol;
using Mcp35.Core.Transport;
using Mcp35.Server;
using Newtonsoft.Json.Linq;

namespace WebSearchMcpServer
{
    /// <summary>
    /// The web server's four tools:
    ///   web__search  (ReadOnly)    — query the web (Tavily), condensed results + optional answer.
    ///   web__extract (ReadOnly)    — fetch and return clean page content for given URLs (Tavily).
    ///   web__get     (ReadOnly)    — HTTP GET a PUBLIC URL over curl; status, response headers, body verbatim.
    ///   web__http    (Destructive) — state-changing HTTP request (POST/PUT/PATCH/DELETE) over curl.
    /// The two raw-HTTP tools are split by side effect so GET can be auto-allowed (ReadOnly, like
    /// extract) while the mutating verbs stay behind the approval gate. Because web__get is auto-allowed
    /// and (unlike Tavily-proxied extract) runs curl DIRECTLY from this machine, it carries an SSRF
    /// guard: it refuses non-public hosts (loopback/private/link-local, incl. DNS-resolved), so it can
    /// only reach the public internet. web__http has no such restriction — it's gated, so the user sees
    /// and approves the exact URL (which is how localhost/dev targets are reached). Request builders and
    /// response condensers are pure functions (unit-tested directly); the handlers add the network call.
    /// Failures are Error values; the Tavily API key never appears in output. See servers-spec §3.
    /// </summary>
    internal static class WebSearchTools
    {
        private const int DefaultMaxResults = 5;
        private const int MaxMaxResults = 20;

        // web__get / web__http limits. Body is capped so a huge response can't blow up the model
        // context; the timeout is the per-request curl --max-time (with a hard cap).
        private const int HttpBodyCap = 100000;          // chars returned to the model
        private const int HttpDefaultTimeoutMs = 30000;
        private const int HttpMaxTimeoutMs = 120000;

        // The state-changing methods web__http will issue. GET is excluded (it's the ReadOnly web__get
        // tool); HEAD is excluded too (`curl -X HEAD` waits for a body that never arrives and hangs).
        private static readonly string[] MutatingMethods = new string[] { "POST", "PUT", "PATCH", "DELETE" };

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

            server.AddTool("get",
                "HTTP GET a URL and return the status code, response headers, and body VERBATIM. " +
                "Use this to read JSON/REST API responses or any raw GET resource. " +
                "For turning an article or web page into clean, readable text use web__extract instead; " +
                "to SEND data (POST/PUT/PATCH/DELETE) use web__http. " +
                "Only http(s) URLs are allowed and redirects are not followed automatically (a 3xx is returned with its Location header). " +
                "Only PUBLIC hosts are reachable — to request localhost or an internal/LAN address use web__http instead. " +
                "An HTTP error status (4xx/5xx) is reported as data, not a failure.",
                BuildGetSchema(),
                ToolAnnotations.ReadOnly(),
                delegate(ToolCallContext ctx) { return Get(config, ctx); });

            server.AddTool("http",
                "Send a STATE-CHANGING HTTP request (POST, PUT, PATCH, or DELETE) with an optional body and custom headers; " +
                "returns the status code, response headers, and body VERBATIM. " +
                "For a plain GET use web__get; for reading article/page text use web__extract. " +
                "Only http(s) URLs are allowed and redirects are not followed automatically (a 3xx is returned with its Location header). " +
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

        private static JObject BuildGetSchema()
        {
            return SchemaBuilder.Object()
                .Str("url", true, "Required. The absolute http(s) URL to GET.")
                .Raw("headers", BuildHeadersSchema(), false)
                .Int("timeout_ms", false, "Abort the request after this many milliseconds (default 30000, max 120000).")
                .Build();
        }

        private static JObject BuildHttpSchema()
        {
            JObject method = StrEnum("Request method (default POST). For a GET request use web__get instead.", MutatingMethods);

            return SchemaBuilder.Object()
                .Str("url", true, "Required. The absolute http(s) URL to request.")
                .Raw("method", method, false)
                .Raw("headers", BuildHeadersSchema(), false)
                .Str("body", false, "Optional request body, sent verbatim (e.g. a JSON string). Set a matching Content-Type header.")
                .Int("timeout_ms", false, "Abort the request after this many milliseconds (default 30000, max 120000).")
                .Build();
        }

        /// <summary>A free-form header map schema: { "Accept": "application/json", ... }. SchemaBuilder
        /// has no object-of-strings helper, so build the fragment directly (additionalProperties: string).</summary>
        private static JObject BuildHeadersSchema()
        {
            JObject headers = new JObject();
            headers["type"] = "object";
            headers["description"] = "Optional request headers as a flat name->value map, e.g. {\"Accept\": \"application/json\"}.";
            JObject headerVal = new JObject();
            headerVal["type"] = "string";
            headers["additionalProperties"] = headerVal;
            return headers;
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

        // ---- http (web__get / web__http) ----

        private static CallToolResult Get(WebSearchConfig config, ToolCallContext ctx)
        {
            // GET only (never mutates -> ReadOnly) and, because it's auto-allowed, public hosts only.
            return RunHttp(config, ctx, "GET", true);
        }

        private static CallToolResult Http(WebSearchConfig config, ToolCallContext ctx)
        {
            string method = NormalizeMutatingMethod(ctx.Arguments.Value<string>("method"));
            if (method == null)
                return ToolResults.Error("web__http handles state-changing requests (POST, PUT, PATCH, DELETE); for a GET request use web__get instead.");
            // No public-only restriction: web__http is gated, so the user approves the exact URL
            // (this is how localhost / internal dev targets are reached).
            return RunHttp(config, ctx, method, false);
        }

        /// <summary>Shared core for web__get / web__http: validate, build, run, condense.</summary>
        private static CallToolResult RunHttp(WebSearchConfig config, ToolCallContext ctx, string method, bool publicOnly)
        {
            if (!config.HasCurl) return ToolResults.Error("curl path not configured.");

            string url = ctx.Arguments.Value<string>("url");
            string urlError = ValidateHttpUrl(url);
            if (urlError != null) return ToolResults.Error(urlError);

            if (publicOnly)
            {
                string hostError = ValidatePublicHost(url);
                if (hostError != null) return ToolResults.Error(hostError);
            }

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
            // so these tools can't be steered into reading local files or other protocols.
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return "only http and https URLs are allowed";
            return null;
        }

        /// <summary>Uppercase + allowlist a state-changing method (default POST). Returns null for GET,
        /// HEAD, or any unsupported method (web__http rejects those — GET belongs to web__get). Pure.</summary>
        public static string NormalizeMutatingMethod(string method)
        {
            if (string.IsNullOrEmpty(method)) return "POST";
            string up = method.Trim().ToUpperInvariant();
            for (int i = 0; i < MutatingMethods.Length; i++)
                if (MutatingMethods[i] == up) return up;
            return null;
        }

        /// <summary>
        /// SSRF guard for the auto-allowed web__get: reject a URL whose host is not a public address —
        /// loopback, private (RFC1918), carrier-grade NAT, link-local (incl. the 169.254.169.254 cloud
        /// metadata endpoint), or a hostname that RESOLVES to one of those. Returns an error string, or
        /// null if the host is public. Best-effort: DNS can change between this check and curl's own
        /// resolution (rebinding/TOCTOU), but it closes the obvious localhost/LAN/metadata vectors, and
        /// redirects aren't followed so a 3xx can't bounce into the internal network.
        /// </summary>
        public static string ValidatePublicHost(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return "url must be an absolute http(s) URL";
            string host = uri.Host;
            if (string.IsNullOrEmpty(host)) return "url has no host";

            IPAddress[] addrs;
            IPAddress literal;
            if (IPAddress.TryParse(host, out literal))
            {
                addrs = new IPAddress[] { literal };
            }
            else
            {
                try { addrs = Dns.GetHostAddresses(host); }
                catch { return "could not resolve host: " + host; }
                if (addrs == null || addrs.Length == 0) return "could not resolve host: " + host;
            }

            for (int i = 0; i < addrs.Length; i++)
            {
                if (!IsPublicIp(addrs[i]))
                    return "refusing to request a non-public address (" + addrs[i] +
                           "); web__get only reaches public hosts — use web__http for localhost or internal/LAN targets";
            }
            return null;
        }

        /// <summary>True only for a routable public IP. Blocks loopback/private/link-local/reserved
        /// (v4 and v6), unwrapping IPv4-mapped IPv6 to check the embedded address. Pure.</summary>
        public static bool IsPublicIp(IPAddress ip)
        {
            if (ip == null) return false;
            if (IPAddress.IsLoopback(ip)) return false; // 127.0.0.0/8 and ::1

            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] b = ip.GetAddressBytes(); // 4 bytes, network order
                if (b[0] == 0) return false;                                  // 0.0.0.0/8 ("this network")
                if (b[0] == 10) return false;                                 // 10.0.0.0/8
                if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;   // 100.64.0.0/10 (CGNAT)
                if (b[0] == 127) return false;                                // 127.0.0.0/8 (loopback)
                if (b[0] == 169 && b[1] == 254) return false;                 // 169.254.0.0/16 (link-local + metadata)
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;    // 172.16.0.0/12
                if (b[0] == 192 && b[1] == 168) return false;                 // 192.168.0.0/16
                if (b[0] >= 224) return false;                                // 224.0.0.0/4 multicast + 240.0.0.0/4 reserved
                return true;
            }

            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return false;   // fe80::/10, fec0::/10
                byte[] b = ip.GetAddressBytes(); // 16 bytes
                bool allZero = true;
                for (int i = 0; i < b.Length; i++) if (b[i] != 0) { allZero = false; break; }
                if (allZero) return false;                                    // :: (unspecified)
                if ((b[0] & 0xFE) == 0xFC) return false;                      // fc00::/7 (unique local)
                if (IsV4Mapped(b))                                            // ::ffff:a.b.c.d -> check embedded v4
                    return IsPublicIp(new IPAddress(new byte[] { b[12], b[13], b[14], b[15] }));
                return true;
            }

            return false; // unknown address family -> treat as non-public
        }

        // ::ffff:0:0/96 — an IPv4 address mapped into IPv6.
        private static bool IsV4Mapped(byte[] b)
        {
            for (int i = 0; i < 10; i++) if (b[i] != 0) return false;
            return b[10] == 0xFF && b[11] == 0xFF;
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
