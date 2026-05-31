using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Mcp35.Server
{
    /// <summary>
    /// A minimal fluent builder for the common case — an object schema with typed properties and
    /// a required list. Anything more complex can be passed as a raw <see cref="JObject"/> / JSON
    /// string to <see cref="McpServer.AddTool"/>; this is convenience, not a full JSON-Schema DSL.
    ///
    /// Uses explicit overloads rather than C# 4 optional parameters so the SDK compiles under the
    /// C# 3 (VS2008) toolchain, matching the rest of the net35 codebase.
    /// </summary>
    public sealed class SchemaBuilder
    {
        private readonly JObject _props = new JObject();
        private readonly List<string> _required = new List<string>();

        public static SchemaBuilder Object()
        {
            return new SchemaBuilder();
        }

        // ---- string ----
        public SchemaBuilder Str(string name) { return Prop(name, "string", false, null); }
        public SchemaBuilder Str(string name, bool required) { return Prop(name, "string", required, null); }
        public SchemaBuilder Str(string name, bool required, string description) { return Prop(name, "string", required, description); }

        // ---- integer ----
        public SchemaBuilder Int(string name) { return Prop(name, "integer", false, null); }
        public SchemaBuilder Int(string name, bool required) { return Prop(name, "integer", required, null); }
        public SchemaBuilder Int(string name, bool required, string description) { return Prop(name, "integer", required, description); }

        // ---- number ----
        public SchemaBuilder Num(string name) { return Prop(name, "number", false, null); }
        public SchemaBuilder Num(string name, bool required) { return Prop(name, "number", required, null); }
        public SchemaBuilder Num(string name, bool required, string description) { return Prop(name, "number", required, description); }

        // ---- boolean ----
        public SchemaBuilder Bool(string name) { return Prop(name, "boolean", false, null); }
        public SchemaBuilder Bool(string name, bool required) { return Prop(name, "boolean", required, null); }
        public SchemaBuilder Bool(string name, bool required, string description) { return Prop(name, "boolean", required, description); }

        // ---- array of a primitive item type ----
        public SchemaBuilder Arr(string name, string itemType) { return Arr(name, itemType, false, null); }
        public SchemaBuilder Arr(string name, string itemType, bool required) { return Arr(name, itemType, required, null); }
        public SchemaBuilder Arr(string name, string itemType, bool required, string description)
        {
            JObject p = new JObject();
            p["type"] = "array";
            JObject items = new JObject();
            items["type"] = itemType;
            p["items"] = items;
            if (description != null) p["description"] = description;
            _props[name] = p;
            if (required) _required.Add(name);
            return this;
        }

        // ---- raw schema fragment (nested objects, enums, …) ----
        public SchemaBuilder Raw(string name, JObject schema) { return Raw(name, schema, false); }
        public SchemaBuilder Raw(string name, JObject schema, bool required)
        {
            _props[name] = schema;
            if (required) _required.Add(name);
            return this;
        }

        private SchemaBuilder Prop(string name, string type, bool required, string description)
        {
            JObject p = new JObject();
            p["type"] = type;
            if (description != null) p["description"] = description;
            _props[name] = p;
            if (required) _required.Add(name);
            return this;
        }

        public JObject Build()
        {
            JObject schema = new JObject();
            schema["type"] = "object";
            schema["properties"] = _props;
            if (_required.Count > 0)
            {
                JArray req = new JArray();
                for (int i = 0; i < _required.Count; i++) req.Add(_required[i]);
                schema["required"] = req;
            }
            return schema;
        }
    }
}
