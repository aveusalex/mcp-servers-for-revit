using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Query
{
    public sealed class ListPhasesArgs
    {
        [JsonProperty("includePhaseFilters")]
        [Description("Also list the phase filters and how each shows New/Existing/Demolished/Temporary elements (default true).")]
        [DefaultValue(true)]
        public bool IncludePhaseFilters { get; set; } = true;
    }

    /// <summary>CON-09: phases in order and phase filters.</summary>
    [McpTool("list_phases", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "query", Idempotent = true,
        Description = "List the project phases in chronological order (id, name, order; the last one is where new " +
                      "work normally goes) and the phase filters with the presentation of each phase status (New, " +
                      "Existing, Demolished, Temporary: ShowByCategory, ShowOverriden or DontShow). Phase names are " +
                      "translated by Revit (\"Existing\" / \"Existente\"): pass ids to other tools when in doubt. Read-only.")]
    public sealed class ListPhasesTool : McpTool<ListPhasesArgs>
    {
        protected override object Run(ToolContext ctx, ListPhasesArgs args)
        {
            var doc = ctx.Doc;
            var phases = doc.Phases.Cast<Phase>().Select((p, i) => new
            {
                id = p.Id.GetValue(),
                uniqueId = p.UniqueId,
                name = p.Name,
                order = i,
                isLast = i == doc.Phases.Size - 1
            }).ToList();

            List<object> filters = null;
            if (args.IncludePhaseFilters)
            {
                filters = new List<object>();
                foreach (var pf in new FilteredElementCollector(doc).OfClass(typeof(PhaseFilter)).Cast<PhaseFilter>().OrderBy(f => f.Name))
                {
                    var d = new Dictionary<string, object> { ["id"] = pf.Id.GetValue(), ["name"] = pf.Name };
                    foreach (var (key, status) in new[]
                             {
                                 ("new", ElementOnPhaseStatus.New), ("existing", ElementOnPhaseStatus.Existing),
                                 ("demolished", ElementOnPhaseStatus.Demolished), ("temporary", ElementOnPhaseStatus.Temporary)
                             })
                    {
                        try { d[key] = pf.GetPhaseStatusPresentation(status).ToString(); }
                        catch { d[key] = null; }
                    }
                    filters.Add(d);
                }
            }

            return new { phases, phaseFilters = filters };
        }
    }
}
