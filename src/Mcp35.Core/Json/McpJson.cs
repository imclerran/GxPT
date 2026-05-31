using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Mcp35.Core.Json
{
    /// <summary>
    /// One shared serializer configuration so every layer (Rpc, Protocol, the host)
    /// serializes identically. See mcp35-core-spec.md section 2.
    /// </summary>
    public static class McpJson
    {
        /// <summary>Explicit nesting cap; deeply nested JSON-Schema payloads are bounded, not unbounded.</summary>
        public const int MaxDepth = 128;

        public static readonly JsonSerializerSettings Settings = CreateSettings();

        private static JsonSerializerSettings CreateSettings()
        {
            JsonSerializerSettings s = new JsonSerializerSettings();
            s.NullValueHandling = NullValueHandling.Ignore;        // absent fields are omitted, not null
            s.MissingMemberHandling = MissingMemberHandling.Ignore; // tolerate additive protocol revisions
            s.DateParseHandling = DateParseHandling.None;          // keep ISO date strings as strings (lossless)
            s.FloatParseHandling = FloatParseHandling.Double;
            s.MaxDepth = MaxDepth;
            s.Formatting = Formatting.None;
            s.Converters.Add(new RequestIdConverter());
            return s;
        }

        public static string Serialize(object value)
        {
            return JsonConvert.SerializeObject(value, Settings);
        }

        public static T Deserialize<T>(string json)
        {
            return JsonConvert.DeserializeObject<T>(json, Settings);
        }

        /// <summary>
        /// Parse an arbitrary JSON document into a JToken for the open-ended boundary.
        /// Uses an explicitly configured reader so date-like strings are NOT coerced to
        /// DateTime (JToken.Parse would otherwise apply DateParseHandling.DateTime).
        /// </summary>
        public static JToken Parse(string json)
        {
            using (StringReader sr = new StringReader(json))
            using (JsonTextReader reader = new JsonTextReader(sr))
            {
                reader.DateParseHandling = DateParseHandling.None;
                reader.FloatParseHandling = FloatParseHandling.Double;
                reader.MaxDepth = MaxDepth;
                return JToken.ReadFrom(reader);
            }
        }

        /// <summary>Parse into a JObject; throws if the document is not a JSON object.</summary>
        public static JObject ParseObject(string json)
        {
            return (JObject)Parse(json);
        }
    }
}
