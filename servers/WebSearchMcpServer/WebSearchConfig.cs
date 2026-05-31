using System;
using System.IO;
using Mcp35.Core.Diagnostics;

namespace WebSearchMcpServer
{
    /// <summary>
    /// Startup config from the environment (servers-spec §1), read once. The Tavily API key and a
    /// curl path are required (net35 can't negotiate TLS 1.2 natively); when absent the tools
    /// degrade to a clear Error rather than the server crashing. The key is never logged.
    /// </summary>
    internal sealed class WebSearchConfig
    {
        public readonly string ApiKey;     // GXPT_WEB_SEARCH_KEY (required) — Tavily bearer token
        public readonly string CurlPath;   // GXPT_CURL_PATH (required)
        public readonly string CaBundle;   // GXPT_CA_BUNDLE (optional)
        public readonly string WorkDir;    // GXPT_WORKDIR (unused here, part of the contract)

        private WebSearchConfig(string apiKey, string curlPath, string caBundle, string workDir)
        {
            ApiKey = apiKey;
            CurlPath = curlPath;
            CaBundle = caBundle;
            WorkDir = workDir;
        }

        public bool HasKey { get { return !string.IsNullOrEmpty(ApiKey); } }
        public bool HasCurl { get { return !string.IsNullOrEmpty(CurlPath); } }

        public static WebSearchConfig FromEnvironment(ILogSink log)
        {
            string apiKey = Environment.GetEnvironmentVariable("GXPT_WEB_SEARCH_KEY");
            string curlPath = Environment.GetEnvironmentVariable("GXPT_CURL_PATH");
            string caBundle = Environment.GetEnvironmentVariable("GXPT_CA_BUNDLE");
            string workDir = Environment.GetEnvironmentVariable("GXPT_WORKDIR");
            if (string.IsNullOrEmpty(workDir)) workDir = Directory.GetCurrentDirectory();

            if (log != null)
            {
                // Never log the key — only whether it's present.
                log.Log("web", "key=" + (string.IsNullOrEmpty(apiKey) ? "absent" : "present")
                    + " curl=" + (string.IsNullOrEmpty(curlPath) ? "absent" : "present"));
            }
            return new WebSearchConfig(apiKey, curlPath, caBundle, workDir);
        }
    }
}
