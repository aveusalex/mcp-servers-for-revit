using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Export
{
    public sealed class ListLinksArgs
    {
        [JsonProperty("kind")]
        [Description("rvt (Revit links), cad (linked and imported DWG/DXF/DGN/SKP...) or all (default).")]
        [EnumValues("rvt", "cad", "all")]
        [DefaultValue("all")]
        public string Kind { get; set; } = "all";
    }

    /// <summary>EXP-08 (list): Revit links and CAD links/imports with status, path and instances.</summary>
    [McpTool("list_links", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "export", Idempotent = true,
        Description = "List the Revit links (type id, name, path, loaded/unloaded/not found, attachment or overlay, " +
                      "nested, instance count) and the CAD files (linked vs imported, path, status, instances and the " +
                      "view owning view-specific ones). Use the linkTypeId with manage_links to reload or unload. Read-only.")]
    public sealed class ListLinksTool : McpTool<ListLinksArgs>
    {
        protected override object Run(ToolContext ctx, ListLinksArgs a)
        {
            var doc = ctx.Doc;
            var kind = (a.Kind ?? "all").ToLowerInvariant();
            object rvt = null, cad = null;

            if (kind != "cad")
            {
                var instances = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
                    .GroupBy(i => i.GetTypeId().GetValue()).ToDictionary(g => g.Key, g => g.ToList());
                rvt = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>()
                    .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(t =>
                    {
                        instances.TryGetValue(t.Id.GetValue(), out var inst);
                        return new
                        {
                            linkTypeId = t.Id.GetValue(),
                            name = t.Name,
                            path = PathOf(t),
                            status = StatusOf(t),
                            loaded = SafeIsLoaded(doc, t),
                            attachment = Safe(() => t.AttachmentType.ToString()),
                            pathType = Safe(() => t.PathType.ToString()),
                            nested = Safe(() => t.IsNestedLink),
                            instanceCount = inst?.Count ?? 0,
                            instanceIds = inst?.Select(i => i.Id.GetValue()).Take(50).ToList() ?? new List<long>()
                        };
                    }).ToList();
            }

            if (kind != "rvt")
            {
                var imports = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().ToList();
                var byType = imports.GroupBy(i => i.GetTypeId().GetValue()).ToDictionary(g => g.Key, g => g.ToList());
                cad = new FilteredElementCollector(doc).OfClass(typeof(CADLinkType)).Cast<CADLinkType>()
                    .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(t =>
                    {
                        byType.TryGetValue(t.Id.GetValue(), out var inst);
                        inst = inst ?? new List<ImportInstance>();
                        bool linked = Safe(() => t.IsExternalFileReference());
                        return new
                        {
                            linkTypeId = t.Id.GetValue(),
                            name = t.Name,
                            mode = linked ? "linked" : "imported",
                            path = linked ? PathOf(t) : null,
                            status = linked ? StatusOf(t) : null,
                            instanceCount = inst.Count,
                            instances = inst.Take(50).Select(i => new
                            {
                                id = i.Id.GetValue(),
                                viewSpecific = i.ViewSpecific,
                                ownerView = i.ViewSpecific && i.OwnerViewId != ElementId.InvalidElementId
                                    ? doc.GetElement(i.OwnerViewId)?.Name
                                    : null,
                                pinned = i.Pinned
                            }).ToList()
                        };
                    }).ToList();
            }

            return new { rvt, cad };
        }

        internal static string PathOf(Element t)
        {
            try
            {
                var r = t.GetExternalFileReference();
                var p = r?.GetAbsolutePath();
                return p == null ? null : ModelPathUtils.ConvertModelPathToUserVisiblePath(p);
            }
            catch
            {
                return null;
            }
        }

        internal static string StatusOf(Element t)
        {
            try { return t.GetExternalFileReference()?.GetLinkedFileStatus().ToString(); }
            catch { return null; }
        }

        private static bool SafeIsLoaded(Document doc, RevitLinkType t)
        {
            try { return RevitLinkType.IsLoaded(doc, t.Id); }
            catch { return false; }
        }

        private static T Safe<T>(Func<T> f)
        {
            try { return f(); }
            catch { return default(T); }
        }
    }
}
