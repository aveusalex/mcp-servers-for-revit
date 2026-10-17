using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMCPCommandSet.Framework
{
    /// <summary>
    /// Builds the JSON schema of a tool's arguments from its POCO. Rules (the manifest generator must match them):
    /// <list type="bullet">
    /// <item>Properties: public instance get/set, minus <c>[JsonIgnore]</c>. Name = <c>[JsonProperty]</c> name, else the
    /// property name in camelCase. Root and nested objects get <c>additionalProperties: false</c>.</item>
    /// <item>string → string; bool → boolean; int/long/short/byte → integer; double/float/decimal → number; enum →
    /// string with <c>enum</c> = member names; arrays/lists/sets → array; <c>Dictionary&lt;string,T&gt;</c> → object
    /// with <c>additionalProperties</c>; <c>JObject</c> → free object; <c>JToken</c>/object → no type; other classes →
    /// nested object. <c>Nullable&lt;T&gt;</c> → T.</item>
    /// <item><c>[Description]</c> → description; <c>[Unit(u)]</c> → <c>x-unit</c> and " (unit: u)" appended to the
    /// description; <c>[Required]</c> → required; <c>[Range]</c> → minimum/maximum; <c>[EnumValues]</c> → enum;
    /// <c>[DefaultValue]</c> → default; <c>[MinLength]/[MaxLength]</c> → minItems/maxItems on arrays,
    /// minLength/maxLength on strings. On arrays, Unit/Range/EnumValues apply to the items.</item>
    /// </list>
    /// </summary>
    /// <remarks>No Revit dependency: shared with the manifest generator and the framework tests.</remarks>
    public static class ToolSchemaGenerator
    {
        private const int MaxDepth = 8;

        public static JObject Generate(Type argsType)
        {
            return ObjectSchema(argsType, new Stack<Type>());
        }

        /// <summary>Arguments a tool's POCO exposes, in declaration order.</summary>
        public static IReadOnlyList<PropertyInfo> ArgProperties(Type type)
        {
            return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
                .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() == null)
                .OrderBy(p => p.MetadataToken)
                .ToList();
        }

        /// <summary>The name the argument has on the wire.</summary>
        public static string JsonName(PropertyInfo property)
        {
            var attr = property.GetCustomAttribute<JsonPropertyAttribute>();
            if (attr != null && !string.IsNullOrEmpty(attr.PropertyName)) return attr.PropertyName;
            var name = property.Name;
            return name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        /// <summary>
        /// Problems that make a POCO break the contract (e.g. a number without <c>[Unit]</c>). Empty when it's fine.
        /// </summary>
        public static IList<string> FindContractViolations(Type argsType, string toolName = null)
        {
            var problems = new List<string>();
            CheckObject(argsType, toolName ?? argsType.Name, problems, new Stack<Type>());
            return problems;
        }

        internal static bool IsNumber(Type type) =>
            type == typeof(double) || type == typeof(float) || type == typeof(decimal);

        internal static bool IsInteger(Type type) =>
            type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) ||
            type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(sbyte);

        /// <summary>Element type when <paramref name="type"/> is an array/list/set (not string or dictionary).</summary>
        internal static Type ItemType(Type type)
        {
            if (type == typeof(string) || IsDictionary(type) || typeof(JToken).IsAssignableFrom(type)) return null;
            if (type.IsArray) return type.GetElementType();
            if (!typeof(IEnumerable).IsAssignableFrom(type)) return null;
            var enumerable = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                ? type
                : type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            return enumerable?.GetGenericArguments()[0] ?? typeof(object);
        }

        internal static bool IsDictionary(Type type) =>
            type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Dictionary<,>) ||
                                   type.GetGenericTypeDefinition() == typeof(IDictionary<,>) ||
                                   type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>));

        internal static bool IsNestedObject(Type type) =>
            type.IsClass && type != typeof(string) && type != typeof(object) && !typeof(JToken).IsAssignableFrom(type) &&
            ItemType(type) == null && !IsDictionary(type);

        internal static Type Unwrap(Type type) => Nullable.GetUnderlyingType(type) ?? type;

        private static JObject ObjectSchema(Type type, Stack<Type> path)
        {
            var schema = new JObject { ["type"] = "object" };
            if (path.Contains(type) || path.Count >= MaxDepth)
            {
                return schema; // recursive type: stop with a free object
            }

            path.Push(type);
            var properties = new JObject();
            var required = new JArray();
            foreach (var property in ArgProperties(type))
            {
                var name = JsonName(property);
                properties[name] = PropertySchema(property, path);
                if (property.GetCustomAttribute<RequiredAttribute>() != null)
                {
                    required.Add(name);
                }
            }
            path.Pop();

            schema["properties"] = properties;
            if (required.Count > 0) schema["required"] = required;
            schema["additionalProperties"] = false;
            return schema;
        }

        private static JObject PropertySchema(PropertyInfo property, Stack<Type> path)
        {
            var type = Unwrap(property.PropertyType);
            var schema = TypeSchema(type, path);
            var itemType = ItemType(type);
            // Value constraints go on the items for arrays.
            var valueSchema = itemType != null && schema["items"] is JObject items ? items : schema;

            var description = property.GetCustomAttribute<DescriptionAttribute>()?.Description;
            var unit = property.GetCustomAttribute<UnitAttribute>()?.Unit;
            if (!string.IsNullOrEmpty(unit))
            {
                valueSchema["x-unit"] = unit;
                if (unit != "none")
                {
                    description = string.IsNullOrEmpty(description) ? $"Unit: {unit}." : $"{description} (unit: {unit})";
                }
            }
            if (!string.IsNullOrEmpty(description))
            {
                schema["description"] = description;
            }

            var enumValues = property.GetCustomAttribute<EnumValuesAttribute>();
            if (enumValues != null)
            {
                valueSchema["enum"] = new JArray(enumValues.Values.Cast<object>().ToArray());
            }

            var range = property.GetCustomAttribute<RangeAttribute>();
            if (range != null)
            {
                if (TryNumber(range.Minimum, out var min) && !double.IsNegativeInfinity(min) && min > double.MinValue)
                    valueSchema["minimum"] = NumberToken(min);
                if (TryNumber(range.Maximum, out var max) && !double.IsPositiveInfinity(max) && max < double.MaxValue)
                    valueSchema["maximum"] = NumberToken(max);
            }

            var minLength = property.GetCustomAttribute<MinLengthAttribute>();
            var maxLength = property.GetCustomAttribute<MaxLengthAttribute>();
            if (itemType != null)
            {
                if (minLength != null) schema["minItems"] = minLength.Length;
                if (maxLength != null && maxLength.Length > 0) schema["maxItems"] = maxLength.Length;
            }
            else if (type == typeof(string))
            {
                if (minLength != null) schema["minLength"] = minLength.Length;
                if (maxLength != null && maxLength.Length > 0) schema["maxLength"] = maxLength.Length;
            }

            var defaultValue = property.GetCustomAttribute<DefaultValueAttribute>();
            if (defaultValue != null)
            {
                schema["default"] = defaultValue.Value == null
                    ? JValue.CreateNull()
                    : defaultValue.Value is Enum e ? new JValue(e.ToString()) : JToken.FromObject(defaultValue.Value);
            }

            return schema;
        }

        private static JObject TypeSchema(Type type, Stack<Type> path)
        {
            type = Unwrap(type);
            if (type == typeof(string)) return new JObject { ["type"] = "string" };
            if (type == typeof(bool)) return new JObject { ["type"] = "boolean" };
            if (IsInteger(type)) return new JObject { ["type"] = "integer" };
            if (IsNumber(type)) return new JObject { ["type"] = "number" };
            if (type.IsEnum)
            {
                return new JObject
                {
                    ["type"] = "string",
                    ["enum"] = new JArray(Enum.GetNames(type).Cast<object>().ToArray())
                };
            }
            if (type == typeof(JObject)) return new JObject { ["type"] = "object" };
            if (type == typeof(object) || typeof(JToken).IsAssignableFrom(type)) return new JObject();
            if (IsDictionary(type))
            {
                return new JObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = TypeSchema(type.GetGenericArguments()[1], path)
                };
            }

            var itemType = ItemType(type);
            if (itemType != null)
            {
                return new JObject { ["type"] = "array", ["items"] = TypeSchema(itemType, path) };
            }

            return ObjectSchema(type, path);
        }

        private static void CheckObject(Type type, string where, IList<string> problems, Stack<Type> path)
        {
            if (path.Contains(type) || path.Count >= MaxDepth) return;
            path.Push(type);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in ArgProperties(type))
            {
                var name = JsonName(property);
                var label = $"{where}.{name}";
                if (!seen.Add(name))
                {
                    problems.Add($"{label}: duplicate argument name (names are case-insensitive).");
                }

                var valueType = Unwrap(property.PropertyType);
                var itemType = ItemType(valueType);
                var scalar = itemType != null ? Unwrap(itemType) : valueType;
                var unit = property.GetCustomAttribute<UnitAttribute>();

                if (IsNumber(scalar))
                {
                    if (unit == null)
                    {
                        problems.Add($"{label}: numeric argument without [Unit] (use mm, m2, m3, deg or none).");
                    }
                }
                if (unit != null && !UnitAttribute.Allowed.Contains(unit.Unit))
                {
                    problems.Add($"{label}: unit '{unit.Unit}' is not one of {string.Join(", ", UnitAttribute.Allowed)}.");
                }
                if (unit != null && !IsNumber(scalar) && !IsInteger(scalar))
                {
                    problems.Add($"{label}: [Unit] on a non-numeric argument.");
                }
                if (property.GetCustomAttribute<EnumValuesAttribute>() != null && scalar != typeof(string))
                {
                    problems.Add($"{label}: [EnumValues] only applies to strings or string arrays.");
                }

                if (IsNestedObject(scalar))
                {
                    CheckObject(scalar, label, problems, path);
                }
            }

            path.Pop();
        }

        private static bool TryNumber(object value, out double number)
        {
            number = 0;
            if (value == null) return false;
            try
            {
                number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static JToken NumberToken(double value)
        {
            if (Math.Abs(value) < 9e15 && Math.Floor(value) == value)
                return new JValue((long)value);
            return new JValue(value);
        }
    }
}
