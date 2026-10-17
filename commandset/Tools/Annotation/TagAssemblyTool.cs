using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Annotation.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Annotation
{
    public sealed class TagAssemblyArgs
    {
        [JsonProperty("assemblyId")]
        [Description("Assembly instance id. Default: assemblyName, or the only assembly in the model.")]
        public long? AssemblyId { get; set; }

        [JsonProperty("assemblyName")]
        [Description("Assembly type name (exact or partial) when assemblyId is not given.")]
        public string AssemblyName { get; set; }

        [JsonProperty("views")]
        [Description("Views to annotate (names or ids). Default: every elevation/section/plan of the assembly plus its locked 3D views (unlocked 3D views and schedules are skipped and listed).")]
        public List<string> Views { get; set; }

        [JsonProperty("pieceTags")]
        [DefaultValue(true)]
        [Description("Tag the pieces: one category tag per distinct piece (grouped by Comments, else by type), on the most visible piece of each group. Default true.")]
        public bool PieceTags { get; set; } = true;

        [JsonProperty("materialTags")]
        [DefaultValue(true)]
        [Description("Material tags (identificador de material): one per distinct material. Default true.")]
        public bool MaterialTags { get; set; } = true;

        [JsonProperty("side")]
        [EnumValues("right", "left")]
        [DefaultValue("right")]
        [Description("Side of the object where the tag column goes. Default right.")]
        public string Side { get; set; } = "right";

        [JsonProperty("offsetPaperMm")]
        [Unit("mm")]
        [Range(1, 200)]
        [DefaultValue(20)]
        [Description("Distance from the object to the tag column, in paper mm (scaled by each view's scale). Default 20.")]
        public double OffsetPaperMm { get; set; } = 20;

        [JsonProperty("stepPaperMm")]
        [Unit("mm")]
        [Range(1, 50)]
        [DefaultValue(6)]
        [Description("Vertical spacing between tags in the column, in paper mm. Default 6.")]
        public double StepPaperMm { get; set; } = 6;

        [JsonProperty("leader")]
        [DefaultValue(true)]
        [Description("Draw leaders from the column to the pieces. Default true.")]
        public bool Leader { get; set; } = true;

        [JsonProperty("tagType")]
        [Description("Tag type for the pieces (\"Family: Type\", name or id; e.g. a tag showing Comments). Default: the category's default tag.")]
        public string TagType { get; set; }

        [JsonProperty("materialTagType")]
        [Description("Material tag type (\"Family: Type\", name or id). Default: the document's default material tag.")]
        public string MaterialTagType { get; set; }

        [JsonProperty("clear")]
        [DefaultValue(false)]
        [Description("First delete the tags already on the assembly's pieces in those views (re-annotate after a change). Default false: pieces already tagged are skipped.")]
        public bool Clear { get; set; }
    }

    /// <summary>ANO-15: tag an assembly's views with aligned piece and material tags (millwork detailing).</summary>
    [McpTool("tag_assembly", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "annotation", TimeoutSeconds = 120,
        Description = "Annotate the views of an assembly (e.g. a millwork module) with tags aligned in a column beside " +
                      "the object: one piece tag per distinct piece (grouped by Comments, e.g. 'Lateral', 'Porta', else by " +
                      "type) and one material tag per distinct material, with leaders, ordered top to bottom so leaders " +
                      "don't cross. Works in elevations, sections, plans and locked 3D views (lock_3d_view). clear:true " +
                      "replaces earlier tags. Tags that Revit refuses (e.g. no tag family loaded for the category) are " +
                      "listed, not fatal. Pair with dimension_assembly. Undo: Ctrl+Z.")]
    public sealed class TagAssemblyTool : McpTool<TagAssemblyArgs>
    {
        protected override void Validate(TagAssemblyArgs a)
        {
            if (!a.PieceTags && !a.MaterialTags)
                throw new ToolException("INVALID_ARGUMENTS", "pieceTags and materialTags are both false: nothing to do.");
        }

        protected override object Run(ToolContext ctx, TagAssemblyArgs a)
        {
            var doc = ctx.Doc;
            var asm = AssemblyDetailing.Resolve(doc, a.AssemblyId, a.AssemblyName);
            var skippedViews = new List<SkippedView>();
            var views = AssemblyDetailing.Views(ctx, asm, a.Views, true, skippedViews);
            var members = AssemblyDetailing.Members(doc, asm).Where(e => e.Category != null && !(e is ElementType)).ToList();
            var memberIds = new HashSet<long>(members.Select(e => e.Id.GetValue()));

            FamilySymbol pieceType = string.IsNullOrWhiteSpace(a.TagType) || members.Count == 0 ? null
                : TagSupport.ResolveTagType(ctx, a.TagType, members[0].Category);
            FamilySymbol materialType = string.IsNullOrWhiteSpace(a.MaterialTagType) ? null
                : TagSupport.ResolveMaterialTagType(ctx, a.MaterialTagType);

            var perView = new List<object>();
            var failureMessages = new List<string>();
            int notesUsed = 0;
            int total = 0, removedTotal = 0;
            foreach (var view in views)
            {
                int removed = a.Clear ? ClearTags(doc, view, memberIds) : 0;
                removedTotal += removed;
                var tagged = TagSupport.TaggedKeys(doc, view);
                var visible = members.Where(e => !e.IsHidden(view) && e.get_BoundingBox(view) != null).ToList();
                var (minR, maxR, minU, maxU) = AssemblyDetailing.Extents(view, visible.Count > 0 ? visible : members);

                // What to tag: (element, byMaterial, label), one per group.
                var targets = new List<(Element e, bool byMaterial, string label)>();
                if (a.PieceTags)
                    foreach (var g in visible.GroupBy(PieceLabel))
                        targets.Add((Representative(view, g), false, g.Key));
                if (a.MaterialTags)
                    foreach (var g in visible.Where(e => MainMaterial(e) != null).GroupBy(e => MainMaterial(e).Id.GetValue()))
                        targets.Add((Representative(view, g), true, MainMaterial(g.First()).Name));

                // Column beside the object, top to bottom in the order of each target's height, so leaders don't cross.
                double off = AssemblyDetailing.PaperToModel(view, a.OffsetPaperMm);
                double step = AssemblyDetailing.PaperToModel(view, a.StepPaperMm);
                double column = a.Side == "left" ? minR - off : maxR + off;
                var ordered = targets.OrderByDescending(t => Center(view, t.e).u).ToList();
                double top = Math.Max(maxU, (maxU + minU) / 2 + step * (ordered.Count - 1) / 2);

                var created = new List<object>();
                var skipped = new List<object>();
                var failed = new List<object>();
                for (int i = 0; i < ordered.Count; i++)
                {
                    var t = ordered[i];
                    var key = (t.byMaterial ? TagSupport.MaterialPrefix : string.Empty) + TagSupport.Key(t.e.Id.GetValue(), null);
                    if (tagged.Contains(key))
                    {
                        skipped.Add(new { element = t.e.Id.GetValue(), label = t.label, kind = t.byMaterial ? "material" : "piece", reason = "already tagged" });
                        continue;
                    }
                    var head = AssemblyDetailing.InView(view, column, top - i * step);
                    using (var sub = new SubTransaction(doc))
                    {
                        sub.Start();
                        try
                        {
                            Element tag = TagSupport.CreateTag(doc, view, t.e, null, t.byMaterial ? materialType : pieceType,
                                a.Leader, TagOrientation.Horizontal, head, t.byMaterial);
                            if (tag is IndependentTag it && a.Leader) it.TagHeadPosition = head;
                            if (t.byMaterial && tag is IndependentTag mt && Blank(doc, mt))
                            {
                                // Revit can't resolve the material of some elements (e.g. DirectShape pieces) and the
                                // tag prints "?": write the material name as a text note with a leader instead.
                                var note = MaterialNote(doc, view, t.e, t.label, head, a.Leader);
                                if (note != null)
                                {
                                    doc.Delete(mt.Id);
                                    tag = note;
                                    notesUsed++;
                                }
                            }
                            sub.Commit();
                            tagged.Add(key);
                            total++;
                            string shown = (tag as TextNote)?.Text?.Trim(), on = tag is TextNote ? "textNote" : null;
                            if (tag is IndependentTag shownTag)
                            {
                                try { shown = shownTag.TagText; } catch { /* some tags have no text */ }
                                try { on = shownTag.GetTaggedReferences().Any(r => r.ElementReferenceType == ElementReferenceType.REFERENCE_TYPE_SURFACE) ? "face" : "element"; } catch { }
                            }
                            created.Add(new { tagId = tag.Id.GetValue(), element = t.e.Id.GetValue(), label = t.label, kind = t.byMaterial ? "material" : "piece", text = shown, on });
                        }
                        catch (Exception ex) when (ex is ToolException || ex is Autodesk.Revit.Exceptions.ApplicationException)
                        {
                            if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack();
                            failed.Add(new { element = t.e.Id.GetValue(), label = t.label, kind = t.byMaterial ? "material" : "piece",
                                code = (ex as ToolException)?.Code ?? "REVIT_ERROR", message = ex.Message });
                            failureMessages.Add((t.byMaterial ? "material" : "piece") + " tags: " + ex.Message);
                        }
                    }
                }
                perView.Add(new { view = new { id = view.Id.GetValue(), name = view.Name, viewType = view.ViewType.ToString() },
                    created, skipped, failed, removed });
            }

            // Say once what blocks a whole kind of tag, instead of leaving it buried in every view's failed list.
            if (a.MaterialTags)
            {
                var blank = members.Select(MainMaterial).Where(m => m != null).GroupBy(m => m.Id.GetValue()).Select(g => g.First())
                    .Where(m => string.IsNullOrWhiteSpace(m.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION)?.AsString()))
                    .Select(m => "'" + m.Name + "'").ToList();
                if (blank.Count > 0)
                    ctx.Warnings.Add(new ToolWarning($"Material(s) {string.Join(", ", blank)} have an empty Description, which material tags " +
                                                     "usually print (shows as \"?\"). Fill it with set_parameters on the material."));
            }
            if (notesUsed > 0)
                ctx.Warnings.Add(new ToolWarning($"Revit could not read the material of {notesUsed} piece(s) for a material tag (it would print \"?\"); " +
                                                 "the material name was written as a text note with a leader instead. It does not update if the material changes."));
            if (a.MaterialTags && !members.Any(e => MainMaterial(e) != null))
                ctx.Warnings.Add(new ToolWarning("No piece has a material, so no material tag was placed. Give the pieces a material " +
                                                 "(create_millwork_module material, or assign_material) and run again."));
            foreach (var msg in failureMessages.Distinct())
                ctx.Warnings.Add(new ToolWarning(msg + (msg.StartsWith("piece", StringComparison.Ordinal)
                    ? " Load a tag family for the pieces' category (load_family) or pass tagType."
                    : " Load a material tag family (load_family) or pass materialTagType.")));

            return new
            {
                assembly = new { id = asm.Id.GetValue(), name = asm.AssemblyTypeName },
                views = perView,
                skippedViews,
                tagCount = total,
                removedCount = removedTotal,
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) +
                                $"{total} tag(s) in {perView.Count} view(s) of '{asm.AssemblyTypeName}'" +
                                (removedTotal > 0 ? $" ({removedTotal} earlier tag(s) replaced)." : ".")
            };
        }

        private static bool Blank(Document doc, IndependentTag tag)
        {
            try
            {
                doc.Regenerate();
                var text = tag.TagText?.Trim();
                return string.IsNullOrEmpty(text) || text == "?";
            }
            catch { return false; }
        }

        /// <summary>A text note with the material name at the column, leader to the piece; null if Revit refuses.</summary>
        private static TextNote MaterialNote(Document doc, View view, Element piece, string text, XYZ head, bool leader)
        {
            try
            {
                var note = TextNote.Create(doc, view.Id, head, text, doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType));
                if (leader)
                {
                    var bb = piece.get_BoundingBox(view) ?? piece.get_BoundingBox(null);
                    if (bb != null)
                    {
                        var c = (bb.Min + bb.Max) / 2;
                        var end = AssemblyDetailing.InView(view, c.DotProduct(view.RightDirection), c.DotProduct(view.UpDirection));
                        var l = note.AddLeader(TextNoteLeaderTypes.TNLT_STRAIGHT_L);
                        l.End = end;
                    }
                }
                return note;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return null;
            }
        }

        /// <summary>Piece group label: Comments (the millwork role), else "Family: Type".</summary>
        private static string PieceLabel(Element e)
        {
            var comments = e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
            if (!string.IsNullOrWhiteSpace(comments)) return comments.Trim();
            var type = e.Document.GetElement(e.GetTypeId()) as ElementType;
            return type != null ? LookupService.FullName(type) : e.Category.Name;
        }

        private static Material MainMaterial(Element e)
        {
            try
            {
                var ids = e.GetMaterialIds(false);
                return ids.Select(id => e.Document.GetElement(id) as Material).Where(m => m != null)
                    .OrderByDescending(m => SafeArea(e, m.Id)).FirstOrDefault();
            }
            catch { return null; }
        }

        private static double SafeArea(Element e, ElementId materialId)
        {
            try { return e.GetMaterialArea(materialId, false); }
            catch { return 0; }
        }

        /// <summary>
        /// The group member to point at: among those with a reasonable extent in the view (at least a third of the
        /// largest), the one nearest the viewer, so a leader doesn't land on a piece hidden behind another (e.g. the
        /// back panel behind the doors in a front elevation).
        /// </summary>
        private static Element Representative(View view, IEnumerable<Element> group)
        {
            var scored = group.Select(e =>
            {
                var bb = e.get_BoundingBox(view) ?? e.get_BoundingBox(null);
                if (bb == null) return (e, extent: 0.0, depth: double.MinValue);
                var d = bb.Max - bb.Min;
                double extent = Math.Abs(d.DotProduct(view.RightDirection)) * Math.Abs(d.DotProduct(view.UpDirection));
                double depth = ((bb.Min + bb.Max) / 2).DotProduct(view.ViewDirection); // ViewDirection points at the viewer
                return (e, extent, depth);
            }).ToList();
            double max = scored.Max(s => s.extent);
            return scored.Where(s => s.extent >= max / 3).OrderByDescending(s => s.depth).ThenByDescending(s => s.extent).First().e;
        }

        private static (double r, double u) Center(View view, Element e)
        {
            var bb = e.get_BoundingBox(view) ?? e.get_BoundingBox(null);
            var c = bb == null ? XYZ.Zero : (bb.Min + bb.Max) / 2;
            return (c.DotProduct(view.RightDirection), c.DotProduct(view.UpDirection));
        }

        private static int ClearTags(Document doc, View view, HashSet<long> memberIds)
        {
            var ids = new List<ElementId>();
            foreach (IndependentTag tag in new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)))
            {
                try
                {
                    if (tag.GetTaggedLocalElementIds().Any(id => memberIds.Contains(id.GetValue()))) ids.Add(tag.Id);
                }
                catch
                {
                    // orphaned tag
                }
            }
            if (ids.Count > 0) doc.Delete(ids);
            return ids.Count;
        }
    }
}
