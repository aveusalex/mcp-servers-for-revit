using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMCPCommandSet.Framework
{
    /// <summary>
    /// Serializes <see cref="ToolResult"/> to the wire envelope
    /// <c>{ ok, data, changes?, warnings[], dryRun, error?{code, message, hint?, recommendedTool?} }</c>.
    /// </summary>
    /// <remarks>No Revit dependency: shared with the framework tests.</remarks>
    public static class EnvelopeJson
    {
        /// <summary>Serializer for tool data: tolerant of loops, no nulls dropped (null is information).</summary>
        public static readonly JsonSerializer DataSerializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            NullValueHandling = NullValueHandling.Include,
            MaxDepth = 64
        });

        public static JObject ToJson(ToolResult result)
        {
            var json = new JObject
            {
                ["ok"] = result.Ok,
                ["data"] = DataToJson(result.Data)
            };

            if (result.Changes != null)
            {
                json["changes"] = ChangesToJson(result.Changes);
            }

            json["warnings"] = new JArray((result.Warnings ?? new System.Collections.Generic.List<ToolWarning>())
                .Select(WarningToJson));
            json["dryRun"] = result.DryRun;

            if (result.Error != null)
            {
                var error = new JObject
                {
                    ["code"] = result.Error.Code,
                    ["message"] = result.Error.Message
                };
                if (!string.IsNullOrEmpty(result.Error.Hint)) error["hint"] = result.Error.Hint;
                if (!string.IsNullOrEmpty(result.Error.RecommendedTool)) error["recommendedTool"] = result.Error.RecommendedTool;
                json["error"] = error;
            }

            return json;
        }

        public static JToken DataToJson(object data)
        {
            if (data == null) return JValue.CreateNull();
            if (data is JToken token) return token;
            return JToken.FromObject(data, DataSerializer);
        }

        public static JObject ChangesToJson(ChangeSet changes)
        {
            var json = new JObject
            {
                ["created"] = new JArray(changes.Created),
                ["modified"] = new JArray(changes.Modified),
                ["deleted"] = new JArray(changes.Deleted)
            };
            if (changes.Truncated) json["truncated"] = true;
            return json;
        }

        public static JObject WarningToJson(ToolWarning warning)
        {
            var json = new JObject { ["message"] = warning.Message };
            if (warning.ElementIds != null && warning.ElementIds.Count > 0)
            {
                json["elementIds"] = new JArray(warning.ElementIds);
            }
            if (!string.IsNullOrEmpty(warning.Severity) && warning.Severity != "warning")
            {
                json["severity"] = warning.Severity;
            }
            return json;
        }
    }
}
