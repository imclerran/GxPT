using System;
using System.IO;
using Mcp35.Core.Diagnostics;

namespace SerperMcpServer
{
    /// <summary>
    /// Startup config from the environment (servers-spec §1), read once. The API key and curl
    /// path are required; when absent the search tool degrades to a clear Error rather than the
    /// server crashing. Secrets are never logged.
    /// </summary>
    internal sealed class SerperConfig
    {
        public readonly string ApiKey;     // GXPT_SERPER_KEY (required)
        public readonly string CurlPath;   // GXPT_CURL_PATH (required; net35 can't TLS 1.2)
        public readonly string CaBundle;   // GXPT_CA_BUNDLE (optional)
        public readonly string WorkDir;    // GXPT_WORKDIR (unused by Serper but part of the contract)

        private SerperConfig(string apiKey, string curlPath, string caBundle, string workDir)
        {
            ApiKey = apiKey;
            CurlPath = curlPath;
            CaBundle = caBundle;
            WorkDir = workDir;
        }

        public bool HasKey { get { return !string.IsNullOrEmpty(ApiKey); } }
        public bool HasCurl { get { return !string.IsNullOrEmpty(CurlPath); } }

        public static SerperConfig FromEnvironment(ILogSink log)
        {
            string apiKey = Environment.GetEnvironmentVariable("GXPT_SERPER_KEY");
            string curlPath = Environment.GetEnvironmentVariable("GXPT_CURL_PATH");
            string caBundle = Environment.GetEnvironmentVariable("GXPT_CA_BUNDLE");
            string workDir = Environment.GetEnvironmentVariable("GXPT_WORKDIR");
            if (string.IsNullOrEmpty(workDir)) workDir = Directory.GetCurrentDirectory();

            if (log != null)
            {
                // Never log the key itself — only whether it is present.
                log.Log("serper", "key=" + (string.IsNullOrEmpty(apiKey) ? "absent" : "present")
                    + " curl=" + (string.IsNullOrEmpty(curlPath) ? "absent" : "present"));
            }
            return new SerperConfig(apiKey, curlPath, caBundle, workDir);
        }
    }
}
