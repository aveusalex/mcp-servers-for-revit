using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMCPCommandSet.Tools.Core
{
    public sealed class ExecuteBatchStep
    {
        [JsonProperty("tool")]
        [Required]
        [Description("Name of a batchable framework tool, e.g. set_elements_pinned.")]
        public string Tool { get; set; }

        [JsonProperty("args")]
        [Description("Arguments of that tool (without document/dryRun). A string \"$steps[i].path\" is replaced by a " +
                     "value from the result of an earlier step i, e.g. \"$steps[0].changes.created[0]\" or \"$steps[1].data.viewId\".")]
        public JObject Args { get; set; }
    }

    /// <remarks>No Revit dependency: the framework tests check it against the contract.</remarks>
    public sealed class ExecuteBatchArgs
    {
        [JsonProperty("steps")]
        [Required]
        [MinLength(1)]
        [MaxLength(200)]
        [Description("Steps to run in order, all against the same document.")]
        public List<ExecuteBatchStep> Steps { get; set; }

        [JsonProperty("stopOnError")]
        [DefaultValue(true)]
        [Description("When true (default), the first failing step rolls the whole batch back. When false, failing " +
                     "steps are skipped (their own changes rolled back) and the others are kept.")]
        public bool StopOnError { get; set; } = true;
    }
}
