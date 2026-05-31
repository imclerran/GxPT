using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Mcp35.Core.Protocol
{
    /// <summary>
    /// A content block in a tool result. Phase 1 fully models <c>text</c>; every other type
    /// (image / audio / resource / resource_link) is preserved verbatim in <see cref="Raw"/>
    /// so nothing is lost on round-trip. See mcp35-core-spec.md section 9.
    /// </summary>
    [JsonConverter(typeof(ContentBlockConverter))]
    public sealed class ContentBlock
    {
        public string Type;   // "text" | "image" | "audio" | "resource" | "resource_link" | ...
        public string Text;   // populated when Type == "text"
        public JObject Raw;    // the full block, preserved for non-text types (and text too)

        public bool TryGetText(out string s)
        {
            if (Type == "text" && Text != null)
            {
                s = Text;
                return true;
            }
            s = null;
            return false;
        }

        public static ContentBlock FromText(string text)
        {
            ContentBlock cb = new ContentBlock();
            cb.Type = "text";
            cb.Text = text;
            return cb;
        }
    }

    /// <summary>
    /// Captures the entire block object into <see cref="ContentBlock.Raw"/> on read so unknown
    /// fields (image data, resource bodies) pass through unchanged, while lifting <c>type</c>
    /// and (for text) <c>text</c> into typed fields.
    /// </summary>
    public sealed class ContentBlockConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(ContentBlock);
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null) return null;

            JObject o = JObject.Load(reader);
            ContentBlock cb = new ContentBlock();
            cb.Raw = o;

            JToken type = o["type"];
            cb.Type = (type == null || type.Type == JTokenType.Null) ? null : (string)type;

            if (cb.Type == "text")
            {
                JToken text = o["text"];
                cb.Text = (text == null || text.Type == JTokenType.Null) ? null : (string)text;
            }
            return cb;
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            ContentBlock cb = (ContentBlock)value;
            if (cb == null)
            {
                writer.WriteNull();
                return;
            }

            // Preserve the original block verbatim when we have it.
            if (cb.Raw != null)
            {
                cb.Raw.WriteTo(writer);
                return;
            }

            writer.WriteStartObject();
            writer.WritePropertyName("type");
            writer.WriteValue(cb.Type ?? "text");
            if (cb.Text != null)
            {
                writer.WritePropertyName("text");
                writer.WriteValue(cb.Text);
            }
            writer.WriteEndObject();
        }
    }
}
