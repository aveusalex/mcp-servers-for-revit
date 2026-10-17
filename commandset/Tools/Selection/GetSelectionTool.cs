using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Selection.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Selection
{
    public sealed class GetSelectionArgs
    {
        [JsonProperty("idsOnly")]
        [DefaultValue(false)]
        [Description("Only ids (fast, for large selections). Default false: id, uniqueId, category, family, type, name, level.")]
        public bool IdsOnly { get; set; }

        [JsonProperty("parameters")]
        [Description("Also return these parameters (names as shown in Revit, pt-BR or English) for each element, as display text.")]
        public List<string> Parameters { get; set; }

        [JsonProperty("limit")]
        [DefaultValue(100)]
        [Description("Page size, 1-1000 (default 100).")]
        public int Limit { get; set; } = 100;

        [JsonProperty("offset")]
        [DefaultValue(0)]
        [Description("Elements to skip, for paging (default 0).")]
        public int Offset { get; set; }
    }

    /// <summary>SEL-02 (framework version of the legacy get_selected_elements): the current selection, paged.</summary>
    [McpTool("get_selection", Kind = ToolKind.UI, Risk = Risk.None, Toolset = "selection", Batchable = false, Idempotent = true,
        Description = "The elements currently selected in Revit (active document), paged (limit/offset, total, up to " +
                      "1000 per page): id, uniqueId, category, family, type, name and level, or idsOnly; optional " +
                      "parameter values. Elements picked inside links are listed separately with the link instance id " +
                      "(Revit 2023+). Also says whether the document is a family and which view is active. Read only.")]
    public sealed class GetSelectionTool : McpTool<GetSelectionArgs>
    {
        protected override object Run(ToolContext ctx, GetSelectionArgs a)
        {
            var uiDoc = SelectionSupport.UiDoc(ctx);
            var doc = ctx.Doc;
            var ids = uiDoc.Selection.GetElementIds().OrderBy(id => id.GetValue()).ToList();
            var page = Paging.Page(ids, a.Limit, a.Offset, 1000);

            var categoryCounts = ids.Select(id => doc.GetElement(id)?.Category?.Name ?? "(sem categoria)")
                .GroupBy(n => n).OrderByDescending(g => g.Count()).Select(g => new { category = g.Key, count = g.Count() }).ToList();

            object items;
            if (a.IdsOnly) items = page.Items.Select(id => id.GetValue()).ToList();
            else
                items = page.Items.Select(id =>
                {
                    var e = doc.GetElement(id);
                    if (e == null) return (object)new { id = id.GetValue(), missing = true };
                    var type = doc.GetElement(e.GetTypeId()) as ElementType;
                    Dictionary<string, string> values = null;
                    if (a.Parameters != null && a.Parameters.Count > 0)
                    {
                        values = new Dictionary<string, string>();
                        foreach (var name in a.Parameters.Where(n => !string.IsNullOrWhiteSpace(n)))
                        {
                            var p = e.LookupParameter(name) ?? type?.LookupParameter(name);
                            values[name] = p == null ? null : (p.StorageType == StorageType.String ? p.AsString() : p.AsValueString());
                        }
                    }
                    var levelId = e.LevelId;
                    return new
                    {
                        id = id.GetValue(),
                        uniqueId = e.UniqueId,
                        category = e.Category?.Name,
                        family = type == null ? null : LookupService.SafeFamilyName(type),
                        type = type?.Name,
                        name = e.Name,
                        level = levelId != null && levelId != ElementId.InvalidElementId ? doc.GetElement(levelId)?.Name : null,
                        groupId = e.GroupId != ElementId.InvalidElementId ? e.GroupId.GetValue() : (long?)null,
                        parameters = values
                    };
                }).ToList();

            List<object> linked = null;
#if REVIT2023_OR_GREATER
            try
            {
                linked = uiDoc.Selection.GetReferences()
                    .Where(r => r.LinkedElementId != ElementId.InvalidElementId)
                    .Take(1000)
                    .Select(r =>
                    {
                        var link = doc.GetElement(r.ElementId) as RevitLinkInstance;
                        var le = link?.GetLinkDocument()?.GetElement(r.LinkedElementId);
                        return (object)new
                        {
                            linkInstanceId = r.ElementId.GetValue(),
                            link = link?.Name,
                            id = r.LinkedElementId.GetValue(),
                            category = le?.Category?.Name,
                            name = le?.Name,
                            reference = r.ElementId.GetValue() + ":" + r.LinkedElementId.GetValue()
                        };
                    }).ToList();
            }
            catch (Exception) { linked = null; }
#endif
            return new
            {
                document = doc.Title,
                isFamilyDocument = doc.IsFamilyDocument,
                activeView = SelectionSupport.ViewRef(uiDoc.ActiveView),
                total = page.Total,
                limit = page.Limit,
                offset = page.Offset,
                truncated = page.Truncated,
                byCategory = categoryCounts,
                items,
                linked
            };
        }
    }
}
