using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.QA.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.QA
{
    public sealed class AuditFamiliesArgs
    {
        [JsonProperty("sections")]
        [Description("What to audit (default unused, inPlace, duplicateNames, manyTypes): unused, inPlace, duplicateNames, manyTypes, complexity (slow).")]
        [EnumValues("unused", "inPlace", "duplicateNames", "manyTypes", "complexity")]
        public List<string> Sections { get; set; }

        [JsonProperty("categories")]
        [Description("Only families of these categories (any language). Default: all loadable families.")]
        public List<string> Categories { get; set; }

        [JsonProperty("typeCountThreshold")]
        [Description("manyTypes: report families with more types than this, 1-1000 (default 30).")]
        [System.ComponentModel.DataAnnotations.Range(1, 1000)]
        [DefaultValue(30)]
        public int TypeCountThreshold { get; set; } = 30;

        [JsonProperty("limit")]
        [Description("Items listed per section, 1-2000 (default 100); counts are always complete.")]
        [System.ComponentModel.DataAnnotations.Range(1, 2000)]
        [DefaultValue(100)]
        public int Limit { get; set; } = 100;

        [JsonProperty("timeBudgetMs")]
        [Description("complexity: stop sampling after this many milliseconds, 1000-300000 (default 20000).")]
        [System.ComponentModel.DataAnnotations.Range(1000, 300000)]
        [DefaultValue(20000)]
        public int TimeBudgetMs { get; set; } = 20000;
    }

    /// <summary>QA-04: family audit (unused, in-place, near-duplicate names, type bloat, geometric complexity).</summary>
    [McpTool("audit_families", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "qa", Idempotent = true,
        TimeoutSeconds = 180,
        Description = "Audit the loaded families: unused ones (Purge Unused list), in-place families, near-duplicate " +
                      "names (\"Porta 80\" vs \"Porta 80 2\" / \"(1)\" / \"copy\"), families with too many types, and " +
                      "optionally geometric complexity (solids/faces of one sample instance per family, within " +
                      "timeBudgetMs). Counts per section and family ids/names. Read-only: use purge_unused to remove.")]
    public sealed class AuditFamiliesTool : McpTool<AuditFamiliesArgs>
    {
        private static readonly string[] DefaultSections = { "unused", "inPlace", "duplicateNames", "manyTypes" };
        private static readonly Regex CopySuffix = new Regex(@"(\s*\(\d+\)|\s+[2-9]|\s*[-_ ]\s*(copy|copia|cópia)\s*\d*)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        protected override object Run(ToolContext ctx, AuditFamiliesArgs a)
        {
            var doc = ctx.Doc;
            var sections = new HashSet<string>(a.Sections == null || a.Sections.Count == 0 ? DefaultSections : a.Sections, StringComparer.OrdinalIgnoreCase);
            var cats = ctx.Services.Categories.ResolveAll(a.Categories);
            var catIds = new HashSet<long>(cats.Select(c => c.Id.GetValue()));
            var families = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .Where(f => catIds.Count == 0 || (f.FamilyCategory != null && catIds.Contains(f.FamilyCategory.Id.GetValue())))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();

            object F(Family f) => new { id = f.Id.GetValue(), name = f.Name, category = f.FamilyCategory?.Name, types = f.GetFamilySymbolIds().Count };
            object Section(IList<Family> list) => new
            {
                count = list.Count,
                items = list.Take(a.Limit).Select(F).ToList(),
                truncated = list.Count > a.Limit
            };

            var result = new Dictionary<string, object>();
            if (sections.Contains("unused"))
            {
                var unused = new HashSet<long>(UnusedFinder.Find(doc, out var source).Select(id => id.GetValue()));
                var list = families.Where(f => unused.Contains(f.Id.GetValue())).ToList();
                result["unused"] = new { source, count = list.Count, items = list.Take(a.Limit).Select(F).ToList(), truncated = list.Count > a.Limit, next = list.Count > 0 ? "purge_unused" : null };
            }
            if (sections.Contains("inPlace"))
                result["inPlace"] = Section(families.Where(f => f.IsInPlace).ToList());
            if (sections.Contains("manyTypes"))
                result["manyTypes"] = Section(families.Where(f => f.GetFamilySymbolIds().Count > a.TypeCountThreshold)
                    .OrderByDescending(f => f.GetFamilySymbolIds().Count).ToList());
            if (sections.Contains("duplicateNames"))
            {
                // "X 2", "X (1)", "X - copy" count as copies only when a family named exactly "X" exists in the category.
                string CatKey(Family f, string name) => (f.FamilyCategory?.Id.GetValue() ?? 0) + "|" + TextMatch.Key(name);
                var loadable = families.Where(f => !f.IsInPlace).ToList();
                var byName = loadable.GroupBy(f => CatKey(f, f.Name)).ToDictionary(g => g.Key, g => g.First());
                var groups = loadable
                    .Select(f => new { f, baseName = CopySuffix.Replace(f.Name ?? string.Empty, string.Empty).Trim() })
                    .Where(x => x.baseName.Length > 0 && x.baseName != x.f.Name && byName.ContainsKey(CatKey(x.f, x.baseName)))
                    .GroupBy(x => CatKey(x.f, x.baseName))
                    .Select(g => new
                    {
                        original = F(byName[g.Key]),
                        copies = g.Select(x => F(x.f)).ToList()
                    })
                    .ToList();
                result["duplicateNames"] = new { count = groups.Count, groups = groups.Take(a.Limit).ToList(), truncated = groups.Count > a.Limit };
            }
            if (sections.Contains("complexity"))
                result["complexity"] = Complexity(doc, families, a);

            return new
            {
                familyCount = families.Count,
                inPlaceCount = families.Count(f => f.IsInPlace),
                sections = result
            };
        }

        private static object Complexity(Document doc, List<Family> families, AuditFamiliesArgs a)
        {
            var budget = new TimeBudget(a.TimeBudgetMs);
            var bySymbol = new Dictionary<long, FamilyInstance>();
            foreach (var fi in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
            {
                var sid = fi.Symbol?.Id.GetValue() ?? 0;
                if (!bySymbol.ContainsKey(sid)) bySymbol[sid] = fi;
            }

            var rows = new List<(Family f, long instanceId, int solids, int faces)>();
            int sampled = 0;
            bool truncated = false;
            foreach (var f in families)
            {
                if (budget.Exceeded)
                {
                    truncated = true;
                    break;
                }
                FamilyInstance sample = null;
                foreach (var sid in f.GetFamilySymbolIds())
                    if (bySymbol.TryGetValue(sid.GetValue(), out sample)) break;
                if (sample == null) continue;
                sampled++;
                int solids = 0, faces = 0;
                try
                {
                    Count(sample.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine }), ref solids, ref faces);
                }
                catch
                {
                    continue;
                }
                rows.Add((f, sample.Id.GetValue(), solids, faces));
            }

            return new
            {
                sampledFamilies = sampled,
                truncated,
                elapsedMs = budget.ElapsedMs,
                heaviest = rows.OrderByDescending(r => r.faces).Take(a.Limit).Select(r => new
                {
                    id = r.f.Id.GetValue(),
                    name = r.f.Name,
                    category = r.f.FamilyCategory?.Name,
                    sampleInstanceId = r.instanceId,
                    solids = r.solids,
                    faces = r.faces
                }).ToList()
            };
        }

        private static void Count(GeometryElement geo, ref int solids, ref int faces)
        {
            if (geo == null) return;
            foreach (var obj in geo)
            {
                if (obj is Solid s && s.Faces.Size > 0)
                {
                    solids++;
                    faces += s.Faces.Size;
                }
                else if (obj is GeometryInstance gi)
                {
                    Count(gi.GetInstanceGeometry(), ref solids, ref faces);
                }
                else if (obj is Mesh m)
                {
                    faces += m.NumTriangles;
                }
            }
        }
    }
}
