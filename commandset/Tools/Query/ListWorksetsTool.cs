using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;

namespace RevitMCPCommandSet.Tools.Query
{
    public sealed class ListWorksetsArgs
    {
        [JsonProperty("includeSystem")]
        [Description("Also list system worksets (families, standards, views), default false.")]
        [DefaultValue(false)]
        public bool IncludeSystem { get; set; }

        [JsonProperty("includeElementCounts")]
        [Description("Count the elements in each user workset (slow on big models; default false).")]
        [DefaultValue(false)]
        public bool IncludeElementCounts { get; set; }
    }

    /// <summary>CON-10: worksets with status.</summary>
    [McpTool("list_worksets", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "query", Idempotent = true,
        Description = "List the worksets of a workshared model: id, name, kind, whether it is the active workset, open, " +
                      "editable by this user, owner, default visibility, optionally element counts. A model without " +
                      "worksharing returns isWorkshared=false and an empty list (not an error). Read-only.")]
    public sealed class ListWorksetsTool : McpTool<ListWorksetsArgs>
    {
        protected override object Run(ToolContext ctx, ListWorksetsArgs args)
        {
            var doc = ctx.Doc;
            if (!doc.IsWorkshared)
                return new { isWorkshared = false, activeWorksetId = (long?)null, items = new List<object>(), total = 0 };

            var table = doc.GetWorksetTable();
            var activeId = table.GetActiveWorksetId();
            var collector = new FilteredWorksetCollector(doc);
            var worksets = args.IncludeSystem
                ? collector.ToWorksets().ToList()
                : collector.OfKind(WorksetKind.UserWorkset).ToWorksets().ToList();

            var items = worksets.OrderBy(w => w.Kind.ToString()).ThenBy(w => w.Name).Select(w => new
            {
                id = (long)w.Id.IntegerValue,
                name = w.Name,
                kind = w.Kind.ToString(),
                isActive = w.Id == activeId,
                isOpen = w.IsOpen,
                isEditable = w.IsEditable,
                owner = string.IsNullOrEmpty(w.Owner) ? null : w.Owner,
                isDefaultVisible = w.IsVisibleByDefault,
                elementCount = args.IncludeElementCounts && w.Kind == WorksetKind.UserWorkset
                    ? new FilteredElementCollector(doc).WherePasses(new ElementWorksetFilter(w.Id)).WhereElementIsNotElementType().GetElementCount()
                    : (int?)null
            }).ToList();

            return new { isWorkshared = true, activeWorksetId = (long)activeId.IntegerValue, items, total = items.Count };
        }
    }
}
