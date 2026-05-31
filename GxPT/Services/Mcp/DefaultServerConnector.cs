using System.Collections.Generic;
using System.Text;
using Mcp35.Client;
using Mcp35.Client.Transport;
using Mcp35.Core.Diagnostics;
using Mcp35.Core.Protocol;
using Mcp35.Core.Rpc;
using Mcp35.Core.Transport;

namespace GxPT
{
    // The live IServerConnector: turns a spec into a real transport (stdio child process or HTTP)
    // and an McpServerConnection. GXPT_WORKDIR is injected (and the process working directory set)
    // for workdir-scoped stdio servers. HTTP servers (GitHub) run over Core's CurlRunner so net35
    // can reach modern TLS 1.2 endpoints.
    internal sealed class DefaultServerConnector : IServerConnector
    {
        private readonly Implementation _clientInfo;
        private readonly string _curlPath;
        private readonly string _caBundlePath;
        private readonly ILogSink _log;

        public DefaultServerConnector(Implementation clientInfo, string curlPath, string caBundlePath, ILogSink log)
        {
            _clientInfo = clientInfo;
            _curlPath = curlPath;
            _caBundlePath = caBundlePath;
            _log = log != null ? log : NullLogSink.Instance;
        }

        public McpServerConnection Create(McpServerSpec spec, string workdir)
        {
            if (spec == null) return null;

            IRpcTransport transport;
            if (spec.Kind == McpTransportKind.Http)
            {
                ICurlRunner curl = new CurlRunner(_curlPath, _caBundlePath, _log);
                transport = new HttpTransport(spec.Url, spec.Headers, curl, _log);
            }
            else
            {
                StdioLaunch launch = new StdioLaunch();
                launch.Command = spec.Command;
                launch.Arguments = JoinArgs(spec.Args);

                Dictionary<string, string> env = new Dictionary<string, string>(spec.Env);
                if (spec.WorkdirScoped && !string.IsNullOrEmpty(workdir))
                {
                    env[McpConfig.EnvWorkdir] = workdir;
                    launch.WorkingDirectory = workdir;
                }
                launch.Environment = env;

                transport = new StdioTransport(launch, _log);
            }

            return new McpServerConnection(spec.Name, transport, _clientInfo, _log);
        }

        // Build a single command-line argument string, quoting tokens that contain spaces/quotes.
        private static string JoinArgs(string[] args)
        {
            if (args == null || args.Length == 0) return string.Empty;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                string a = args[i] != null ? args[i] : string.Empty;
                if (a.IndexOf(' ') >= 0 || a.IndexOf('"') >= 0)
                    sb.Append('"').Append(a.Replace("\"", "\\\"")).Append('"');
                else
                    sb.Append(a);
            }
            return sb.ToString();
        }
    }
}
