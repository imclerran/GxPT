using System;
using System.Collections.Generic;
using Mcp35.Core.Diagnostics;
using Mcp35.Core.Transport;
using Newtonsoft.Json.Linq;

namespace WebSearchMcpServer
{
    /// <summary>The outcome of a Tavily call: either a parsed JSON body or an error message.</summary>
    internal sealed class TavilyResult
    {
        public readonly bool Ok;
        public readonly JObject Body;       // when Ok
        public readonly string ErrorMessage; // when !Ok

        private TavilyResult(bool ok, JObject body, string error)
        {
            Ok = ok;
            Body = body;
            ErrorMessage = error;
        }

        public static TavilyResult Success(JObject body) { return new TavilyResult(true, body, null); }
        public static TavilyResult Failure(string message) { return new TavilyResult(false, null, message); }
    }

    /// <summary>
    /// Thin wrapper over the Tavily REST API (search + extract) via Mcp35.Core's CurlRunner.
    /// The bearer token goes into curl's -K config file (never the command line). Every transport
    /// / HTTP / parse failure is returned as a TavilyResult.Failure — this class never throws and
    /// never echoes the token. Endpoints are overridable so tests can point at a fake curl.
    /// </summary>
    internal sealed class TavilyClient
    {
        public const string SearchEndpoint = "https://api.tavily.com/search";
        public const string ExtractEndpoint = "https://api.tavily.com/extract";
        private const int RequestTimeoutMs = 30000;

        private readonly WebSearchConfig _config;
        private readonly ILogSink _log;

        public TavilyClient(WebSearchConfig config, ILogSink log)
        {
            _config = config;
            _log = log ?? NullLogSink.Instance;
        }

        public TavilyResult Search(JObject requestBody) { return Post(SearchEndpoint, requestBody); }
        public TavilyResult Extract(JObject requestBody) { return Post(ExtractEndpoint, requestBody); }

        private TavilyResult Post(string url, JObject requestBody)
        {
            if (!_config.HasKey) return TavilyResult.Failure("Web search API key not configured.");
            if (!_config.HasCurl) return TavilyResult.Failure("curl path not configured.");

            CurlRequest req = new CurlRequest();
            req.Url = url;
            req.Method = "POST";
            req.BodyJson = requestBody.ToString(Newtonsoft.Json.Formatting.None);
            req.TimeoutMs = RequestTimeoutMs;
            req.Headers = new Dictionary<string, string>();
            req.Headers["Content-Type"] = "application/json";
            req.Headers["Authorization"] = "Bearer " + _config.ApiKey; // → -K config file, off the cmd line

            CurlRunner runner = new CurlRunner(_config.CurlPath, _config.CaBundle, _log);

            CurlResult result;
            try
            {
                result = runner.Run(req);
            }
            catch (Exception ex)
            {
                return TavilyResult.Failure("request failed: " + ex.Message);
            }

            if (result.HttpStatus != 0 && (result.HttpStatus < 200 || result.HttpStatus >= 300))
                return TavilyResult.Failure("request failed with HTTP status " + result.HttpStatus);

            if (string.IsNullOrEmpty(result.Body))
                return TavilyResult.Failure("empty response from web search provider");

            JObject parsed;
            try
            {
                parsed = JObject.Parse(result.Body);
            }
            catch (Exception)
            {
                return TavilyResult.Failure("could not parse web search response");
            }

            return TavilyResult.Success(parsed);
        }
    }
}
