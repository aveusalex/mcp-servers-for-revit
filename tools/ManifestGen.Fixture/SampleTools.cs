using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Framework;

namespace ManifestGen.Fixture
{
    public enum Alignment { Center, Exterior, Interior }

    public class SampleQueryArgs
    {
        [JsonProperty("categories")]
        [Description("Categories to search, by OST_ name, English name or localized name.")]
        [EnumValues("OST_Walls", "OST_Doors", "OST_Windows")]
        public List<string> Categories { get; set; }

        [JsonProperty("limit")]
        [Description("Maximum number of elements to return.")]
        [Range(1, 1000)]
        [DefaultValue(100)]
        public int Limit { get; set; } = 100;

        [JsonProperty("includeTypes")]
        [Description("Also return element types.")]
        [DefaultValue(false)]
        public bool IncludeTypes { get; set; }

        [JsonIgnore]
        public string Internal { get; set; }
    }

    [McpTool("sample_find_elements", Toolset = "query", Idempotent = true,
        Description = "Find elements by category in the target document. Read-only; returns ids and names, paged.")]
    public sealed class SampleFindElementsTool : McpTool<SampleQueryArgs>
    {
        protected override object Run(object ctx, SampleQueryArgs args) => null;
    }

    public class Offset
    {
        [JsonProperty("x")]
        [Description("Offset along X.")]
        [Unit("mm")]
        public double X { get; set; }

        [JsonProperty("y")]
        [Description("Offset along Y.")]
        [Unit("mm")]
        public double Y { get; set; }
    }

    public class SampleMoveArgs
    {
        [JsonProperty("elementIds", Required = Required.Always)]
        [Description("Ids of the elements to move.")]
        public long[] ElementIds { get; set; }

        [JsonProperty("offset")]
        [Required]
        [Description("Translation to apply.")]
        public Offset Offset { get; set; }

        [JsonProperty("rotation")]
        [Description("Rotation about the element origin.")]
        [Unit("deg")]
        [Range(-360.0, 360.0)]
        public double? Rotation { get; set; }

        [JsonProperty("alignment")]
        [Description("Reference line used for walls.")]
        public Alignment Alignment { get; set; }

        [JsonProperty("tags")]
        [Description("Free-form tags stored in the audit log.")]
        public Dictionary<string, string> Tags { get; set; }

        [JsonProperty("extra")]
        [Description("Opaque payload forwarded as-is.")]
        public JObject Extra { get; set; }
    }

    [McpTool("sample_move_elements", Kind = ToolKind.Write, Risk = Risk.Medium, Toolset = "editing", TimeoutSeconds = 120,
        Description = "Move elements by an offset in millimetres. Runs in one transaction; undo with Ctrl+Z in Revit.")]
    public sealed class SampleMoveElementsTool : McpTool<SampleMoveArgs>
    {
        protected override object Run(object ctx, SampleMoveArgs args) => null;
    }

    public class EmptyArgs
    {
    }

    [McpTool("sample_select_elements", Kind = ToolKind.UI, Batchable = false,
        Description = "Select elements in the active view. Needs the target document to be the active one.")]
    public sealed class SampleSelectTool : McpTool<EmptyArgs>
    {
        protected override object Run(object ctx, EmptyArgs args) => null;
    }

    /// <summary>Not a tool: no attribute.</summary>
    public sealed class NotATool : McpTool<EmptyArgs>
    {
        protected override object Run(object ctx, EmptyArgs args) => null;
    }
}
