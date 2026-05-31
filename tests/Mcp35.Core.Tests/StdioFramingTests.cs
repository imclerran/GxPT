using System.IO;
using System.Text;
using Mcp35.Core.Transport;
using Xunit;

namespace Mcp35.Core.Tests
{
    public class StdioFramingTests
    {
        [Fact]
        public void Write_then_read_round_trips_a_message()
        {
            const string msg = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}";

            var ms = new MemoryStream();
            StdioFraming.WriteMessage(ms, msg);

            ms.Position = 0;
            using (var reader = new StreamReader(ms, new UTF8Encoding(false, false)))
            {
                Assert.Equal(msg, StdioFraming.ReadMessage(reader));
                Assert.Null(StdioFraming.ReadMessage(reader)); // EOF
            }
        }

        [Fact]
        public void Write_appends_exactly_one_newline_and_utf8_bytes()
        {
            const string msg = "{\"s\":\"café\"}"; // multi-byte char

            var ms = new MemoryStream();
            StdioFraming.WriteMessage(ms, msg);
            byte[] bytes = ms.ToArray();

            byte[] expected = new UTF8Encoding(false, false).GetBytes(msg);
            Assert.Equal((byte)'\n', bytes[bytes.Length - 1]);
            Assert.Equal(expected.Length + 1, bytes.Length);
        }

        [Fact]
        public void Multiple_messages_read_back_in_order()
        {
            var ms = new MemoryStream();
            StdioFraming.WriteMessage(ms, "{\"a\":1}");
            StdioFraming.WriteMessage(ms, "{\"b\":2}");

            ms.Position = 0;
            using (var reader = new StreamReader(ms, new UTF8Encoding(false, false)))
            {
                Assert.Equal("{\"a\":1}", StdioFraming.ReadMessage(reader));
                Assert.Equal("{\"b\":2}", StdioFraming.ReadMessage(reader));
                Assert.Null(StdioFraming.ReadMessage(reader));
            }
        }
    }
}
