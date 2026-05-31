using System.Collections.Generic;
using Mcp35.Core.Json;
using Mcp35.Core.Rpc;
using Newtonsoft.Json;
using Xunit;

namespace Mcp35.Core.Tests
{
    /// <summary>
    /// RequestId equality/hashing back the pending-request table, and the converter must
    /// round-trip each id kind (number / string / null) exactly.
    /// </summary>
    public class RequestIdTests
    {
        [Fact]
        public void Long_ids_equate_and_hash_together()
        {
            Assert.Equal(RequestId.FromLong(42), RequestId.FromLong(42));
            Assert.Equal(RequestId.FromLong(42).GetHashCode(), RequestId.FromLong(42).GetHashCode());
            Assert.NotEqual(RequestId.FromLong(42), RequestId.FromLong(43));
        }

        [Fact]
        public void String_and_long_ids_are_distinct()
        {
            Assert.NotEqual(RequestId.FromLong(1), RequestId.FromString("1"));
        }

        [Fact]
        public void Default_and_Null_are_both_null()
        {
            RequestId def = default(RequestId);
            Assert.True(def.IsNull);
            Assert.Equal(RequestId.Null, def);
        }

        [Fact]
        public void Works_as_dictionary_key()
        {
            var d = new Dictionary<RequestId, string>();
            d[RequestId.FromLong(1)] = "one";
            d[RequestId.FromString("a")] = "alpha";

            Assert.Equal("one", d[RequestId.FromLong(1)]);
            Assert.Equal("alpha", d[RequestId.FromString("a")]);
            Assert.False(d.ContainsKey(RequestId.FromLong(2)));
        }

        [Theory]
        [InlineData("123", true, 123)]
        public void Converter_reads_number(string json, bool isLong, long value)
        {
            var id = JsonConvert.DeserializeObject<RequestId>(json, McpJson.Settings);
            Assert.Equal(isLong, id.IsLong);
            Assert.Equal(value, id.AsLong);
        }

        [Fact]
        public void Converter_reads_string()
        {
            var id = JsonConvert.DeserializeObject<RequestId>("\"abc\"", McpJson.Settings);
            Assert.True(id.IsString);
            Assert.Equal("abc", id.AsString);
        }

        [Fact]
        public void Converter_reads_null()
        {
            var id = JsonConvert.DeserializeObject<RequestId>("null", McpJson.Settings);
            Assert.True(id.IsNull);
        }

        [Fact]
        public void Converter_writes_each_kind_in_its_original_form()
        {
            Assert.Equal("5", JsonConvert.SerializeObject(RequestId.FromLong(5), McpJson.Settings));
            Assert.Equal("\"x\"", JsonConvert.SerializeObject(RequestId.FromString("x"), McpJson.Settings));
            Assert.Equal("null", JsonConvert.SerializeObject(RequestId.Null, McpJson.Settings));
        }
    }
}
