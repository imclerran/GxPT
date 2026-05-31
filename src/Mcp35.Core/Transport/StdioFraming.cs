using System.IO;
using System.Text;

namespace Mcp35.Core.Transport
{
    /// <summary>
    /// MCP stdio framing: newline-delimited UTF-8 JSON (messages contain no embedded newlines).
    /// This is NOT LSP Content-Length framing. See mcp35-core-spec.md §8.
    /// </summary>
    public static class StdioFraming
    {
        // No BOM, no exception on invalid bytes — matches the decode used elsewhere in GxPT.
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);

        /// <summary>Write one message as UTF-8 bytes followed by '\n', then flush.</summary>
        public static void WriteMessage(Stream stdin, string json)
        {
            byte[] bytes = Utf8.GetBytes(json);
            stdin.Write(bytes, 0, bytes.Length);
            stdin.WriteByte((byte)'\n');
            stdin.Flush();
        }

        /// <summary>Read one message (one line); returns null at end of stream.</summary>
        public static string ReadMessage(TextReader stdout)
        {
            return stdout.ReadLine();
        }

        /// <summary>A reader configured for stdio: UTF-8, no BOM swallowing surprises.</summary>
        public static StreamReader CreateReader(Stream stdout)
        {
            return new StreamReader(stdout, Utf8);
        }
    }
}
