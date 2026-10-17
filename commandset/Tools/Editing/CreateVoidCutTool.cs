using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Editing
{
    public sealed class CreateVoidCutArgs
    {
        [JsonProperty("voidElementId", Required = Required.Always)]
        [Description("The cutting family instance: a family with an unattached void (e.g. the office's sink/cooktop cut-out family).")]
        public long VoidElementId { get; set; }

        [JsonProperty("hostIds")]
        [MaxLength(200)]
        [Description("Elements to cut (countertop, wall, floor, millwork piece...). Default: every element whose bounding box touches the void instance.")]
        public List<long> HostIds { get; set; }

        [JsonProperty("operation")]
        [EnumValues("add", "remove")]
        [DefaultValue("add")]
        [Description("add (default): the void cuts the hosts. remove: undo those cuts.")]
        public string Operation { get; set; } = "add";
    }

    /// <summary>FAM-17: cut hosts with a void family instance (InstanceVoidCutUtils).</summary>
    [McpTool("create_void_cut", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "editing",
        Description = "Cut elements with a void family instance (criar vazio de corte: sink or cooktop opening in a " +
                      "countertop, niche in a wall, passage in a millwork panel). The instance must come from a family " +
                      "with an unattached void ('Cut with Voids When Loaded'). Give hostIds, or let it find every element " +
                      "whose bounding box touches the void. operation remove undoes the cuts. Each host reports done / " +
                      "already / not_applicable with the reason. Undo: Ctrl+Z.")]
    public sealed class CreateVoidCutTool : McpTool<CreateVoidCutArgs>
    {
        protected override object Run(ToolContext ctx, CreateVoidCutArgs a)
        {
            var doc = ctx.Doc;
            bool add = !string.Equals(a.Operation, "remove", StringComparison.OrdinalIgnoreCase);
            var cutter = ctx.Services.Lookup.Element(a.VoidElementId);
            if (!InstanceVoidCutUtils.IsVoidInstanceCuttingElement(cutter))
                throw new ToolException("NOT_A_VOID_INSTANCE", $"Element {a.VoidElementId} ({cutter.Category?.Name}) is not a family instance with an unattached void.",
                    "Use a family whose void is not attached to its own solids and has 'Cut with Voids When Loaded' on (edit the family if needed).");

            List<Element> hosts;
            bool auto = a.HostIds == null || a.HostIds.Count == 0;
            if (!auto) hosts = a.HostIds.Distinct().Where(id => id != a.VoidElementId).Select(id => ctx.Services.Lookup.Element(id)).ToList();
            else if (!add) hosts = InstanceVoidCutUtils.GetElementsBeingCut(cutter).Select(doc.GetElement).Where(e => e != null).ToList();
            else
            {
                var bb = cutter.get_BoundingBox(null)
                         ?? throw new ToolException("NO_GEOMETRY", $"Element {a.VoidElementId} has no bounding box; pass hostIds.");
                var filter = new BoundingBoxIntersectsFilter(new Outline(bb.Min, bb.Max));
                hosts = new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(filter)
                    .Where(e => e.Id != cutter.Id && e.Category != null && e.Category.CategoryType == CategoryType.Model)
                    .Where(InstanceVoidCutUtils.CanBeCutWithVoid).ToList();
                if (hosts.Count == 0)
                    throw new ToolException("NOTHING_TO_CUT", "No element that can be cut touches the void instance.", "Move the void onto the host or pass hostIds.");
            }

            var results = new List<object>();
            int done = 0;
            foreach (var host in hosts)
            {
                string status, reason = null;
                try
                {
                    if (add)
                    {
                        if (!InstanceVoidCutUtils.CanBeCutWithVoid(host)) { status = "not_applicable"; reason = $"{host.Category?.Name} can't be cut by a void instance."; }
                        else if (InstanceVoidCutUtils.InstanceVoidCutExists(host, cutter)) status = "already";
                        else { InstanceVoidCutUtils.AddInstanceVoidCut(doc, host, cutter); status = "done"; done++; }
                    }
                    else
                    {
                        if (!InstanceVoidCutUtils.InstanceVoidCutExists(host, cutter)) status = "already";
                        else { InstanceVoidCutUtils.RemoveInstanceVoidCut(doc, host, cutter); status = "done"; done++; }
                    }
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    status = "not_applicable";
                    reason = ex.Message;
                }
                results.Add(new { hostId = host.Id.GetValue(), category = host.Category?.Name, status, reason });
            }
            doc.Regenerate();
            return new
            {
                operation = add ? "add" : "remove",
                voidElementId = a.VoidElementId,
                hostsFoundAutomatically = auto,
                results,
                elementsBeingCut = InstanceVoidCutUtils.GetElementsBeingCut(cutter).Select(i => i.GetValue()).OrderBy(i => i).ToList(),
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) +
                                $"{done} host(s) {(add ? "cut by" : "released from")} void {a.VoidElementId}."
            };
        }
    }
}
