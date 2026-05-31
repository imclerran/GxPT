using System;
using System.Collections.Generic;
using Mcp35.Core.Protocol;
using Mcp35.Core.Transport;
using Mcp35.Server;
using Newtonsoft.Json.Linq;

namespace SerperMcpServer
{
    /// <summary>
    /// The Serper server's single tool, <c>search</c> (ReadOnly). Web search via
    /// google.serper.dev over curl (net35 can't negotiate TLS 1.2 natively), reusing
    /// Mcp35.Core's CurlRunner. Results condense to organic[] + answer/knowledge boxes.
    /// Every failure mode returns an Error value; the API key never appears in output.
    /// See servers-spec §3.
    /// </summary>
    internal static class SerperTools
    {
        private const string Endpoint = "https://google.serper.dev/search";
        private const int DefaultNum = 10;
        private const int MaxNum = 20;
        private const int RequestTimeoutMs = 20000;

        public static void Register(McpServer server, SerperConfig config)
        {
            server.AddTool("search",
                "Search the web and return condensed results (title, link, snippet) plus answer/knowledge boxes.",
                SchemaBuilder.Object()
                    .Str("query", true, "The search query")
                    .Int("num", false, "Number of results (default 10, max 20)")
                    .Str("gl", false, "Country code, e.g. 'us'")
                    .Str("hl", false, "UI language, e.g. 'en'")
                    .Build(),
                delegate(ToolCallContext ctx) { return Search(config, ctx); });
        }

        private static CallToolResult Search(SerperConfig config, ToolCallContext ctx)
        {
            if (!config.HasKey) return ToolResults.Error("Serper API key not configured.");
            if (!config.HasCurl) return ToolResults.Error("curl path not configured.");

            string query = ctx.Arguments.Value<string>("query");
            if (string.IsNullOrEmpty(query)) return ToolResults.Error("query is required");

            // Build request body.
            JObject body = new JObject();
            body["q"] = query;
            body["num"] = ClampNum(ctx);
            string gl = ctx.Arguments.Value<string>("gl");
            string hl = ctx.Arguments.Value<string>("hl");
            if (!string.IsNullOrEmpty(gl)) body["gl"] = gl;
            if (!string.IsNullOrEmpty(hl)) body["hl"] = hl;

            CurlRequest req = new CurlRequest();
            req.Url = Endpoint;
            req.Method = "POST";
            req.BodyJson = body.ToString(Newtonsoft.Json.Formatting.None);
            req.TimeoutMs = RequestTimeoutMs;
            req.Headers = new Dictionary<string, string>();
            req.Headers["Content-Type"] = "application/json";
            req.Headers["X-API-KEY"] = config.ApiKey; // → -K config file, never the command line

            CurlRunner runner = new CurlRunner(config.CurlPath, config.CaBundle, ctx.Log);

            CurlResult result;
            try
            {
                result = runner.Run(req);
            }
            catch (Exception ex)
            {
                return ToolResults.Error("search request failed: " + ex.Message);
            }

            if (result.HttpStatus != 0 && (result.HttpStatus < 200 || result.HttpStatus >= 300))
                return ToolResults.Error("search failed with HTTP status " + result.HttpStatus);

            if (string.IsNullOrEmpty(result.Body))
                return ToolResults.Error("empty response from search provider");

            JObject parsed;
            try
            {
                parsed = JObject.Parse(result.Body);
            }
            catch (Exception)
            {
                return ToolResults.Error("could not parse search response");
            }

            return ToolResults.Json(Condense(parsed));
        }

        private static int ClampNum(ToolCallContext ctx)
        {
            JToken t = ctx.Arguments["num"];
            if (t == null || t.Type == JTokenType.Null) return DefaultNum;
            int n;
            try { n = t.Value<int>(); }
            catch { return DefaultNum; }
            if (n < 1) return 1;
            if (n > MaxNum) return MaxNum;
            return n;
        }

        /// <summary>
        /// Project the raw Serper JSON into a compact, model-friendly shape: organic results as
        /// {title, link, snippet}, plus answerBox / knowledgeGraph when present. Pure function —
        /// unit-tested directly.
        /// </summary>
        public static JObject Condense(JObject raw)
        {
            JObject outp = new JObject();

            JArray organicOut = new JArray();
            JToken organic = raw["organic"];
            if (organic != null && organic.Type == JTokenType.Array)
            {
                foreach (JToken item in (JArray)organic)
                {
                    JObject o = item as JObject;
                    if (o == null) continue;
                    JObject r = new JObject();
                    r["title"] = StringOf(o["title"]);
                    r["link"] = StringOf(o["link"]);
                    r["snippet"] = StringOf(o["snippet"]);
                    organicOut.Add(r);
                }
            }
            outp["organic"] = organicOut;

            JToken answerBox = raw["answerBox"];
            if (answerBox != null && answerBox.Type == JTokenType.Object)
            {
                JObject ab = (JObject)answerBox;
                JObject abOut = new JObject();
                if (ab["answer"] != null) abOut["answer"] = StringOf(ab["answer"]);
                if (ab["snippet"] != null) abOut["snippet"] = StringOf(ab["snippet"]);
                if (ab["title"] != null) abOut["title"] = StringOf(ab["title"]);
                if (ab["link"] != null) abOut["link"] = StringOf(ab["link"]);
                outp["answerBox"] = abOut;
            }

            JToken kg = raw["knowledgeGraph"];
            if (kg != null && kg.Type == JTokenType.Object)
            {
                JObject k = (JObject)kg;
                JObject kgOut = new JObject();
                if (k["title"] != null) kgOut["title"] = StringOf(k["title"]);
                if (k["type"] != null) kgOut["type"] = StringOf(k["type"]);
                if (k["description"] != null) kgOut["description"] = StringOf(k["description"]);
                outp["knowledgeGraph"] = kgOut;
            }

            return outp;
        }

        private static string StringOf(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            return t.Type == JTokenType.String ? (string)t : t.ToString();
        }
    }
}
