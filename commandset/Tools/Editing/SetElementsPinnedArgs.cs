using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Newtonsoft.Json;

namespace RevitMCPCommandSet.Tools.Editing
{
    /// <remarks>No Revit dependency: the framework tests check it against the contract.</remarks>
    public sealed class SetElementsPinnedArgs
    {
        [JsonProperty("elementIds")]
        [Required]
        [MinLength(1)]
        [MaxLength(5000)]
        [Description("Ids of the elements to pin or unpin.")]
        public List<long> ElementIds { get; set; }

        [JsonProperty("pinned")]
        [Required]
        [Description("true pins the elements (they can't be moved or deleted by accident); false unpins them.")]
        public bool? Pinned { get; set; }
    }
}
