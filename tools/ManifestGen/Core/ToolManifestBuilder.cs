using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Manifest
{
    /// <summary>
    /// Builds tools.manifest.json (Contract D) from [McpTool] classes.
    ///
    /// Everything is matched by type/attribute NAME through CustomAttributeData, never by typeof(), so the same code
    /// works on types loaded by System.Reflection.MetadataLoadContext (ManifestGen, no Revit loaded) and on live
    /// runtime types (the add-in's ToolRegistry.GetManifestJson() can link this file and get a byte-identical
    /// manifest and capabilityHash). Keep it C# 7.3 / net48 friendly.
    /// </summary>
    public static class ToolManifestBuilder
    {
        public const int ManifestVersion = 1;

        private const string McpToolAttributeName = "McpToolAttribute";
        private const string McpToolBaseName = "McpTool`1";

        /// <summary>Units accepted on the wire (FASE0_SPEC: mm, m², m³, degrees).</summary>
        public static readonly string[] KnownUnits = { "mm", "m2", "m3", "deg" };

        /// <summary>Builds the whole manifest from candidate types (non-tool types are ignored).</summary>
        public static JObject Build(IEnumerable<Type> candidateTypes, Action<string> warn = null)
        {
            warn = warn ?? (_ => { });
            var tools = new List<JObject>();
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var type in candidateTypes)
            {
                var attr = FindAttribute(type, McpToolAttributeName, warn);
                if (attr == null) continue;
                if (type.IsAbstract)
                {
                    warn($"{type.FullName}: [McpTool] on an abstract class is ignored.");
                    continue;
                }

                var entry = BuildToolEntry(type, attr, warn);
                var name = (string)entry["name"];
                string other;
                if (seen.TryGetValue(name, out other))
                    throw new InvalidOperationException($"Duplicate tool name '{name}' on {other} and {type.FullName}.");
                seen[name] = type.FullName;
                tools.Add(entry);
            }

            var sorted = new JArray(tools.OrderBy(t => (string)t["name"], StringComparer.Ordinal));
            return new JObject
            {
                ["version"] = ManifestVersion,
                ["capabilityHash"] = ComputeCapabilityHash(sorted),
                ["tools"] = sorted
            };
        }

        public static JObject BuildToolEntry(Type toolType, CustomAttributeData mcpTool, Action<string> warn)
        {
            var name = mcpTool.ConstructorArguments.Count > 0 ? mcpTool.ConstructorArguments[0].Value as string : null;
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException($"{toolType.FullName}: [McpTool] needs a name.");
            if (!IsSnakeCase(name))
                throw new InvalidOperationException($"{toolType.FullName}: tool name '{name}' must be snake_case.");

            var kind = EnumName(Named(mcpTool, "Kind"), "Read").ToLowerInvariant();
            var risk = EnumName(Named(mcpTool, "Risk"), "None").ToLowerInvariant();
            var toolset = Named(mcpTool, "Toolset")?.Value as string ?? "core";
            var batchable = Named(mcpTool, "Batchable")?.Value as bool? ?? true;
            var idempotent = Named(mcpTool, "Idempotent")?.Value as bool? ?? false;
            var timeout = Named(mcpTool, "TimeoutSeconds")?.Value as int? ?? 50;
            var description = Named(mcpTool, "Description")?.Value as string ?? "";

            if (description.Trim().Length < 20)
                warn($"{name}: description is missing or too short; say what it does, when to use it, units and how to undo.");
            if (kind == "write" && risk == "none")
                warn($"{name}: write tool with Risk.None; set Risk explicitly (Low/Medium/High).");

            var argsType = FindArgsType(toolType);
            JObject schema;
            if (argsType == null)
            {
                warn($"{name}: {toolType.FullName} does not derive from McpTool<TArgs>; using an empty schema.");
                schema = new JObject { ["type"] = "object", ["properties"] = new JObject(), ["additionalProperties"] = false };
            }
            else
            {
                schema = ObjectSchema(argsType, new Stack<string>(), warn, name);
            }

            var entry = new JObject
            {
                ["name"] = name,
                ["description"] = description,
                ["kind"] = kind,
                ["risk"] = risk,
                ["toolset"] = toolset,
                ["batchable"] = batchable,
                ["idempotent"] = idempotent,
                ["timeoutSeconds"] = timeout,
                ["inputSchema"] = schema
            };
            // Only written when set, so tools without it keep the same entry (and capabilityHash).
            if (Named(mcpTool, "DryRunDefault")?.Value as bool? == true)
            {
                if (kind != "write") warn($"{name}: DryRunDefault only applies to write tools.");
                entry["dryRunDefault"] = true;
            }
            return entry;
        }

        /// <summary>
        /// SHA-256 (lowercase hex) of the canonical JSON of the tools array sorted by name: object keys sorted
        /// ordinally, no whitespace, Newtonsoft value formatting. Any change to a name, flag or schema changes it.
        /// </summary>
        public static string ComputeCapabilityHash(JArray sortedTools)
        {
            var canonical = Canonicalize(sortedTools).ToString(Formatting.None);
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// <summary>Indented, LF line endings, trailing newline: what gets committed.</summary>
        public static string Serialize(JObject manifest)
        {
            return manifest.ToString(Formatting.Indented).Replace("\r\n", "\n") + "\n";
        }

        // ---- schema ---------------------------------------------------------------------------------------------

        private static JObject ObjectSchema(Type type, Stack<string> path, Action<string> warn, string tool)
        {
            var properties = new JObject();
            var required = new JArray();
            path.Push(type.FullName);

            foreach (var member in SerializableMembers(type))
            {
                if (FindAttribute(member, "JsonIgnoreAttribute", warn) != null) continue;

                var jsonProp = FindAttribute(member, "JsonPropertyAttribute", warn);
                var propName = member.Name;
                var isRequired = false;
                if (jsonProp != null)
                {
                    if (jsonProp.ConstructorArguments.Count > 0 && jsonProp.ConstructorArguments[0].Value is string ctorName)
                        propName = ctorName;
                    if (Named(jsonProp, "PropertyName")?.Value is string named)
                        propName = named;
                    var req = EnumName(Named(jsonProp, "Required"), "Default");
                    isRequired = req == "Always" || req == "AllowNull";
                }
                if (FindAttribute(member, "RequiredAttribute", warn) != null ||
                    FindAttribute(member, "JsonRequiredAttribute", warn) != null)
                    isRequired = true;

                var memberType = MemberType(member);
                JObject prop;
                if (path.Contains(memberType.FullName))
                    prop = new JObject { ["type"] = "object" };
                else
                    prop = TypeSchema(memberType, path, warn, tool);

                ApplyMemberAttributes(prop, member, warn, tool, propName);
                properties[propName] = prop;
                if (isRequired) required.Add(propName);
            }

            path.Pop();
            var schema = new JObject { ["type"] = "object", ["properties"] = properties };
            if (required.Count > 0) schema["required"] = required;
            schema["additionalProperties"] = false;
            return schema;
        }

        private static JObject TypeSchema(Type type, Stack<string> path, Action<string> warn, string tool)
        {
            var nullable = UnwrapNullable(type);
            if (nullable != null) type = nullable;

            switch (type.FullName)
            {
                case "System.String":
                case "System.Char":
                    return new JObject { ["type"] = "string" };
                case "System.Boolean":
                    return new JObject { ["type"] = "boolean" };
                case "System.Byte":
                case "System.SByte":
                case "System.Int16":
                case "System.UInt16":
                case "System.Int32":
                case "System.UInt32":
                case "System.Int64":
                case "System.UInt64":
                    return new JObject { ["type"] = "integer" };
                case "System.Single":
                case "System.Double":
                case "System.Decimal":
                    return new JObject { ["type"] = "number" };
                case "System.DateTime":
                case "System.DateTimeOffset":
                    return new JObject { ["type"] = "string", ["format"] = "date-time" };
                case "System.Guid":
                    return new JObject { ["type"] = "string", ["format"] = "uuid" };
                case "System.Object":
                case "Newtonsoft.Json.Linq.JToken":
                case "Newtonsoft.Json.Linq.JValue":
                    return new JObject();
                case "Newtonsoft.Json.Linq.JObject":
                    return new JObject { ["type"] = "object" };
                case "Newtonsoft.Json.Linq.JArray":
                    return new JObject { ["type"] = "array" };
            }

            if (type.IsEnum)
                return new JObject { ["type"] = "string", ["enum"] = new JArray(type.GetEnumNames().Cast<object>().ToArray()) };

            if (type.IsArray)
                return ArraySchema(type.GetElementType(), path, warn, tool);

            var dictValue = GenericInterfaceArg(type, "System.Collections.Generic.IDictionary`2", 1)
                            ?? GenericInterfaceArg(type, "System.Collections.Generic.IReadOnlyDictionary`2", 1);
            if (dictValue != null)
            {
                return new JObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = path.Contains(dictValue.FullName) ? new JObject() : TypeSchema(dictValue, path, warn, tool)
                };
            }

            var element = GenericInterfaceArg(type, "System.Collections.Generic.IEnumerable`1", 0);
            if (element != null)
                return ArraySchema(element, path, warn, tool);

            if (type.IsPrimitive || type.IsPointer)
            {
                warn($"{tool}: unsupported type {type.FullName}; schema left open.");
                return new JObject();
            }

            if (type.Namespace != null && type.Namespace.StartsWith("Autodesk.", StringComparison.Ordinal))
            {
                warn($"{tool}: argument type {type.FullName} is a Revit API type; use plain values (ids, mm) on the wire.");
                return new JObject();
            }

            return ObjectSchema(type, path, warn, tool);
        }

        private static JObject ArraySchema(Type element, Stack<string> path, Action<string> warn, string tool)
        {
            var items = path.Contains(element.FullName) ? new JObject { ["type"] = "object" } : TypeSchema(element, path, warn, tool);
            return new JObject { ["type"] = "array", ["items"] = items };
        }

        private static void ApplyMemberAttributes(JObject prop, MemberInfo member, Action<string> warn, string tool, string propName)
        {
            // Attributes that constrain values go on the items for arrays.
            var target = (string)prop["type"] == "array" && prop["items"] is JObject items ? items : prop;

            var enumValues = FindAttribute(member, "EnumValuesAttribute", warn);
            if (enumValues != null)
            {
                var values = new JArray();
                foreach (var arg in enumValues.ConstructorArguments)
                    foreach (var v in Flatten(arg))
                        values.Add(v);
                if (values.Count > 0)
                {
                    target["type"] = target["type"] ?? "string";
                    target["enum"] = values;
                }
            }

            var range = FindAttribute(member, "RangeAttribute", warn);
            if (range != null)
            {
                var args = range.ConstructorArguments;
                var offset = args.Count == 3 ? 1 : 0; // (Type, string, string) overload
                if (args.Count >= 2)
                {
                    var min = ToDouble(args[offset].Value);
                    var max = ToDouble(args[offset + 1].Value);
                    if (min.HasValue && !double.IsInfinity(min.Value) && min.Value > double.MinValue && min.Value > int.MinValue)
                        target["minimum"] = Number(min.Value);
                    if (max.HasValue && !double.IsInfinity(max.Value) && max.Value < double.MaxValue && max.Value < int.MaxValue)
                        target["maximum"] = Number(max.Value);
                }
            }

            var unit = FindAttribute(member, "UnitAttribute", warn);
            string unitName = null;
            if (unit != null)
            {
                unitName = unit.ConstructorArguments.Count > 0 ? Convert.ToString(unit.ConstructorArguments[0].Value, CultureInfo.InvariantCulture) : null;
                if (string.IsNullOrEmpty(unitName))
                    unitName = Named(unit, "Name")?.Value as string ?? Named(unit, "Unit")?.Value as string;
                if (!string.IsNullOrEmpty(unitName))
                {
                    if (Array.IndexOf(KnownUnits, unitName) < 0)
                        warn($"{tool}.{propName}: unit '{unitName}' is not one of {string.Join(", ", KnownUnits)}.");
                    prop["x-unit"] = unitName;
                }
            }

            var defaultValue = FindAttribute(member, "DefaultValueAttribute", warn);
            if (defaultValue != null && defaultValue.ConstructorArguments.Count > 0)
            {
                var arg = defaultValue.ConstructorArguments.Count == 1
                    ? defaultValue.ConstructorArguments[0]
                    : defaultValue.ConstructorArguments[defaultValue.ConstructorArguments.Count - 1];
                var token = TypedToToken(arg);
                if (token != null && token.Type != JTokenType.Null) prop["default"] = token;
            }

            var descriptionAttr = FindAttribute(member, "DescriptionAttribute", warn);
            var description = descriptionAttr != null && descriptionAttr.ConstructorArguments.Count > 0
                ? descriptionAttr.ConstructorArguments[0].Value as string
                : null;
            if (!string.IsNullOrEmpty(unitName) && (description == null || description.IndexOf(unitName, StringComparison.OrdinalIgnoreCase) < 0))
                description = (string.IsNullOrEmpty(description) ? "" : description.TrimEnd() + " ") + "Unit: " + unitName + ".";
            if (!string.IsNullOrEmpty(description)) prop.AddFirst(new JProperty("description", description));
            else warn($"{tool}.{propName}: no [Description].");

            var type = (string)target["type"];
            if (type == "number" && string.IsNullOrEmpty(unitName) && LooksLikeMeasure(propName))
                warn($"{tool}.{propName}: numeric measure without [Unit] (mm, m2, m3, deg).");
        }

        /// <summary>Name heuristic shared with the server's contract test.</summary>
        public static bool LooksLikeMeasure(string name)
        {
            var n = name.ToLowerInvariant();
            string[] words =
            {
                "length", "width", "height", "depth", "thickness", "offset", "elevation", "distance", "radius",
                "diameter", "area", "volume", "angle", "rotation", "spacing", "tolerance", "gap", "size", "x", "y", "z"
            };
            foreach (var w in words)
            {
                if (n == w) return true;
                if (w.Length > 1 && (n.EndsWith(w, StringComparison.Ordinal) || n.StartsWith(w, StringComparison.Ordinal)))
                    return true;
            }
            return false;
        }

        // ---- reflection helpers (name-based, MetadataLoadContext safe) -----------------------------------------

        private static IEnumerable<MemberInfo> SerializableMembers(Type type)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var members = new List<MemberInfo>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in type.GetProperties(flags))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                var getter = p.GetGetMethod(true);
                var isPublic = getter != null && getter.IsPublic;
                if (!isPublic && !HasAttributeNamed(p, "JsonPropertyAttribute")) continue;
                if (names.Add(p.Name)) members.Add(p);
            }
            foreach (var f in type.GetFields(flags))
            {
                if (f.IsStatic || f.Name.Contains("<")) continue;
                if (!f.IsPublic && !HasAttributeNamed(f, "JsonPropertyAttribute")) continue;
                if (names.Add(f.Name)) members.Add(f);
            }
            return members.OrderBy(m => DeclarationDepth(m.DeclaringType, type)).ThenBy(m => m.MetadataToken);
        }

        private static int DeclarationDepth(Type declaring, Type type)
        {
            // Base-class members first (like Newtonsoft), then the derived ones.
            var depth = 0;
            for (var t = type; t != null && t.FullName != declaring.FullName; t = t.BaseType) depth++;
            return -depth;
        }

        private static bool HasAttributeNamed(MemberInfo member, string name)
        {
            try
            {
                return member.GetCustomAttributesData().Any(a => SafeAttrName(a) == name);
            }
            catch
            {
                return false;
            }
        }

        private static Type MemberType(MemberInfo m)
        {
            var p = m as PropertyInfo;
            return p != null ? p.PropertyType : ((FieldInfo)m).FieldType;
        }

        public static CustomAttributeData FindAttribute(MemberInfo member, string name, Action<string> warn)
        {
            IList<CustomAttributeData> all;
            try
            {
                all = member.GetCustomAttributesData();
            }
            catch (Exception ex)
            {
                warn?.Invoke($"{member.Name}: cannot read attributes ({ex.Message}).");
                return null;
            }
            foreach (var a in all)
            {
                if (SafeAttrName(a) == name) return a;
            }
            return null;
        }

        private static string SafeAttrName(CustomAttributeData a)
        {
            try
            {
                return a.AttributeType.Name;
            }
            catch
            {
                // Attribute type from an assembly we cannot resolve (e.g. a Revit attribute); not one of ours.
                return null;
            }
        }

        private static CustomAttributeTypedArgument? Named(CustomAttributeData attr, string member)
        {
            foreach (var n in attr.NamedArguments)
            {
                if (n.MemberName == member) return n.TypedValue;
            }
            return null;
        }

        private static string EnumName(CustomAttributeTypedArgument? arg, string fallback)
        {
            if (arg == null || arg.Value.Value == null) return fallback;
            var t = arg.Value.ArgumentType;
            if (t.IsEnum)
            {
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (Convert.ToInt64(f.GetRawConstantValue(), CultureInfo.InvariantCulture) ==
                        Convert.ToInt64(arg.Value.Value, CultureInfo.InvariantCulture))
                        return f.Name;
                }
            }
            return Convert.ToString(arg.Value.Value, CultureInfo.InvariantCulture);
        }

        private static IEnumerable<JToken> Flatten(CustomAttributeTypedArgument arg)
        {
            if (arg.Value is IEnumerable<CustomAttributeTypedArgument> list)
            {
                foreach (var item in list)
                    foreach (var v in Flatten(item))
                        yield return v;
            }
            else if (arg.Value is Type enumType && enumType.IsEnum)
            {
                foreach (var n in enumType.GetEnumNames()) yield return n;
            }
            else
            {
                var token = TypedToToken(arg);
                if (token != null) yield return token;
            }
        }

        private static JToken TypedToToken(CustomAttributeTypedArgument arg)
        {
            var v = arg.Value;
            if (v == null) return JValue.CreateNull();
            if (arg.ArgumentType.IsEnum) return EnumName(arg, null);
            if (v is IEnumerable<CustomAttributeTypedArgument> list)
                return new JArray(list.Select(TypedToToken).Where(t => t != null).ToArray());
            if (v is Type) return null;
            if (v is double d) return Number(d);
            if (v is float f) return Number(f);
            return new JValue(v);
        }

        /// <summary>Integral doubles are written as integers so 5000.0 and 5000 hash the same everywhere.</summary>
        private static JToken Number(double d)
        {
            if (Math.Abs(d) < 9e15 && Math.Floor(d) == d) return new JValue((long)d);
            return new JValue(d);
        }

        private static double? ToDouble(object v)
        {
            if (v == null) return null;
            if (v is string s)
            {
                double parsed;
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : (double?)null;
            }
            try
            {
                return Convert.ToDouble(v, CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        private static Type UnwrapNullable(Type t)
        {
            return t.IsGenericType && t.GetGenericTypeDefinition().FullName == "System.Nullable`1" ? t.GetGenericArguments()[0] : null;
        }

        private static Type GenericInterfaceArg(Type type, string genericDefinition, int index)
        {
            if (type.FullName == "System.String") return null;
            var candidates = new List<Type>();
            if (type.IsGenericType) candidates.Add(type);
            candidates.AddRange(type.GetInterfaces());
            foreach (var c in candidates)
            {
                if (c.IsGenericType && c.GetGenericTypeDefinition().FullName == genericDefinition)
                    return c.GetGenericArguments()[index];
            }
            return null;
        }

        private static Type FindArgsType(Type toolType)
        {
            for (var t = toolType.BaseType; t != null; t = t.BaseType)
            {
                if (t.IsGenericType && t.GetGenericTypeDefinition().Name == McpToolBaseName)
                    return t.GetGenericArguments()[0];
            }
            return null;
        }

        private static bool IsSnakeCase(string name)
        {
            if (name.Length == 0 || !(name[0] >= 'a' && name[0] <= 'z')) return false;
            foreach (var c in name)
            {
                if (!(c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '_')) return false;
            }
            return true;
        }

        private static JToken Canonicalize(JToken token)
        {
            var obj = token as JObject;
            if (obj != null)
            {
                var sorted = new JObject();
                foreach (var p in obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
                    sorted[p.Name] = Canonicalize(p.Value);
                return sorted;
            }
            var arr = token as JArray;
            if (arr != null) return new JArray(arr.Select(Canonicalize).ToArray());
            if (token.Type == JTokenType.Float) return new JRaw(JsNumber(token.Value<double>()));
            return token.DeepClone();
        }

        /// <summary>
        /// Formats a double exactly like JavaScript's Number#toString (shortest round-trip digits, exponent only
        /// below 1e-6 or from 1e21), so the server's canonical JSON — and the capabilityHash — match.
        /// Newtonsoft would write 0.000001 as "1E-06".
        /// </summary>
        public static string JsNumber(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return "null";
            if (d == 0) return "0";
            string r = d.ToString("R", CultureInfo.InvariantCulture);
            bool negative = r[0] == '-';
            if (negative) r = r.Substring(1);

            // Split "d.dddE±x" (or plain decimal) into significant digits and a decimal exponent.
            int exp = 0;
            int e = r.IndexOfAny(new[] { 'E', 'e' });
            if (e >= 0)
            {
                exp = int.Parse(r.Substring(e + 1), CultureInfo.InvariantCulture);
                r = r.Substring(0, e);
            }
            int dot = r.IndexOf('.');
            string digits = dot >= 0 ? r.Remove(dot, 1) : r;
            int pointPos = (dot >= 0 ? dot : r.Length) + exp; // decimal point position within digits
            int lead = 0;
            while (lead < digits.Length - 1 && digits[lead] == '0') { lead++; pointPos--; }
            digits = digits.Substring(lead).TrimEnd('0');
            if (digits.Length == 0) return "0";

            // JS: n = pointPos; k = digits.Length; scientific when n > 21 or n <= -6.
            int n = pointPos, k = digits.Length;
            string s;
            if (k <= n && n <= 21) s = digits + new string('0', n - k);
            else if (0 < n && n <= 21) s = digits.Substring(0, n) + "." + digits.Substring(n);
            else if (-6 < n && n <= 0) s = "0." + new string('0', -n) + digits;
            else
            {
                int x = n - 1;
                string mant = k == 1 ? digits : digits.Substring(0, 1) + "." + digits.Substring(1);
                s = mant + "e" + (x >= 0 ? "+" : "-") + Math.Abs(x).ToString(CultureInfo.InvariantCulture);
            }
            return negative ? "-" + s : s;
        }
    }
}
