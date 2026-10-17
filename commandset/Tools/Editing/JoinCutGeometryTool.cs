using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Editing.M1Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Editing
{
    public sealed class ElementPair
    {
        [JsonProperty("a", Required = Required.Always)]
        [Description("First element. For cut/uncut: the element that IS CUT (e.g. the wall).")]
        public long A { get; set; }

        [JsonProperty("b", Required = Required.Always)]
        [Description("Second element. For cut/uncut: the CUTTING element (e.g. a void/solid family instance).")]
        public long B { get; set; }
    }

    public sealed class JoinCutGeometryArgs
    {
        [JsonProperty("operation", Required = Required.Always)]
        [Required]
        [EnumValues("join", "unjoin", "switchOrder", "cut", "uncut")]
        [Description("join / unjoin geometry, switchOrder (which element cuts the other in a join), cut / uncut (b cuts a: a family with unattached voids cuts with its voids, otherwise solid-solid cut).")]
        public string Operation { get; set; }

        [JsonProperty("pairs")]
        [MaxLength(1000)]
        [Description("Explicit pairs {a, b}.")]
        public List<ElementPair> Pairs { get; set; }

        [JsonProperty("elementIds")]
        [MaxLength(300)]
        [Description("Alternative to pairs: every pair among these elements whose bounding boxes touch (join/unjoin/switchOrder only; at most 2000 pairs).")]
        public List<long> ElementIds { get; set; }
    }

    /// <summary>EDT-12: join/unjoin/switch join order, and solid/void cuts between elements.</summary>
    [McpTool("join_cut_geometry", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "editing", TimeoutSeconds = 120,
        Description = "Join or unjoin the geometry of element pairs (walls, floors, columns, beams...), switch which one " +
                      "cuts the other, or cut / uncut (b cuts a: void cut when b is a family with unattached voids, " +
                      "otherwise solid-solid cut). Give pairs {a,b} or elementIds (all touching pairs, join/unjoin/" +
                      "switchOrder only). Each pair reports done / already / not_applicable with Revit's reason; one bad " +
                      "pair does not undo the others. Undo: Ctrl+Z.")]
    public sealed class JoinCutGeometryTool : McpTool<JoinCutGeometryArgs>
    {
        protected override void Validate(JoinCutGeometryArgs a)
        {
            bool hasPairs = a.Pairs != null && a.Pairs.Count > 0;
            bool hasIds = a.ElementIds != null && a.ElementIds.Count > 0;
            if (hasPairs == hasIds) throw new ToolException("INVALID_ARGUMENTS", "Give exactly one of pairs or elementIds.");
            if (hasIds && (a.Operation == "cut" || a.Operation == "uncut"))
                throw new ToolException("INVALID_ARGUMENTS", "cut/uncut need explicit pairs (a = cut element, b = cutting element).");
            if (hasIds && a.ElementIds.Distinct().Count() < 2) throw new ToolException("INVALID_ARGUMENTS", "elementIds needs at least 2 elements.");
            if (hasPairs && a.Pairs.Any(p => p == null || p.A == p.B)) throw new ToolException("INVALID_ARGUMENTS", "Each pair needs two different elements.");
        }

        protected override object Run(ToolContext ctx, JoinCutGeometryArgs a)
        {
            var doc = ctx.Doc;
            var pairs = a.Pairs != null && a.Pairs.Count > 0 ? ExplicitPairs(doc, a.Pairs) : TouchingPairs(doc, a.ElementIds);
            var results = new List<object>();
            int done = 0, already = 0, failed = 0;
            foreach (var (x, y) in pairs)
            {
                string status, message = null;
                using (var sub = new SubTransaction(doc))
                {
                    sub.Start();
                    try
                    {
                        status = Apply(doc, a.Operation, x, y, out message);
                        doc.Regenerate();
                        sub.Commit();
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                    {
                        if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack();
                        status = "not_applicable";
                        message = ex.Message;
                    }
                }
                if (status == "done") done++;
                else if (status == "already") already++;
                else failed++;
                results.Add(new { a = x.Id.GetValue(), b = y.Id.GetValue(), status, message });
            }

            return new
            {
                operation = a.Operation,
                pairCount = pairs.Count,
                doneCount = done,
                alreadyCount = already,
                notApplicableCount = failed,
                pairs = results.Take(1000).ToList(),
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) +
                                $"{a.Operation}: {done} pair(s) changed, {already} already in that state, {failed} not applicable."
            };
        }

        private static string Apply(Document doc, string op, Element a, Element b, out string message)
        {
            message = null;
            switch (op)
            {
                case "join":
                    if (JoinGeometryUtils.AreElementsJoined(doc, a, b)) return "already";
                    JoinGeometryUtils.JoinGeometry(doc, a, b);
                    return "done";
                case "unjoin":
                    if (!JoinGeometryUtils.AreElementsJoined(doc, a, b)) return "already";
                    JoinGeometryUtils.UnjoinGeometry(doc, a, b);
                    return "done";
                case "switchOrder":
                    if (!JoinGeometryUtils.AreElementsJoined(doc, a, b))
                    {
                        message = "The elements are not joined; join them first.";
                        return "not_applicable";
                    }
                    JoinGeometryUtils.SwitchJoinOrder(doc, a, b);
                    message = JoinGeometryUtils.IsCuttingElementInJoin(doc, a, b) ? "a now cuts b." : "b now cuts a.";
                    return "done";
                case "cut":
                    if (InstanceVoidCutUtils.IsVoidInstanceCuttingElement(b) && InstanceVoidCutUtils.CanBeCutWithVoid(a))
                    {
                        if (InstanceVoidCutUtils.InstanceVoidCutExists(a, b)) return "already";
                        InstanceVoidCutUtils.AddInstanceVoidCut(doc, a, b);
                        message = "void cut";
                        return "done";
                    }
                    if (SolidSolidCutUtils.CutExistsBetweenElements(a, b, out bool firstCutsSecond) && !firstCutsSecond) return "already";
                    if (!SolidSolidCutUtils.CanElementCutElement(b, a, out CutFailureReason reason))
                    {
                        message = $"Revit can't make {b.Id.GetValue()} cut {a.Id.GetValue()}: {reason}.";
                        return "not_applicable";
                    }
                    SolidSolidCutUtils.AddCutBetweenSolids(doc, a, b);
                    message = "solid cut";
                    return "done";
                default: // uncut
                {
                    bool removed = false;
                    if (InstanceVoidCutUtils.InstanceVoidCutExists(a, b))
                    {
                        InstanceVoidCutUtils.RemoveInstanceVoidCut(doc, a, b);
                        removed = true;
                    }
                    if (SolidSolidCutUtils.CutExistsBetweenElements(a, b, out _))
                    {
                        SolidSolidCutUtils.RemoveCutBetweenSolids(doc, a, b);
                        removed = true;
                    }
                    return removed ? "done" : "already";
                }
            }
        }

        private static List<(Element, Element)> ExplicitPairs(Document doc, List<ElementPair> pairs)
        {
            var ids = pairs.SelectMany(p => new[] { p.A, p.B }).Distinct().ToList();
            var map = Editing.Support.EditGeometry.Elements(doc, ids).ToDictionary(e => e.Id.GetValue());
            return pairs.Select(p => (map[p.A], map[p.B])).ToList();
        }

        private static List<(Element, Element)> TouchingPairs(Document doc, List<long> ids)
        {
            var elements = Editing.Support.EditGeometry.Elements(doc, ids);
            var boxes = elements.Select(e => (e, box: M1Elements.Box(e))).Where(x => x.box != null).ToList();
            var pairs = new List<(Element, Element)>();
            for (int i = 0; i < boxes.Count; i++)
            for (int j = i + 1; j < boxes.Count; j++)
            {
                if (!M1Elements.BoxesOverlap(boxes[i].box, boxes[j].box, 1e-3)) continue;
                pairs.Add((boxes[i].e, boxes[j].e));
                if (pairs.Count > 2000)
                    throw new ToolException("TOO_MANY_PAIRS", "More than 2000 touching pairs; pass fewer elements or explicit pairs.");
            }
            return pairs;
        }
    }
}
