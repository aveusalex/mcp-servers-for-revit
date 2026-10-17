using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Architecture.Support
{
    /// <summary>Level creation, elevation changes and plan views, each read back.</summary>
    public static class LevelOps
    {
        /// <summary>Elevations closer than this (feet, ≈0.5 mm) are the same.</summary>
        public const double SameElevation = 0.5 / 304.8;

        public static Level Create(ToolContext ctx, string name, double elevationFt)
        {
            ctx.Services.Names.EnsureUniqueInClass(typeof(Level), name, "level name");
            var level = Level.Create(ctx.Doc, elevationFt)
                        ?? throw new ToolException("CREATE_FAILED", $"Revit did not create level '{name}'.");
            ctx.Services.Names.ApplyName(level, name, "level name");
            SetElevation(level, elevationFt);
            return level;
        }

        /// <summary>Sets <see cref="Level.Elevation"/> (relative to the level type's elevation base) and reads it back.</summary>
        public static void SetElevation(Level level, double elevationFt)
        {
            if (Math.Abs(level.Elevation - elevationFt) > 1e-7) level.Elevation = elevationFt;
            if (Math.Abs(level.Elevation - elevationFt) > 1e-6)
                throw new ToolException("ELEVATION_NOT_APPLIED",
                    $"Revit kept level '{level.Name}' at {ArchGeometry.Fmt(ArchGeometry.Mm(level.Elevation))} instead of {ArchGeometry.Fmt(ArchGeometry.Mm(elevationFt))}.");
        }

        /// <summary>Renames through the shared name rules (forbidden characters, collision) with read-back.</summary>
        public static void Rename(ToolContext ctx, Level level, string name)
        {
            if (string.Equals(level.Name, name, StringComparison.Ordinal)) return;
            ctx.Services.Names.EnsureUniqueInClass(typeof(Level), name, "level name", level.Id);
            ctx.Services.Names.ApplyName(level, name, "level name");
        }

        public static IList<ViewPlan> PlansOf(Document doc, Level level, ViewFamily family) =>
            new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.GenLevel != null && v.GenLevel.Id == level.Id && FamilyOf(doc, v) == family)
                .ToList();

        private static ViewFamily? FamilyOf(Document doc, View v) =>
            (doc.GetElement(v.GetTypeId()) as ViewFamilyType)?.ViewFamily;

        /// <summary>A plan view of the level; the view type is the one named, else Revit's default for that view family.</summary>
        public static ViewPlan CreatePlan(ToolContext ctx, Level level, ViewFamily family, string viewTypeName)
        {
            var doc = ctx.Doc;
            var types = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .Where(t => t.ViewFamily == family).ToList();
            ViewFamilyType vft;
            if (!string.IsNullOrWhiteSpace(viewTypeName))
                vft = ctx.Services.Lookup.ByNameOrId(viewTypeName, types, family == ViewFamily.CeilingPlan ? "ceiling plan view type" : "floor plan view type");
            else
            {
                var group = family == ViewFamily.CeilingPlan ? ElementTypeGroup.ViewTypeCeilingPlan : ElementTypeGroup.ViewTypeFloorPlan;
                vft = doc.GetElement(doc.GetDefaultElementTypeId(group)) as ViewFamilyType;
                if (vft == null && types.Count == 1) vft = types[0];
                if (vft == null)
                    throw new ToolException("VIEW_TYPE_REQUIRED", $"The document has no default {family} view type; name one.",
                        "Available: " + string.Join("; ", types.Select(t => t.Name)) + ".");
            }
            var plan = ViewPlan.Create(doc, vft.Id, level.Id)
                       ?? throw new ToolException("CREATE_FAILED", $"Revit did not create the {family} view of '{level.Name}'.");
            return plan;
        }

        /// <summary>What hangs on a level: plan views and elements whose level is it (moved/deleted with it).</summary>
        public static object Dependents(Document doc, Level level)
        {
            var views = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.GenLevel != null && v.GenLevel.Id == level.Id).Select(v => v.Name).ToList();
            var elements = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                .WherePasses(new ElementLevelFilter(level.Id)).GetElementCount();
            return new { views = views.Count, viewNames = views.Take(20).ToList(), elements };
        }

        public static object Describe(Level l) => new
        {
            id = l.Id.GetValue(),
            name = l.Name,
            elevationMm = ArchGeometry.Mm(l.Elevation),
            projectElevationMm = ArchGeometry.Mm(l.ProjectElevation)
        };

        public static IList<Level> All(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();

        public static bool SameName(string a, string b) =>
            string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
