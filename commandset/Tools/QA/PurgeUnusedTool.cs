using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.QA.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.QA
{
    public sealed class PurgeUnusedArgs
    {
        [JsonProperty("targets")]
        [Description("What to purge (default families, types, materials): families, types, materials, fillPatterns, linePatterns, viewTemplates, other.")]
        [EnumValues("families", "types", "materials", "fillPatterns", "linePatterns", "viewTemplates", "other")]
        public List<string> Targets { get; set; }

        [JsonProperty("apply")]
        [Description("false (default): only list what is unused (nothing is deleted). true: delete it (one Ctrl+Z undoes the whole purge).")]
        [DefaultValue(false)]
        public bool Apply { get; set; }

        [JsonProperty("exclude")]
        [Description("Regular expressions on the element name; matching elements are never purged (e.g. \"^Carimbo\", \"_PADRAO$\").")]
        public List<string> Exclude { get; set; }

        [JsonProperty("maxElements")]
        [Description("Delete at most this many elements in one call, 1-10000 (default 2000).")]
        [System.ComponentModel.DataAnnotations.Range(1, 10000)]
        [DefaultValue(2000)]
        public int MaxElements { get; set; } = 2000;

        [JsonProperty("passes")]
        [Description("apply only: repeat the purge up to this many times, 1-5 (default 3), since deleting a family can free its nested families and materials.")]
        [System.ComponentModel.DataAnnotations.Range(1, 5)]
        [DefaultValue(3)]
        public int Passes { get; set; } = 3;

        [JsonProperty("listLimit")]
        [Description("Maximum items listed in the result, 0-2000 (default 300); counts are always complete.")]
        [System.ComponentModel.DataAnnotations.Range(0, 2000)]
        [DefaultValue(300)]
        public int ListLimit { get; set; } = 300;
    }

    /// <summary>QA-05: Purge Unused with a preview by default.</summary>
    [McpTool("purge_unused", Kind = ToolKind.Write, Risk = Risk.Medium, DryRunDefault = true, Toolset = "qa", Batchable = false,
        TimeoutSeconds = 600,
        Description = "Find and (with apply: true) delete unused families, types, materials and patterns - the same list " +
                      "as Revit's Purge Unused on 2024+ (older versions: families and types only). By default it only " +
                      "lists (apply false); dryRun: true previews the exact deletions with real ids. exclude[] protects " +
                      "names by regex. Repeats up to 'passes' times to catch cascades. Undo: Ctrl+Z (one transaction).")]
    public sealed class PurgeUnusedTool : McpTool<PurgeUnusedArgs>
    {
        private static readonly string[] DefaultTargets = { "families", "types", "materials" };

        protected override void Validate(PurgeUnusedArgs a)
        {
            foreach (var x in a.Exclude ?? new List<string>())
            {
                try { _ = new Regex(x); }
                catch (ArgumentException ex) { throw new ToolException("INVALID_ARGUMENTS", $"exclude '{x}' is not a valid regex: {ex.Message}"); }
            }
        }

        protected override object Run(ToolContext ctx, PurgeUnusedArgs a)
        {
            var doc = ctx.Doc;
            var targets = new HashSet<string>((a.Targets == null || a.Targets.Count == 0) ? DefaultTargets : a.Targets, StringComparer.OrdinalIgnoreCase);
            var excludes = (a.Exclude ?? new List<string>()).Select(x => new Regex(x, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToList();

            var found = new List<object>();
            var counts = new Dictionary<string, int>();
            var protectedItems = new List<object>();
            var deleted = new List<long>();
            var failed = new List<object>();
            string source = null;
            int passesRun = 0;
            var attempted = new HashSet<long>();
            bool hitMax = false;

            int passes = a.Apply ? a.Passes : 1;
            for (int pass = 1; pass <= passes; pass++)
            {
                passesRun = pass;
                var candidates = new List<Element>();
                foreach (var id in UnusedFinder.Find(doc, out source))
                {
                    var e = doc.GetElement(id);
                    if (e == null || !e.IsValidObject || attempted.Contains(id.GetValue())) continue;
                    var kind = UnusedFinder.Classify(e);
                    if (!targets.Contains(kind)) continue;
                    var name = QaSupport.SafeName(e) ?? string.Empty;
                    var fam = (e as FamilySymbol)?.FamilyName;
                    if (excludes.Any(rx => rx.IsMatch(name) || (fam != null && rx.IsMatch(fam))))
                    {
                        if (pass == 1 && protectedItems.Count < a.ListLimit)
                            protectedItems.Add(new { id = id.GetValue(), name, kind });
                        continue;
                    }
                    candidates.Add(e);
                }
                if (candidates.Count == 0) break;

                foreach (var e in candidates)
                {
                    var kind = UnusedFinder.Classify(e);
                    counts[kind] = counts.TryGetValue(kind, out var n) ? n + 1 : 1;
                    if (found.Count < a.ListLimit)
                        found.Add(new
                        {
                            id = e.Id.GetValue(),
                            name = QaSupport.SafeName(e),
                            kind,
                            family = (e as FamilySymbol)?.FamilyName,
                            category = e.Category?.Name,
                            pass
                        });
                }
                if (!a.Apply) break;

                // Families first: deleting a family also deletes its types.
                foreach (var e in candidates.OrderBy(x => x is Family ? 0 : 1))
                {
                    if (deleted.Count >= a.MaxElements)
                    {
                        hitMax = true;
                        break;
                    }
                    if (!e.IsValidObject || doc.GetElement(e.Id) == null) continue; // already gone with its family
                    var id = e.Id;
                    attempted.Add(id.GetValue());
                    var name = QaSupport.SafeName(e);
                    using (var st = new SubTransaction(doc))
                    {
                        try
                        {
                            st.Start();
                            doc.Delete(id);
                            st.Commit();
                            deleted.Add(id.GetValue());
                        }
                        catch (Exception ex)
                        {
                            if (st.HasStarted() && !st.HasEnded()) st.RollBack();
                            failed.Add(new { id = id.GetValue(), name, reason = ex.Message });
                        }
                    }
                }
                if (hitMax) break;
                doc.Regenerate();
            }

            int totalFound = counts.Values.Sum();
            return new
            {
                applied = a.Apply,
                source,
                targets = targets.ToList(),
                foundCount = totalFound,
                countsByKind = counts,
                deletedCount = deleted.Count,
                failedCount = failed.Count,
                passes = passesRun,
                reachedMaxElements = hitMax,
                items = found,
                itemsTruncated = totalFound > found.Count,
                protectedByExclude = protectedItems,
                failed,
                next = a.Apply ? null : "Nothing was deleted. Review the list, then call again with apply: true (dryRun: true first previews the exact deletions)."
            };
        }
    }
}
