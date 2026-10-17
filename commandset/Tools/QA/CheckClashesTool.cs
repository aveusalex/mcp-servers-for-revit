using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.QA.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.QA
{
    public sealed class ClashSet
    {
        [JsonProperty("categories")]
        [Description("Categories in any language (OST_Ducts / Ducts / Dutos). With link, taken from the linked model.")]
        public List<string> Categories { get; set; }

        [JsonProperty("elementIds")]
        [Description("Explicit elements: id, UniqueId or \"linkInstanceId:elementId\" for linked elements.")]
        public List<string> ElementIds { get; set; }

        [JsonProperty("link")]
        [Description("Revit link instance id or name: take the categories from that linked model instead of the host.")]
        public string Link { get; set; }
    }

    public sealed class CheckClashesArgs
    {
        [JsonProperty("setA")]
        [Required]
        [Description("First set of elements.")]
        public ClashSet SetA { get; set; }

        [JsonProperty("setB")]
        [Description("Second set. Omit to check set A against itself.")]
        public ClashSet SetB { get; set; }

        [JsonProperty("mode")]
        [Description("solid (default): real intersection volume of the solids; bbox: bounding boxes only (fast, many false positives).")]
        [EnumValues("solid", "bbox")]
        [DefaultValue("solid")]
        public string Mode { get; set; } = "solid";

        [JsonProperty("tolerance")]
        [Description("Grow the bounding boxes by this much before comparing (bbox mode and pre-filter). Default 0.")]
        [Unit("mm")]
        [DefaultValue(0)]
        public double Tolerance { get; set; }

        [JsonProperty("minVolume")]
        [Description("solid mode: ignore intersections smaller than this (default 0.000001 m3 = 1 cm3).")]
        [Unit("m3")]
        [DefaultValue(0.000001)]
        public double MinVolume { get; set; } = 0.000001;

        [JsonProperty("ignoreJoined")]
        [Description("Skip pairs joined with Join Geometry and host/hosted pairs (door in its wall). Default true.")]
        [DefaultValue(true)]
        public bool IgnoreJoined { get; set; } = true;

        [JsonProperty("maxPairs")]
        [Description("Stop after this many clashes, 1-5000 (default 200).")]
        [System.ComponentModel.DataAnnotations.Range(1, 5000)]
        [DefaultValue(200)]
        public int MaxPairs { get; set; } = 200;

        [JsonProperty("maxElementsPerSet")]
        [Description("Refuse sets larger than this, 1-50000 (default 10000); narrow the categories or use a link/elementIds.")]
        [System.ComponentModel.DataAnnotations.Range(1, 50000)]
        [DefaultValue(10000)]
        public int MaxElementsPerSet { get; set; } = 10000;

        [JsonProperty("timeBudgetMs")]
        [Description("Stop after this many milliseconds and return what was found (truncated), 1000-600000 (default 30000).")]
        [System.ComponentModel.DataAnnotations.Range(1000, 600000)]
        [DefaultValue(30000)]
        public int TimeBudgetMs { get; set; } = 30000;
    }

    /// <summary>
    /// QA-08: real clash detection between two sets (or one set against itself), host and linked models. Bounding
    /// boxes in host coordinates pre-filter the pairs; solid mode then intersects the solids (linked solids moved by
    /// the link's transform) with BooleanOperationsUtils and keeps volumes above minVolume.
    /// </summary>
    [McpTool("check_clashes", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "qa", Idempotent = true,
        TimeoutSeconds = 300,
        Description = "Detect clashes between set A and set B (or A against itself): each set by categories, explicit " +
                      "element ids and/or a Revit link. solid mode computes the real intersection volume (m3) and centre " +
                      "(mm) of each clashing pair; bbox mode only compares bounding boxes. Joined and host/hosted pairs are " +
                      "skipped by default. Bounded by maxPairs and timeBudgetMs (truncated says so). Read-only.")]
    public sealed class CheckClashesTool : McpTool<CheckClashesArgs>
    {
        private sealed class Item
        {
            public Element Element;
            public Document Doc;
            public Transform Transform;
            public RevitLinkInstance Link;
            public XYZ Min, Max;
            public List<Solid> Solids;
            public string Key;
        }

        protected override void Validate(CheckClashesArgs a)
        {
            void Check(ClashSet s, string name)
            {
                if (s == null) return;
                if ((s.Categories?.Count ?? 0) == 0 && (s.ElementIds?.Count ?? 0) == 0)
                    throw new ToolException("INVALID_ARGUMENTS", $"{name} needs categories or elementIds.");
            }
            if (a.SetA == null) throw new ToolException("INVALID_ARGUMENTS", "setA is required.");
            Check(a.SetA, "setA");
            Check(a.SetB, "setB");
            if (a.Tolerance < 0 || a.MinVolume < 0) throw new ToolException("INVALID_ARGUMENTS", "tolerance and minVolume can't be negative.");
        }

        protected override object Run(ToolContext ctx, CheckClashesArgs a)
        {
            var budget = new TimeBudget(a.TimeBudgetMs);
            bool solid = !string.Equals(a.Mode, "bbox", StringComparison.OrdinalIgnoreCase);
            double tol = UnitPolicy.ToInternal(a.Tolerance, UnitSpec.Length);
            double minVol = UnitPolicy.ToInternal(a.MinVolume, UnitSpec.Volume);

            var setA = Collect(ctx, a.SetA, "setA", a.MaxElementsPerSet);
            bool self = a.SetB == null;
            var setB = self ? setA : Collect(ctx, a.SetB, "setB", a.MaxElementsPerSet);

            var sortedB = setB.Select((it, i) => (it, i)).OrderBy(x => x.it.Min.X).ToList();
            var minXs = sortedB.Select(x => x.it.Min.X).ToList();
            var indexA = self ? setA.Select((it, i) => (it, i)).ToDictionary(x => x.it, x => x.i) : null;

            var clashes = new List<object>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int candidates = 0, checkedPairs = 0, booleanFailures = 0, noSolid = 0, skippedRelated = 0;
            string stopReason = null;

            for (int ai = 0; ai < setA.Count && stopReason == null; ai++)
            {
                var A = setA[ai];
                int upper = UpperBound(minXs, A.Max.X + tol);
                for (int k = 0; k < upper; k++)
                {
                    if (budget.Exceeded) { stopReason = "timeBudgetMs reached"; break; }
                    var (B, bi) = sortedB[k];
                    if (self && bi <= ai) continue;
                    if (ReferenceEquals(A, B) || A.Key == B.Key) continue;
                    if (B.Max.X < A.Min.X - tol || B.Max.Y < A.Min.Y - tol || B.Min.Y > A.Max.Y + tol ||
                        B.Max.Z < A.Min.Z - tol || B.Min.Z > A.Max.Z + tol) continue;
                    var pairKey = string.CompareOrdinal(A.Key, B.Key) < 0 ? A.Key + "|" + B.Key : B.Key + "|" + A.Key;
                    if (!seen.Add(pairKey)) continue;
                    candidates++;

                    if (a.IgnoreJoined && Related(A, B))
                    {
                        skippedRelated++;
                        continue;
                    }

                    if (!solid)
                    {
                        clashes.Add(Pair(A, B, null, Center(A, B), "bbox"));
                    }
                    else
                    {
                        var sa = SolidsOf(A);
                        var sb = SolidsOf(B);
                        if (sa.Count == 0 || sb.Count == 0)
                        {
                            noSolid++;
                            continue;
                        }
                        checkedPairs++;
                        double volume = 0;
                        XYZ center = null;
                        double best = 0;
                        bool failed = false;
                        foreach (var x in sa)
                        foreach (var y in sb)
                        {
                            try
                            {
                                var inter = BooleanOperationsUtils.ExecuteBooleanOperation(x, y, BooleanOperationsType.Intersect);
                                if (inter == null || inter.Volume <= 0) continue;
                                volume += inter.Volume;
                                if (inter.Volume > best)
                                {
                                    best = inter.Volume;
                                    try { center = inter.ComputeCentroid(); }
                                    catch { center = null; }
                                }
                            }
                            catch
                            {
                                failed = true;
                            }
                        }
                        if (volume > minVol && volume > 0)
                            clashes.Add(Pair(A, B, volume, center ?? Center(A, B), "solid"));
                        else if (failed && volume <= 0)
                        {
                            booleanFailures++;
                            clashes.Add(Pair(A, B, null, Center(A, B), "bbox-fallback (Revit could not intersect these solids)"));
                        }
                    }

                    if (clashes.Count >= a.MaxPairs)
                    {
                        stopReason = "maxPairs reached";
                        break;
                    }
                }
                if (budget.Exceeded && stopReason == null) stopReason = "timeBudgetMs reached";
            }

            return new
            {
                mode = solid ? "solid" : "bbox",
                setACount = setA.Count,
                setBCount = self ? (int?)null : setB.Count,
                selfCheck = self,
                candidatePairs = candidates,
                solidPairsChecked = solid ? checkedPairs : (int?)null,
                skippedJoinedOrHosted = skippedRelated,
                pairsWithoutSolids = noSolid,
                booleanFailures,
                clashCount = clashes.Count,
                clashes,
                truncated = stopReason != null,
                stopReason,
                elapsedMs = budget.ElapsedMs
            };
        }

        private static int UpperBound(List<double> sorted, double value)
        {
            int lo = 0, hi = sorted.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (sorted[mid] <= value) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        private static object Pair(Item A, Item B, double? volume, XYZ center, string method) => new
        {
            a = Describe(A),
            b = Describe(B),
            volume = volume.HasValue ? Math.Round(UnitPolicy.FromInternal(volume.Value, UnitSpec.Volume, out _), 6) : (double?)null,
            volumeUnit = volume.HasValue ? "m3" : null,
            center = center == null ? null : new { x = QaSupport.Mm(center.X), y = QaSupport.Mm(center.Y), z = QaSupport.Mm(center.Z) },
            method
        };

        private static object Describe(Item it) => new
        {
            id = it.Element.Id.GetValue(),
            reference = it.Key,
            linkInstanceId = it.Link?.Id.GetValue(),
            category = it.Element.Category?.Name,
            name = QaSupport.SafeName(it.Element)
        };

        private static XYZ Center(Item A, Item B)
        {
            var min = new XYZ(Math.Max(A.Min.X, B.Min.X), Math.Max(A.Min.Y, B.Min.Y), Math.Max(A.Min.Z, B.Min.Z));
            var max = new XYZ(Math.Min(A.Max.X, B.Max.X), Math.Min(A.Max.Y, B.Max.Y), Math.Min(A.Max.Z, B.Max.Z));
            return (min + max) / 2;
        }

        private static bool Related(Item A, Item B)
        {
            if (A.Link != null || B.Link != null || !ReferenceEquals(A.Doc, B.Doc)) return false;
            try
            {
                if (JoinGeometryUtils.AreElementsJoined(A.Doc, A.Element, B.Element)) return true;
            }
            catch
            {
                // not joinable
            }
            return HostedBy(A.Element, B.Element) || HostedBy(B.Element, A.Element);
        }

        private static bool HostedBy(Element child, Element host)
        {
            if (!(child is FamilyInstance fi)) return false;
            if (fi.Host != null && fi.Host.Id == host.Id) return true;
            return fi.SuperComponent != null && fi.SuperComponent.Id == host.Id;
        }

        private List<Item> Collect(ToolContext ctx, ClashSet set, string name, int max)
        {
            var doc = ctx.Doc;
            var items = new List<Item>();
            var keys = new HashSet<string>(StringComparer.Ordinal);

            void Add(Element e, Document d, RevitLinkInstance link)
            {
                if (e == null || e is ElementType || e.Category == null) return;
                var key = link == null
                    ? e.Id.GetValue().ToString(CultureInfo.InvariantCulture)
                    : link.Id.GetValue().ToString(CultureInfo.InvariantCulture) + ":" + e.Id.GetValue().ToString(CultureInfo.InvariantCulture);
                if (!keys.Add(key)) return;
                BoundingBoxXYZ bb;
                try { bb = e.get_BoundingBox(null); }
                catch { bb = null; }
                if (bb == null) return;
                var t = link?.GetTotalTransform();
                var (min, max2) = Bounds(bb, t);
                items.Add(new Item { Element = e, Doc = d, Transform = t, Link = link, Min = min, Max = max2, Key = key });
                if (items.Count > max)
                    throw new ToolException("SET_TOO_LARGE", $"{name} has more than {max} elements.",
                        "Narrow the categories, use elementIds, or raise maxElementsPerSet (slower).");
            }

            RevitLinkInstance setLink = null;
            if (!string.IsNullOrWhiteSpace(set.Link)) setLink = ResolveLink(doc, set.Link);

            var cats = ctx.Services.Categories.ResolveAll(set.Categories);
            if (cats.Count > 0)
            {
                var src = setLink == null ? doc : setLink.GetLinkDocument();
                if (src == null)
                    throw new ToolException("LINK_NOT_LOADED", $"The link '{set.Link}' is not loaded.", "Reload it (manage_links) and retry.", "manage_links");
                foreach (var e in new FilteredElementCollector(src).WhereElementIsNotElementType()
                             .WherePasses(new ElementMulticategoryFilter(cats.Select(c => c.Id).ToList())).ToElements())
                    Add(e, src, setLink);
            }
            foreach (var r in set.ElementIds ?? new List<string>())
            {
                var res = ctx.Services.Lookup.Resolve(r);
                Add(res.Element, res.Document, res.Link);
            }
            if (items.Count == 0)
                throw new ToolException("SET_EMPTY", $"{name} matched no element with geometry.", "Check the categories / ids (and that the link is loaded).");
            return items;
        }

        private static RevitLinkInstance ResolveLink(Document doc, string reference)
        {
            var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList();
            if (long.TryParse(reference.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                var byId = links.FirstOrDefault(l => l.Id.GetValue() == id || l.GetTypeId().GetValue() == id);
                if (byId != null) return byId;
            }
            var key = TextMatch.Key(reference);
            var matches = links.Where(l =>
            {
                var typeName = doc.GetElement(l.GetTypeId())?.Name ?? string.Empty;
                return TextMatch.Key(l.Name).Contains(key) || TextMatch.Key(typeName).Contains(key);
            }).ToList();
            if (matches.Count == 1) return matches[0];
            if (matches.Count > 1)
                throw new ToolException("AMBIGUOUS_NAME", $"'{reference}' matches {matches.Count} link instances.",
                    "Use the link instance id: " + string.Join(", ", matches.Select(l => $"{l.Id.GetValue()} ({l.Name})")));
            throw new ToolException("LINK_NOT_FOUND", $"No Revit link matches '{reference}'.", "Use list_links to see the links.", "list_links");
        }

        private static (XYZ min, XYZ max) Bounds(BoundingBoxXYZ bb, Transform link)
        {
            var t = bb.Transform ?? Transform.Identity;
            if (link != null) t = link.Multiply(t);
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            foreach (var x in new[] { bb.Min.X, bb.Max.X })
            foreach (var y in new[] { bb.Min.Y, bb.Max.Y })
            foreach (var z in new[] { bb.Min.Z, bb.Max.Z })
            {
                var p = t.OfPoint(new XYZ(x, y, z));
                minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); minZ = Math.Min(minZ, p.Z);
                maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); maxZ = Math.Max(maxZ, p.Z);
            }
            return (new XYZ(minX, minY, minZ), new XYZ(maxX, maxY, maxZ));
        }

        private static List<Solid> SolidsOf(Item it)
        {
            if (it.Solids != null) return it.Solids;
            var list = new List<Solid>();
            try
            {
                var geo = it.Element.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine, IncludeNonVisibleObjects = false });
                Collect(geo, list);
            }
            catch
            {
                // elements without geometry
            }
            if (it.Transform != null && !it.Transform.IsIdentity)
            {
                var moved = new List<Solid>();
                foreach (var s in list)
                {
                    try { moved.Add(SolidUtils.CreateTransformed(s, it.Transform)); }
                    catch { /* skip */ }
                }
                list = moved;
            }
            it.Solids = list;
            return list;
        }

        private static void Collect(GeometryElement geo, List<Solid> into)
        {
            if (geo == null) return;
            foreach (var obj in geo)
            {
                switch (obj)
                {
                    case Solid s when s.Volume > 1e-9 && s.Faces.Size > 0:
                        into.Add(s);
                        break;
                    case GeometryInstance gi:
                        Collect(gi.GetInstanceGeometry(), into);
                        break;
                    case GeometryElement ge:
                        Collect(ge, into);
                        break;
                }
            }
        }
    }
}
