using System;
using System.Globalization;
using Newtonsoft.Json;
using Mcp35.Core.Rpc;

namespace Mcp35.Core.Json
{
    /// <summary>
    /// Reads a JSON number into a long-backed <see cref="RequestId"/>, a string into a
    /// string-backed one, and null into the null id; writes each back in its original kind.
    /// </summary>
    public sealed class RequestIdConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(RequestId);
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            RequestId id = (RequestId)value;
            if (id.IsNull) writer.WriteNull();
            else if (id.IsString) writer.WriteValue(id.AsString);
            else writer.WriteValue(id.AsLong);
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            switch (reader.TokenType)
            {
                case JsonToken.Null:
                    return RequestId.Null;
                case JsonToken.Integer:
                    return RequestId.FromLong(Convert.ToInt64(reader.Value, CultureInfo.InvariantCulture));
                case JsonToken.String:
                    return RequestId.FromString((string)reader.Value);
                default:
                    throw new JsonSerializationException("Invalid JSON-RPC id token: " + reader.TokenType);
            }
        }
    }
}
