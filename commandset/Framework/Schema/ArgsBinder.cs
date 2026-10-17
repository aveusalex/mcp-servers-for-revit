using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMCPCommandSet.Framework
{
    /// <summary>
    /// Turns the JSON arguments of a call into the tool's POCO, enforcing what the schema promises: no unknown
    /// arguments, [Required] present, [Range], [EnumValues], [MinLength]/[MaxLength]. Every failure is an
    /// INVALID_ARGUMENTS <see cref="ToolException"/> with a message the AI can act on.
    /// </summary>
    /// <remarks>No Revit dependency: covered by the framework tests.</remarks>
    public static class ArgsBinder
    {
        private static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            MissingMemberHandling = MissingMemberHandling.Ignore, // unknown names are reported by Check
            Culture = CultureInfo.InvariantCulture
        });

        public static T Bind<T>(JObject args) where T : class, new()
        {
            return (T)Bind(args, typeof(T)) ?? new T();
        }

        public static object Bind(JObject args, Type type)
        {
            args = args ?? new JObject();
            Check(args, type, string.Empty, 0);
            try
            {
                return args.ToObject(type, Serializer);
            }
            catch (JsonException ex)
            {
                throw new ToolException(ToolErrorCodes.InvalidArguments,
                    $"Invalid arguments: {ex.Message}",
                    "Compare the arguments with the tool's input schema (types and names).");
            }
        }

        private static void Check(JObject json, Type type, string prefix, int depth)
        {
            if (depth > 8) return;
            var properties = ToolSchemaGenerator.ArgProperties(type);
            var byName = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in properties)
            {
                byName[ToolSchemaGenerator.JsonName(p)] = p;
            }

            foreach (var entry in json.Properties())
            {
                if (!byName.ContainsKey(entry.Name))
                {
                    var allowed = byName.Count == 0 ? "(none)" : string.Join(", ", byName.Keys);
                    throw Invalid($"Unknown argument '{prefix}{entry.Name}'. Accepted: {allowed}.");
                }
            }

            foreach (var property in properties)
            {
                var name = ToolSchemaGenerator.JsonName(property);
                var label = prefix + name;
                var token = Find(json, name);
                bool missing = token == null || token.Type == JTokenType.Null ||
                               (token.Type == JTokenType.String && string.IsNullOrWhiteSpace((string)token));

                if (missing)
                {
                    if (property.GetCustomAttribute<RequiredAttribute>() != null)
                        throw Invalid($"Missing required argument '{label}'.");
                    continue;
                }

                CheckValue(property, token, label, depth);
            }
        }

        private static void CheckValue(PropertyInfo property, JToken token, string label, int depth)
        {
            var type = ToolSchemaGenerator.Unwrap(property.PropertyType);
            var itemType = ToolSchemaGenerator.ItemType(type);

            var minLength = property.GetCustomAttribute<MinLengthAttribute>();
            var maxLength = property.GetCustomAttribute<MaxLengthAttribute>();

            IEnumerable<JToken> values;
            if (itemType != null)
            {
                if (!(token is JArray array))
                    throw Invalid($"Argument '{label}' must be an array.");
                if (minLength != null && array.Count < minLength.Length)
                    throw Invalid($"Argument '{label}' needs at least {minLength.Length} item(s).");
                if (maxLength != null && maxLength.Length > 0 && array.Count > maxLength.Length)
                    throw Invalid($"Argument '{label}' accepts at most {maxLength.Length} item(s); got {array.Count}.");
                values = array;
            }
            else
            {
                if (type == typeof(string) && token.Type == JTokenType.String)
                {
                    var length = ((string)token).Length;
                    if (minLength != null && length < minLength.Length)
                        throw Invalid($"Argument '{label}' must have at least {minLength.Length} character(s).");
                    if (maxLength != null && maxLength.Length > 0 && length > maxLength.Length)
                        throw Invalid($"Argument '{label}' must have at most {maxLength.Length} character(s).");
                }
                values = new[] { token };
            }

            var enumValues = property.GetCustomAttribute<EnumValuesAttribute>();
            var range = property.GetCustomAttribute<RangeAttribute>();
            var scalar = itemType != null ? ToolSchemaGenerator.Unwrap(itemType) : type;

            foreach (var value in values)
            {
                if (value == null || value.Type == JTokenType.Null) continue;

                if (enumValues != null && value.Type == JTokenType.String)
                {
                    var text = (string)value;
                    if (!enumValues.Values.Any(v => string.Equals(v, text, StringComparison.OrdinalIgnoreCase)))
                        throw Invalid($"Argument '{label}' must be one of: {string.Join(", ", enumValues.Values)}; got '{text}'.");
                }

                if (range != null && (value.Type == JTokenType.Integer || value.Type == JTokenType.Float))
                {
                    var number = value.Value<double>();
                    var min = Convert.ToDouble(range.Minimum, CultureInfo.InvariantCulture);
                    var max = Convert.ToDouble(range.Maximum, CultureInfo.InvariantCulture);
                    if (number < min || number > max)
                        throw Invalid($"Argument '{label}' must be between {Format(min)} and {Format(max)}; got {Format(number)}.");
                }

                if (ToolSchemaGenerator.IsNestedObject(scalar))
                {
                    if (!(value is JObject nested))
                        throw Invalid($"Argument '{label}' must be an object.");
                    Check(nested, scalar, label + ".", depth + 1);
                }
            }
        }

        private static JToken Find(JObject json, string name)
        {
            var exact = json[name];
            if (exact != null) return exact;
            return json.Properties()
                .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;
        }

        private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        private static ToolException Invalid(string message) =>
            new ToolException(ToolErrorCodes.InvalidArguments, message,
                "Compare the arguments with the tool's input schema (names, types, limits).");
    }
}
