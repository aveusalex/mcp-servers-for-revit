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
using RevitMCPCommandSet.Tools.Architecture.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Architecture
{
    // ---------------------------------------------------------------------------------------------------------------
    // ARQ-11 create_or_update_levels
    // ---------------------------------------------------------------------------------------------------------------

    public sealed class LevelItem
    {
        [JsonProperty("name")]
        [Required]
        [Description("Level name. If a level with this name exists it is updated, otherwise created (unless level is given).")]
        public string Name { get; set; }

        [JsonProperty("level")]
        [Description("Existing level to update (name or id); it is renamed to name. Omit to match by name.")]
        public string Level { get; set; }

        [JsonProperty("elevation")]
        [Unit("mm")]
        [Description("Elevation in mm, as shown on the level head (relative to the level type's elevation base). Required to create.")]
        public double? Elevation { get; set; }

        [JsonProperty("createPlanView")]
        [Description("Create a floor plan for the level if it has none. Default: true for new levels, false for existing ones.")]
        public bool? CreatePlanView { get; set; }

        [JsonProperty("createCeilingPlan")]
        [DefaultValue(false)]
        [Description("Create a reflected ceiling plan for the level if it has none. Default false.")]
        public bool CreateCeilingPlan { get; set; }
    }

    public sealed class CreateOrUpdateLevelsArgs
    {
        [JsonProperty("items")]
        [Required]
        [MinLength(1)]
        [MaxLength(200)]
        [Description("Levels to create or update.")]
        public List<LevelItem> Items { get; set; }

        [JsonProperty("floorPlanType")]
        [Description("Floor plan view type for new plans, by name. Default: the document's default floor plan type.")]
        public string FloorPlanType { get; set; }

        [JsonProperty("ceilingPlanType")]
        [Description("Ceiling plan view type for new plans, by name. Default: the document's default ceiling plan type.")]
        public string CeilingPlanType { get; set; }
    }

    /// <summary>ARQ-11: create, rename or move levels in batch, with optional plan views.</summary>
    [McpTool("create_or_update_levels", Kind = ToolKind.Write, Risk = Risk.Medium, Toolset = "architecture",
        Description = "Create levels or update existing ones (upsert by name, or rename an existing level given in " +
                      "level): elevation in mm, optional floor plan and ceiling plan. Names are checked for forbidden " +
                      "characters and collisions and read back. Changing an elevation moves everything on that level; " +
                      "the result lists how many elements and views depend on it. Plan views are not renamed. One Ctrl+Z " +
                      "undoes the call.")]
    public sealed class CreateOrUpdateLevelsTool : McpTool<CreateOrUpdateLevelsArgs>
    {
        protected override void Validate(CreateOrUpdateLevelsArgs args)
        {
            var dup = NameRules.DuplicatesIn(args.Items.Select(i => i?.Name));
            if (dup.Count > 0) throw new ToolException("NAME_COLLISION", "Level name(s) repeated in the request: " + string.Join(", ", dup) + ".");
        }

        protected override object Run(ToolContext ctx, CreateOrUpdateLevelsArgs args)
        {
            var doc = ctx.Doc;
            var results = new List<object>();
            for (int i = 0; i < args.Items.Count; i++)
            {
                var it = args.Items[i];
                try
                {
                    results.Add(Upsert(ctx, args, it, i));
                }
                catch (ToolException ex)
                {
                    throw new ToolException(ex.Code, $"Level [{i}] '{it.Name}': {ex.Message} Nothing was changed.", ex.Hint, ex.RecommendedTool);
                }
            }
            return new { levels = results, count = results.Count };
        }

        private static object Upsert(ToolContext ctx, CreateOrUpdateLevelsArgs args, LevelItem it, int index)
        {
            var doc = ctx.Doc;
            Level level = null;
            if (!string.IsNullOrWhiteSpace(it.Level)) level = ctx.Services.Lookup.Level(it.Level);
            else level = LevelOps.All(doc).FirstOrDefault(l => string.Equals(l.Name, it.Name, StringComparison.Ordinal))
                         ?? LevelOps.All(doc).FirstOrDefault(l => LevelOps.SameName(l.Name, it.Name));

            string action;
            object before = null;
            object dependents = null;
            if (level == null)
            {
                if (!it.Elevation.HasValue)
                    throw new ToolException("INVALID_ARGUMENTS", "elevation is required to create a level.");
                level = LevelOps.Create(ctx, it.Name, ArchGeometry.Ft(it.Elevation.Value));
                action = "created";
            }
            else
            {
                before = LevelOps.Describe(level);
                var changes = new List<string>();
                if (!string.Equals(level.Name, it.Name, StringComparison.Ordinal))
                {
                    LevelOps.Rename(ctx, level, it.Name);
                    changes.Add("renamed");
                }
                if (it.Elevation.HasValue && Math.Abs(level.Elevation - ArchGeometry.Ft(it.Elevation.Value)) > 1e-7)
                {
                    dependents = LevelOps.Dependents(doc, level);
                    LevelOps.SetElevation(level, ArchGeometry.Ft(it.Elevation.Value));
                    changes.Add("moved");
                }
                action = changes.Count == 0 ? "unchanged" : string.Join("+", changes);
            }

            var views = new List<object>();
            bool wantPlan = it.CreatePlanView ?? action == "created";
            if (wantPlan && LevelOps.PlansOf(doc, level, ViewFamily.FloorPlan).Count == 0)
            {
                var v = LevelOps.CreatePlan(ctx, level, ViewFamily.FloorPlan, args.FloorPlanType);
                views.Add(new { id = v.Id.GetValue(), name = v.Name, kind = "floorPlan" });
            }
            if (it.CreateCeilingPlan && LevelOps.PlansOf(doc, level, ViewFamily.CeilingPlan).Count == 0)
            {
                var v = LevelOps.CreatePlan(ctx, level, ViewFamily.CeilingPlan, args.CeilingPlanType);
                views.Add(new { id = v.Id.GetValue(), name = v.Name, kind = "ceilingPlan" });
            }

            return new { index, action, level = LevelOps.Describe(level), before, dependentsMoved = dependents, viewsCreated = views };
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // ARQ-12 setup_project_levels
    // ---------------------------------------------------------------------------------------------------------------

    public sealed class ProjectFloor
    {
        [JsonProperty("name")]
        [Required]
        [Description("Floor (pavimento) name used in the level names, e.g. \"Térreo\", \"Pavimento Tipo\", \"Cobertura\".")]
        public string Name { get; set; }

        [JsonProperty("finishedElevation")]
        [Required]
        [Unit("mm")]
        [Description("Finished floor elevation (AC, piso acabado) in mm.")]
        public double FinishedElevation { get; set; }

        [JsonProperty("code")]
        [Description("Optional code for {code} in nameTemplate (e.g. \"N01\"). Default: the floor's position, 2 digits.")]
        public string Code { get; set; }

        [JsonProperty("finishThickness")]
        [Unit("mm")]
        [Range(0, 2000)]
        [Description("Overrides finishThickness for this floor (mm).")]
        public double? FinishThickness { get; set; }

        [JsonProperty("ceilingHeight")]
        [Unit("mm")]
        [Range(0, 50000)]
        [Description("Overrides ceilingHeight for this floor (mm).")]
        public double? CeilingHeight { get; set; }
    }

    public sealed class SiteLevel
    {
        [JsonProperty("name")]
        [Required]
        [Description("Level name, used as is (e.g. \"Terreno\", \"Calçada\").")]
        public string Name { get; set; }

        [JsonProperty("elevation")]
        [Required]
        [Unit("mm")]
        [Description("Elevation in mm.")]
        public double Elevation { get; set; }
    }

    public sealed class LevelKindLabels
    {
        [JsonProperty("finished")]
        [DefaultValue("AC")]
        [Description("Label of the finished-floor level (default \"AC\").")]
        public string Finished { get; set; } = "AC";

        [JsonProperty("structure")]
        [DefaultValue("ES")]
        [Description("Label of the structure (top of slab) level (default \"ES\").")]
        public string Structure { get; set; } = "ES";

        [JsonProperty("ceiling")]
        [DefaultValue("FO")]
        [Description("Label of the ceiling level (default \"FO\").")]
        public string Ceiling { get; set; } = "FO";
    }

    public sealed class SetupProjectLevelsArgs
    {
        [JsonProperty("floors")]
        [Required]
        [MinLength(1)]
        [MaxLength(100)]
        [Description("Floors (pavimentos) from bottom to top with their finished elevation.")]
        public List<ProjectFloor> Floors { get; set; }

        [JsonProperty("kinds")]
        [EnumValues("finished", "structure", "ceiling")]
        [Description("Which levels each floor gets. Default [finished, structure, ceiling] (AC, ES, FO).")]
        public List<string> Kinds { get; set; }

        [JsonProperty("finishThickness")]
        [Unit("mm")]
        [Range(0, 2000)]
        [Description("Floor finish build-up in mm: structure level = finished - finishThickness. Required when kinds include structure.")]
        public double? FinishThickness { get; set; }

        [JsonProperty("ceilingHeight")]
        [Unit("mm")]
        [Range(0, 50000)]
        [Description("Ceiling height above the finished floor in mm: ceiling level = finished + ceilingHeight. Required when kinds include ceiling.")]
        public double? CeilingHeight { get; set; }

        [JsonProperty("labels")]
        [Description("Labels for {kind} in nameTemplate. Default AC / ES / FO.")]
        public LevelKindLabels Labels { get; set; }

        [JsonProperty("nameTemplate")]
        [DefaultValue("{floor} - {kind}")]
        [Description("Level name pattern with {floor}, {kind}, {code} and {index} (1-based, 2 digits). Default \"{floor} - {kind}\". Follow the office naming (get_office_knowledge).")]
        public string NameTemplate { get; set; }

        [JsonProperty("siteLevels")]
        [MaxLength(50)]
        [Description("Extra levels used as is: terrain, sidewalk (calçada), platforms.")]
        public List<SiteLevel> SiteLevels { get; set; }

        [JsonProperty("adoptByElevation")]
        [DefaultValue(true)]
        [Description("Rename an unmatched existing level that sits at a wanted elevation (±1 mm) instead of creating a duplicate (e.g. the template's \"Nível 1\"). Default true.")]
        public bool AdoptByElevation { get; set; } = true;

        [JsonProperty("removeUnlisted")]
        [DefaultValue(false)]
        [Description("Plan to delete existing levels that are not in the setup. Default false (they are reported as extra and kept).")]
        public bool RemoveUnlisted { get; set; }

        [JsonProperty("confirmRemove")]
        [DefaultValue(false)]
        [Description("Required to actually delete levels (and the views and elements hosted on them). Without it a plan with removals is refused outside dryRun.")]
        public bool ConfirmRemove { get; set; }

        [JsonProperty("createPlanViews")]
        [DefaultValue(true)]
        [Description("Create a floor plan for finished levels and a ceiling plan for ceiling levels when missing. Default true.")]
        public bool CreatePlanViews { get; set; } = true;
    }

    /// <summary>ARQ-12: declarative level setup (AC/ES/FO per floor + site levels) with a create/adjust/keep/remove plan.</summary>
    [McpTool("setup_project_levels", Kind = ToolKind.Write, Risk = Risk.Medium, Toolset = "architecture", TimeoutSeconds = 120,
        Description = "Declarative project level setup: describe the floors (name + finished elevation in mm) and the tool " +
                      "derives finished (AC), structure (ES = AC - finishThickness) and ceiling (FO = AC + ceilingHeight) " +
                      "levels plus site levels, compares them with the model and returns a plan per level: create, adjust " +
                      "(elevation), rename, keep, extra or remove. Run with dryRun first to show the plan. Removing levels " +
                      "deletes their views and hosted elements and needs removeUnlisted + confirmRemove. One Ctrl+Z undoes the call.")]
    public sealed class SetupProjectLevelsTool : McpTool<SetupProjectLevelsArgs>
    {
        private sealed class Wanted
        {
            public string Name;
            public double ElevationFt;
            public string Kind;
            public string Floor;
            public Level Match;
            public string Action;
        }

        protected override void Validate(SetupProjectLevelsArgs args)
        {
            var kinds = Kinds(args);
            if (kinds.Contains("structure") && !args.FinishThickness.HasValue && args.Floors.Any(f => !f.FinishThickness.HasValue))
                throw new ToolException("INVALID_ARGUMENTS", "finishThickness is required when kinds include structure (ES).");
            if (kinds.Contains("ceiling") && !args.CeilingHeight.HasValue && args.Floors.Any(f => !f.CeilingHeight.HasValue))
                throw new ToolException("INVALID_ARGUMENTS", "ceilingHeight is required when kinds include ceiling (FO).");
            if (args.ConfirmRemove && !args.RemoveUnlisted)
                throw new ToolException("INVALID_ARGUMENTS", "confirmRemove only makes sense with removeUnlisted:true.");
        }

        private static IList<string> Kinds(SetupProjectLevelsArgs args) =>
            (args.Kinds == null || args.Kinds.Count == 0 ? new List<string> { "finished", "structure", "ceiling" } : args.Kinds)
            .Select(k => k.ToLowerInvariant()).Distinct().ToList();

        protected override object Run(ToolContext ctx, SetupProjectLevelsArgs args)
        {
            var doc = ctx.Doc;
            var wanted = BuildWanted(ctx, args);
            var existing = LevelOps.All(doc).ToList();
            var claimed = new HashSet<long>();

            // 1. by name
            foreach (var w in wanted)
            {
                var m = existing.FirstOrDefault(l => !claimed.Contains(l.Id.GetValue()) && string.Equals(l.Name, w.Name, StringComparison.Ordinal))
                        ?? existing.FirstOrDefault(l => !claimed.Contains(l.Id.GetValue()) && LevelOps.SameName(l.Name, w.Name));
                if (m == null) continue;
                w.Match = m;
                claimed.Add(m.Id.GetValue());
                bool sameElevation = Math.Abs(m.Elevation - w.ElevationFt) <= LevelOps.SameElevation;
                bool sameName = string.Equals(m.Name, w.Name, StringComparison.Ordinal);
                w.Action = sameElevation ? (sameName ? "keep" : "rename") : "adjust";
            }
            // 2. adopt by elevation
            if (args.AdoptByElevation)
            {
                foreach (var w in wanted.Where(x => x.Match == null))
                {
                    var m = existing.Where(l => !claimed.Contains(l.Id.GetValue()) && Math.Abs(l.Elevation - w.ElevationFt) <= 1 / 304.8)
                        .OrderBy(l => Math.Abs(l.Elevation - w.ElevationFt)).FirstOrDefault();
                    if (m == null) continue;
                    w.Match = m;
                    claimed.Add(m.Id.GetValue());
                    w.Action = "rename";
                }
            }
            // 3. create
            foreach (var w in wanted.Where(x => x.Match == null)) w.Action = "create";
            // 4. leftovers
            var leftovers = existing.Where(l => !claimed.Contains(l.Id.GetValue())).ToList();

            var plan = new List<PlanEntry>();
            foreach (var w in wanted.OrderBy(x => x.ElevationFt))
            {
                plan.Add(new PlanEntry
                {
                    Action = w.Action,
                    Name = w.Name,
                    Kind = w.Kind,
                    Floor = w.Floor,
                    ElevationMm = ArchGeometry.Mm(w.ElevationFt),
                    Current = w.Match == null ? null : LevelOps.Describe(w.Match),
                    Dependents = w.Action == "adjust" ? LevelOps.Dependents(doc, w.Match) : null
                });
            }
            foreach (var l in leftovers)
            {
                plan.Add(new PlanEntry
                {
                    Action = args.RemoveUnlisted ? "remove" : "extra",
                    Name = l.Name,
                    ElevationMm = ArchGeometry.Mm(l.Elevation),
                    Current = LevelOps.Describe(l),
                    Dependents = LevelOps.Dependents(doc, l)
                });
            }
            foreach (var w in SanityWarnings(args, wanted)) ctx.Warnings.Add(new ToolWarning(w));

            bool removing = args.RemoveUnlisted && leftovers.Count > 0;
            if (removing && !args.ConfirmRemove && !ctx.DryRun)
                throw new ToolException("CONFIRM_REQUIRED",
                    $"The setup would delete {leftovers.Count} level(s) ({string.Join(", ", leftovers.Select(l => l.Name))}) with their views and hosted elements. Nothing was changed.",
                    "Show the plan (dryRun) to the user and call again with confirmRemove:true only after they agree, or drop removeUnlisted.");

            // Apply, in dryRun too: the dispatcher rolls it back, so the plan is checked against what Revit accepts.
            // A leftover never holds a wanted name (it would have matched by name), so only renames can collide
            // among themselves: go through temporary names first.
            var renames = wanted.Where(w => w.Match != null && !string.Equals(w.Match.Name, w.Name, StringComparison.Ordinal)).ToList();
            foreach (var w in renames)
                ctx.Services.Names.ApplyName(w.Match, "mcp-tmp-" + w.Match.Id.GetValue().ToString(CultureInfo.InvariantCulture), "level name");
            foreach (var w in renames) LevelOps.Rename(ctx, w.Match, w.Name);
            foreach (var w in wanted.Where(x => x.Action == "adjust")) LevelOps.SetElevation(w.Match, w.ElevationFt);
            foreach (var w in wanted.Where(x => x.Action == "create")) w.Match = LevelOps.Create(ctx, w.Name, w.ElevationFt);
            // Delete last, so the document always keeps at least one level.
            if (removing) DeleteLevels(doc, leftovers);

            var views = new List<object>();
            if (args.CreatePlanViews)
            {
                foreach (var w in wanted)
                {
                    var family = w.Kind == "finished" ? ViewFamily.FloorPlan : w.Kind == "ceiling" ? ViewFamily.CeilingPlan : (ViewFamily?)null;
                    if (family == null || w.Match == null) continue;
                    if (LevelOps.PlansOf(doc, w.Match, family.Value).Count > 0) continue;
                    var v = LevelOps.CreatePlan(ctx, w.Match, family.Value, null);
                    views.Add(new { id = v.Id.GetValue(), name = v.Name, level = w.Name, kind = family.Value.ToString() });
                }
            }
            doc.Regenerate();

            return new
            {
                plan,
                summary = plan.GroupBy(p => p.Action).ToDictionary(g => g.Key, g => g.Count()),
                applied = !ctx.DryRun,
                levels = wanted.Where(w => w.Match != null && w.Match.IsValidObject).OrderBy(w => w.ElevationFt).Select(w => LevelOps.Describe(w.Match)).ToList(),
                viewsCreated = views,
                note = ctx.DryRun ? "dryRun: nothing was kept. Show this plan to the user before applying." : null
            };
        }

        private sealed class PlanEntry
        {
            [JsonProperty("action")] public string Action { get; set; }
            [JsonProperty("name")] public string Name { get; set; }
            [JsonProperty("kind")] public string Kind { get; set; }
            [JsonProperty("floor")] public string Floor { get; set; }
            [JsonProperty("elevationMm")] public double ElevationMm { get; set; }
            [JsonProperty("current")] public object Current { get; set; }
            [JsonProperty("dependents")] public object Dependents { get; set; }
        }

        private static void DeleteLevels(Document doc, IList<Level> levels)
        {
            foreach (var l in levels.Where(x => x.IsValidObject))
            {
                try
                {
                    doc.Delete(l.Id);
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException ex)
                {
                    throw new ToolException("DELETE_FAILED", $"Revit refused to delete level '{l.Name}': {ex.Message}");
                }
            }
        }

        private static List<Wanted> BuildWanted(ToolContext ctx, SetupProjectLevelsArgs args)
        {
            var labels = args.Labels ?? new LevelKindLabels();
            var template = string.IsNullOrWhiteSpace(args.NameTemplate) ? "{floor} - {kind}" : args.NameTemplate;
            var kinds = Kinds(args);
            var list = new List<Wanted>();
            for (int i = 0; i < args.Floors.Count; i++)
            {
                var f = args.Floors[i];
                var code = string.IsNullOrWhiteSpace(f.Code) ? (i + 1).ToString("00", CultureInfo.InvariantCulture) : f.Code.Trim();
                string Name(string label) => template.Replace("{floor}", f.Name.Trim()).Replace("{kind}", label)
                    .Replace("{code}", code).Replace("{index}", (i + 1).ToString("00", CultureInfo.InvariantCulture)).Trim();
                double ac = ArchGeometry.Ft(f.FinishedElevation);
                if (kinds.Contains("structure"))
                    list.Add(new Wanted { Name = Name(labels.Structure), ElevationFt = ac - ArchGeometry.Ft(f.FinishThickness ?? args.FinishThickness ?? 0), Kind = "structure", Floor = f.Name });
                if (kinds.Contains("finished"))
                    list.Add(new Wanted { Name = Name(labels.Finished), ElevationFt = ac, Kind = "finished", Floor = f.Name });
                if (kinds.Contains("ceiling"))
                    list.Add(new Wanted { Name = Name(labels.Ceiling), ElevationFt = ac + ArchGeometry.Ft(f.CeilingHeight ?? args.CeilingHeight ?? 0), Kind = "ceiling", Floor = f.Name });
            }
            foreach (var s in args.SiteLevels ?? new List<SiteLevel>())
                list.Add(new Wanted { Name = s.Name.Trim(), ElevationFt = ArchGeometry.Ft(s.Elevation), Kind = "site", Floor = null });

            var dup = NameRules.DuplicatesIn(list.Select(w => w.Name));
            if (dup.Count > 0)
                throw new ToolException("NAME_COLLISION", "The setup produces repeated level names: " + string.Join(", ", dup) + ".",
                    "Include {kind} and {floor} (or {code}) in nameTemplate.");
            foreach (var w in list) ctx.Services.Names.Validate(w.Name, "level name");
            return list;
        }

        private static List<string> SanityWarnings(SetupProjectLevelsArgs args, List<Wanted> wanted)
        {
            var warnings = new List<string>();
            var floors = args.Floors.OrderBy(f => f.FinishedElevation).ToList();
            if (!floors.SequenceEqual(args.Floors))
                warnings.Add("floors are not listed from bottom to top; {index}/{code} follow the order given.");
            for (int i = 0; i + 1 < floors.Count; i++)
            {
                var fo = wanted.FirstOrDefault(w => w.Kind == "ceiling" && w.Floor == floors[i].Name);
                var es = wanted.FirstOrDefault(w => w.Kind == "structure" && w.Floor == floors[i + 1].Name);
                if (fo != null && es != null && fo.ElevationFt >= es.ElevationFt)
                    warnings.Add($"The ceiling of '{floors[i].Name}' ({ArchGeometry.Fmt(ArchGeometry.Mm(fo.ElevationFt))}) is at or above the structure of '{floors[i + 1].Name}' ({ArchGeometry.Fmt(ArchGeometry.Mm(es.ElevationFt))}).");
            }
            return warnings;
        }
    }
}
