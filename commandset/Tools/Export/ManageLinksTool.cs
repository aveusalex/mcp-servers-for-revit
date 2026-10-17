using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Export
{
    public sealed class ManageLinksArgs
    {
        [JsonProperty("operation")]
        [Required]
        [Description("reload (reload from the saved path) or unload.")]
        [EnumValues("reload", "unload")]
        public string Operation { get; set; }

        [JsonProperty("linkTypeIds")]
        [Required]
        [MinLength(1)]
        [MaxLength(100)]
        [Description("Revit link type ids (linkTypeId from list_links).")]
        public List<long> LinkTypeIds { get; set; }
    }

    /// <summary>
    /// EXP-08 (reload/unload of Revit links). Revit refuses these inside a transaction and they are outside the undo
    /// stack, so the tool runs self-transacted and honours dryRun itself.
    /// </summary>
    [McpTool("manage_links", Kind = ToolKind.Write, Risk = Risk.Medium, Toolset = "export", Batchable = false,
        TimeoutSeconds = 600,
        Description = "Reload or unload Revit links by link type id (from list_links). Reload re-reads the file from its " +
                      "saved path (fails with the reason if the file is missing); unload removes the link's geometry " +
                      "from the session until reloaded. Neither can be undone with Ctrl+Z: undo an unload with reload. " +
                      "dryRun lists what would happen. CAD links are listed by list_links but not managed here.")]
    public sealed class ManageLinksTool : McpTool<ManageLinksArgs>, ISelfTransactedTool
    {
        protected override object Run(ToolContext ctx, ManageLinksArgs a)
        {
            var doc = ctx.Doc;
            bool reload = string.Equals(a.Operation, "reload", StringComparison.OrdinalIgnoreCase);
            var types = new List<RevitLinkType>();
            foreach (var id in a.LinkTypeIds.Distinct())
            {
                var e = doc.GetElement(id.ToRevitElementId());
                if (e is RevitLinkInstance inst) e = doc.GetElement(inst.GetTypeId());
                if (!(e is RevitLinkType t))
                    throw new ToolException("LINK_NOT_FOUND", $"{id} is not a Revit link type (or instance) in the target document.",
                        "Use list_links to get linkTypeId values.", "list_links");
                if (t.IsNestedLink)
                    throw new ToolException("NESTED_LINK", $"'{t.Name}' is a nested link; manage it from its parent link's file.");
                if (!types.Any(x => x.Id == t.Id)) types.Add(t);
            }

            if (ctx.DryRun)
            {
                return new
                {
                    operation = reload ? "reload" : "unload",
                    wouldAffect = types.Select(t => new { linkTypeId = t.Id.GetValue(), name = t.Name, path = ListLinksTool.PathOf(t), status = ListLinksTool.StatusOf(t) }).ToList()
                };
            }

            if (doc.IsReadOnly)
                throw new ToolException(ToolErrorCodes.DocumentReadOnly, $"'{doc.Title}' is read-only.");
            if (doc.IsModifiable)
                throw new ToolException(ToolErrorCodes.DocumentBusy, $"'{doc.Title}' has an open transaction.",
                    "Ask the user to finish the current command in Revit, then retry.");

            var results = new List<LinkResult>();
            foreach (var t in types)
            {
                try
                {
                    if (reload)
                    {
                        var r = t.Reload();
                        var status = r?.LoadResult.ToString();
                        bool ok = r != null && r.LoadResult == LinkLoadResultType.LinkLoaded;
                        results.Add(new LinkResult { linkTypeId = t.Id.GetValue(), name = t.Name, ok = ok, result = status, path = ListLinksTool.PathOf(t) });
                    }
                    else
                    {
                        t.Unload(null);
                        results.Add(new LinkResult { linkTypeId = t.Id.GetValue(), name = t.Name, ok = true, result = "Unloaded", path = ListLinksTool.PathOf(t) });
                    }
                }
                catch (Exception ex)
                {
                    results.Add(new LinkResult { linkTypeId = t.Id.GetValue(), name = t.Name, ok = false, result = ex.Message, path = ListLinksTool.PathOf(t) });
                }
            }

            int okCount = results.Count(r => r.ok);
            if (okCount == 0)
                throw new ToolException(reload ? "LINK_RELOAD_FAILED" : "LINK_UNLOAD_FAILED",
                    $"No link was {(reload ? "reloaded" : "unloaded")}: {JsonConvert.SerializeObject(results)}",
                    reload ? "Check that the linked files still exist at their saved paths (list_links shows the path and status)." : null);
            return new { operation = reload ? "reload" : "unload", succeeded = okCount, failed = results.Count - okCount, results };
        }
    }

    internal sealed class LinkResult
    {
        public long linkTypeId { get; set; }
        public string name { get; set; }
        public bool ok { get; set; }
        public string result { get; set; }
        public string path { get; set; }
    }
}
