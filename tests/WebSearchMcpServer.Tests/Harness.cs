using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Mcp35.Core.Json;
using Mcp35.Core.Protocol;
using Mcp35.Server;
using Newtonsoft.Json.Linq;

namespace WebSearchMcpServer.Tests
{
    /// <summary>Scripted-stream harness + web-server construction with env injection.</summary>
    internal static class Harness
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);

        public static List<JObject> Exchange(McpServer server, params string[] inputLines)
        {
            byte[] rawIn = Utf8.GetBytes(string.Join("\n", inputLines) + "\n");
            byte[] rawOut;
            using (MemoryStream stdin = new MemoryStream(rawIn))
            using (MemoryStream stdout = new MemoryStream())
            {
                server.Run(stdin, stdout);
                rawOut = stdout.ToArray();
            }

            List<JObject> messages = new List<JObject>();
            foreach (string line in Utf8.GetString(rawOut).Split('\n'))
            {
                if (line.Length == 0) continue;
                messages.Add((JObject)McpJson.Parse(line));
            }
            return messages;
        }

        public static string ToolsList(int id) { return Request(id, "tools/list", null); }

        public static string ToolsCall(int id, string name, JObject arguments)
        {
            JObject p = new JObject();
            p["name"] = name;
            if (arguments != null) p["arguments"] = arguments;
            return Request(id, "tools/call", p);
        }

        public static JObject Args(params object[] kv)
        {
            JObject o = new JObject();
            for (int i = 0; i + 1 < kv.Length; i += 2)
                o[(string)kv[i]] = JToken.FromObject(kv[i + 1]);
            return o;
        }

        public static string Request(int id, string method, JObject prms)
        {
            JObject o = new JObject();
            o["jsonrpc"] = "2.0";
            o["id"] = id;
            o["method"] = method;
            if (prms != null) o["params"] = prms;
            return o.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>Build a web server with the given env (null values clear the variable).</summary>
        public static McpServer NewWebServer(string apiKey, string curlPath)
        {
            Environment.SetEnvironmentVariable("GXPT_WEB_SEARCH_KEY", apiKey);
            Environment.SetEnvironmentVariable("GXPT_CURL_PATH", curlPath);
            Environment.SetEnvironmentVariable("GXPT_CA_BUNDLE", null);

            Implementation info = new Implementation();
            info.Name = "web";
            info.Version = "1.0";
            McpServer server = new McpServer(info, null);
            WebSearchTools.Register(server, WebSearchConfig.FromEnvironment(null));
            return server;
        }

        // ---- result helpers ----

        public static bool IsError(JObject message)
        {
            JObject r = message["result"] as JObject;
            return r != null && r.Value<bool>("isError");
        }

        public static string Text(JObject message)
        {
            return (string)message["result"]["content"][0]["text"];
        }

        public static JObject Structured(JObject message)
        {
            return (JObject)message["result"]["structuredContent"];
        }
    }
}
