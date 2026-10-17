using System;
using System.ComponentModel;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMCPCommandSet.Framework.Services
{
    /// <summary>
    /// Reference to a parameter, resolved in this order: <c>guid</c> (shared parameter, immune to language and
    /// duplicates) → <c>builtIn</c> (e.g. ALL_MODEL_INSTANCE_COMMENTS, language-neutral) → <c>name</c> (as the user
    /// sees it, localized). A plain JSON string is read as <c>{name}</c>.
    /// </summary>
    [JsonConverter(typeof(ParameterRefConverter))]
    public sealed class ParameterRef
    {
        [JsonProperty("guid")]
        [Description("Shared parameter GUID. Wins over builtIn and name.")]
        public string Guid { get; set; }

        [JsonProperty("builtIn")]
        [Description("BuiltInParameter enum name, e.g. ALL_MODEL_INSTANCE_COMMENTS. Language-neutral.")]
        public string BuiltIn { get; set; }

        [JsonProperty("name")]
        [Description("Parameter name as shown in Revit (in Revit's language). Fails with AMBIGUOUS_PARAMETER if two parameters share it.")]
        public string Name { get; set; }

        public static ParameterRef ByName(string name) => new ParameterRef { Name = name };

        [JsonIgnore]
        public bool IsEmpty => string.IsNullOrWhiteSpace(Guid) && string.IsNullOrWhiteSpace(BuiltIn) && string.IsNullOrWhiteSpace(Name);

        /// <summary>Parsed GUID, or throws INVALID_ARGUMENTS for a malformed one; null when no GUID was given.</summary>
        public Guid? ParsedGuid()
        {
            if (string.IsNullOrWhiteSpace(Guid)) return null;
            if (System.Guid.TryParse(Guid.Trim(), out var g)) return g;
            throw new ToolException("INVALID_ARGUMENTS", $"'{Guid}' is not a valid GUID.");
        }

        public override string ToString() =>
            !string.IsNullOrWhiteSpace(Guid) ? "guid:" + Guid :
            !string.IsNullOrWhiteSpace(BuiltIn) ? BuiltIn :
            Name ?? string.Empty;
    }

    /// <summary>Accepts "Comments" or {"guid"|"builtIn"|"name": ...}.</summary>
    public sealed class ParameterRefConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) => objectType == typeof(ParameterRef);

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            var token = JToken.Load(reader);
            switch (token.Type)
            {
                case JTokenType.Null:
                case JTokenType.Undefined:
                    return null;
                case JTokenType.String:
                    return new ParameterRef { Name = (string)token };
                case JTokenType.Object:
                    var o = (JObject)token;
                    return new ParameterRef
                    {
                        Guid = Str(o, "guid"),
                        BuiltIn = Str(o, "builtIn") ?? Str(o, "builtin") ?? Str(o, "builtInParameter"),
                        Name = Str(o, "name")
                    };
                default:
                    throw new JsonSerializationException("A parameter reference must be a name string or {guid|builtIn|name}.");
            }
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            var r = (ParameterRef)value;
            writer.WriteStartObject();
            if (!string.IsNullOrWhiteSpace(r.Guid)) { writer.WritePropertyName("guid"); writer.WriteValue(r.Guid); }
            if (!string.IsNullOrWhiteSpace(r.BuiltIn)) { writer.WritePropertyName("builtIn"); writer.WriteValue(r.BuiltIn); }
            if (!string.IsNullOrWhiteSpace(r.Name)) { writer.WritePropertyName("name"); writer.WriteValue(r.Name); }
            writer.WriteEndObject();
        }

        private static string Str(JObject o, string key)
        {
            var t = o.GetValue(key, StringComparison.OrdinalIgnoreCase);
            return t == null || t.Type == JTokenType.Null ? null : t.ToString();
        }
    }

    public enum ElementRefKind
    {
        Id,
        UniqueId,
        /// <summary>"linkInstanceId:elementId" — an element inside a Revit link (read-only).</summary>
        Linked
    }

    /// <summary>
    /// A parsed element reference: a numeric id ("123456"), a UniqueId, or "linkInstanceId:elementId" for an element
    /// inside a link. Pure parsing; <see cref="LookupService"/> resolves it in the document.
    /// </summary>
    public sealed class ElementRef
    {
        private ElementRef(ElementRefKind kind, string text, long id, string uniqueId, long linkInstanceId)
        {
            Kind = kind;
            Text = text;
            Id = id;
            UniqueId = uniqueId;
            LinkInstanceId = linkInstanceId;
        }

        public ElementRefKind Kind { get; }
        public string Text { get; }

        /// <summary>The element id (inside the link for <see cref="ElementRefKind.Linked"/>).</summary>
        public long Id { get; }

        public string UniqueId { get; }
        public long LinkInstanceId { get; }

        public static ElementRef Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ToolException("INVALID_ARGUMENTS", "An element reference is empty.");
            var t = text.Trim();

            if (TryLong(t, out var id))
            {
                if (id <= 0)
                    throw new ToolException("INVALID_ARGUMENTS", $"'{t}' is not a valid element id (must be positive).");
                return new ElementRef(ElementRefKind.Id, t, id, null, 0);
            }

            var colon = t.IndexOf(':');
            if (colon > 0 && t.IndexOf(':', colon + 1) < 0 &&
                TryLong(t.Substring(0, colon).Trim(), out var link) &&
                TryLong(t.Substring(colon + 1).Trim(), out var inner))
            {
                if (link <= 0 || inner <= 0)
                    throw new ToolException("INVALID_ARGUMENTS", $"'{t}' is not a valid linked element reference.");
                return new ElementRef(ElementRefKind.Linked, t, inner, null, link);
            }

            // Revit UniqueIds are "<guid>-<8 hex>" (45 chars); accept any token without blanks that has a dash.
            if (t.IndexOf('-') > 0 && t.IndexOf(' ') < 0 && t.Length >= 20)
                return new ElementRef(ElementRefKind.UniqueId, t, 0, t, 0);

            throw new ToolException("INVALID_ARGUMENTS",
                $"'{t}' is not an element id, a UniqueId or 'linkInstanceId:elementId'.");
        }

        public static ElementRef FromId(long id) => Parse(id.ToString(CultureInfo.InvariantCulture));

        private static bool TryLong(string s, out long v) =>
            long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
    }
}
