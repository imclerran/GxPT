using System.Collections.Generic;
using System.IO;
using System.Text;
using Mcp35.Core.Json;
using Newtonsoft.Json.Linq;

namespace Mcp35.Server.Tests
{
    /// <summary>
    /// Drives <see cref="McpServer.Run(Stream,Stream)"/> with a scripted set of input lines and
    /// captures the framed stdout. Because the runtime is serial and stdin EOF ends the loop,
    /// we can feed all input up front and read everything written.
    /// </summary>
    internal static class ServerHarness
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);

        /// <summary>Run the server over the given newline-delimited input lines; return parsed stdout messages.</summary>
        public static List<JObject> Exchange(McpServer server, params string[] inputLines)
        {
            byte[] rawOut;
            byte[] rawIn = Utf8.GetBytes(string.Join("\n", inputLines) + "\n");

            using (MemoryStream stdin = new MemoryStream(rawIn))
            using (MemoryStream stdout = new MemoryStream())
            {
                server.Run(stdin, stdout);
                rawOut = stdout.ToArray();
            }

            return ParseFramed(rawOut);
        }

        /// <summary>Run the server, returning both the parsed messages and the raw stdout bytes (for framing checks).</summary>
        public static List<JObject> Exchange(McpServer server, out byte[] rawStdout, params string[] inputLines)
        {
            byte[] rawIn = Utf8.GetBytes(string.Join("\n", inputLines) + "\n");
            using (MemoryStream stdin = new MemoryStream(rawIn))
            using (MemoryStream stdout = new MemoryStream())
            {
                server.Run(stdin, stdout);
                rawStdout = stdout.ToArray();
            }
            return ParseFramed(rawStdout);
        }

        public static List<JObject> ParseFramed(byte[] rawOut)
        {
            string text = Utf8.GetString(rawOut);
            List<JObject> messages = new List<JObject>();
            string[] lines = text.Split('\n');
            foreach (string line in lines)
            {
                if (line.Length == 0) continue;
                messages.Add((JObject)McpJson.Parse(line));
            }
            return messages;
        }

        // --- request builders ---

        public static string Initialize(int id, string protocolVersion)
        {
            JObject p = new JObject();
            p["protocolVersion"] = protocolVersion;
            p["capabilities"] = new JObject();
            return Request(id, "initialize", p);
        }

        public static string InitializedNotification()
        {
            JObject o = new JObject();
            o["jsonrpc"] = "2.0";
            o["method"] = "notifications/initialized";
            return o.ToString(Newtonsoft.Json.Formatting.None);
        }

        public static string ToolsList(int id)
        {
            return Request(id, "tools/list", null);
        }

        public static string ToolsCall(int id, string name, JObject arguments)
        {
            JObject p = new JObject();
            p["name"] = name;
            if (arguments != null) p["arguments"] = arguments;
            return Request(id, "tools/call", p);
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
    }
}
