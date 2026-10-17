using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Core
{
    /// <summary>
    /// <para>What a command does to the model (<c>read</c> / <c>write</c> / <c>ui</c>) and how risky it is.
    /// Replaces the old name-prefix guess (<c>IsLikelyWrite</c>).</para>
    ///
    /// <para>Sources, in order: the <c>kind</c>/<c>risk</c> the broker stamps on every command envelope (the
    /// broker is the authority: it gates on the same data); the command set's own manifest, read by
    /// reflection from <c>RevitMCPCommandSet.Framework.ToolRegistry.GetManifestJson()</c> (absent in older
    /// command sets, so guarded); the static legacy table below, mirroring <c>broker/src/classify.js</c>;
    /// and finally fail-closed <c>write/medium</c>.</para>
    /// </summary>
    internal static class ToolClassification
    {
        public sealed class Info
        {
            public Info(string kind, string risk)
            {
                Kind = kind;
                Risk = risk;
            }

            public string Kind { get; }
            public string Risk { get; }
            public bool IsWrite => string.Equals(Kind, "write", StringComparison.OrdinalIgnoreCase);
        }

        private const string RegistryTypeName = "RevitMCPCommandSet.Framework.ToolRegistry";

        // Keep in sync with LEGACY_TOOLS in broker/src/classify.js.
        private static readonly Dictionary<string, Info> Legacy = new Dictionary<string, Info>(StringComparer.OrdinalIgnoreCase)
        {
            ["say_hello"] = new Info("read", "none"),
            ["list_revit_links"] = new Info("read", "none"),
            ["get_available_family_types"] = new Info("read", "none"),
            ["get_current_view_elements"] = new Info("read", "none"),
            ["get_current_view_info"] = new Info("read", "none"),
            ["get_selected_elements"] = new Info("read", "none"),
            ["ai_element_filter"] = new Info("read", "none"),
            ["export_room_data"] = new Info("read", "none"),
            ["get_material_quantities"] = new Info("read", "none"),
            ["analyze_model_statistics"] = new Info("read", "none"),
            ["create_point_based_element"] = new Info("write", "low"),
            ["create_line_based_element"] = new Info("write", "low"),
            ["create_surface_based_element"] = new Info("write", "low"),
            ["create_grid"] = new Info("write", "low"),
            ["create_level"] = new Info("write", "low"),
            ["create_room"] = new Info("write", "low"),
            ["create_structural_framing_system"] = new Info("write", "low"),
            ["create_dimensions"] = new Info("write", "low"),
            ["color_splash"] = new Info("write", "low"),
            ["tag_walls"] = new Info("write", "medium"),
            ["tag_rooms"] = new Info("write", "medium"),
            ["delete_element"] = new Info("write", "medium"),
            ["operate_element"] = new Info("write", "low"),
            ["send_code_to_revit"] = new Info("write", "high"),
        };

        private static readonly object Sync = new object();
        private static Dictionary<string, Info> _manifest;

        /// <summary>Classify <paramref name="command"/>, preferring the broker's envelope fields when present.</summary>
        public static Info Classify(string command, string envelopeKind = null, string envelopeRisk = null)
        {
            if (!string.IsNullOrEmpty(envelopeKind))
                return new Info(envelopeKind.ToLowerInvariant(), string.IsNullOrEmpty(envelopeRisk) ? "medium" : envelopeRisk.ToLowerInvariant());

            if (!string.IsNullOrEmpty(command))
            {
                var manifest = Manifest();
                if (manifest != null && manifest.TryGetValue(command, out var fromManifest)) return fromManifest;
                if (Legacy.TryGetValue(command, out var fromTable)) return fromTable;
            }
            return new Info("write", "medium"); // fail-closed
        }

        /// <summary>
        /// The command set's manifest JSON, or null when the loaded command set predates the framework
        /// (no ToolRegistry type) or the call fails.
        /// </summary>
        public static string TryGetManifestJson()
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type type;
                    try { type = asm.GetType(RegistryTypeName, false); }
                    catch { continue; }
                    if (type == null) continue;

                    var method = type.GetMethod("GetManifestJson", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                    if (method == null || method.ReturnType != typeof(string)) return null;
                    return method.Invoke(null, null) as string;
                }
            }
            catch
            {
                // A broken manifest must never stop command dispatch; the legacy table still applies.
            }
            return null;
        }

        /// <summary>Parsed manifest, cached once found. Retried while the command set is not loaded yet.</summary>
        private static Dictionary<string, Info> Manifest()
        {
            lock (Sync)
            {
                if (_manifest != null) return _manifest;
                var json = TryGetManifestJson();
                if (string.IsNullOrEmpty(json)) return null;
                _manifest = Parse(json);
                return _manifest;
            }
        }

        internal static Dictionary<string, Info> Parse(string json)
        {
            var map = new Dictionary<string, Info>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var token = JToken.Parse(json);
                var tools = token is JArray arr ? arr : token["tools"] as JArray;
                if (tools == null) return map;
                foreach (var t in tools)
                {
                    var name = (string)t["name"];
                    if (string.IsNullOrEmpty(name)) continue;
                    var kind = ((string)t["kind"] ?? "write").ToLowerInvariant();
                    var risk = ((string)t["risk"] ?? "medium").ToLowerInvariant();
                    if (kind != "read" && kind != "write" && kind != "ui") kind = "write";
                    if (risk != "none" && risk != "low" && risk != "medium" && risk != "high") risk = "medium";
                    map[name] = new Info(kind, risk);
                }
            }
            catch
            {
                // Malformed manifest: classify from the legacy table / fail-closed.
            }
            return map;
        }
    }
}
