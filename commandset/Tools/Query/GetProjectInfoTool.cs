using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Query.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Query
{
    public sealed class GetProjectInfoArgs
    {
        [JsonProperty("include")]
        [Description("Extra sections: phases, worksets, levels, links, counts, units, parameters (all Project Information parameters). Default: only the metadata.")]
        [EnumValues("phases", "worksets", "levels", "links", "counts", "units", "parameters")]
        public List<string> Include { get; set; }
    }

    /// <summary>CON-01: project / document metadata with optional summary sections.</summary>
    [McpTool("get_project_info", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "query", Idempotent = true,
        Description = "Project and document metadata: title, path, central model, worksharing, Revit version, Project " +
                      "Information fields (name, number, client, address, status, issue date, author, organization, " +
                      "building name; read by built-in parameter, so it works in any Revit language) and the active " +
                      "view. Ask for more with include[]: phases, worksets, levels (elevation mm), links, counts " +
                      "(elements per main category), units (display units) and parameters (every Project Information " +
                      "parameter in wire units). Read-only.")]
    public sealed class GetProjectInfoTool : McpTool<GetProjectInfoArgs>
    {
        private static readonly (string key, BuiltInParameter bip)[] InfoFields =
        {
            ("name", BuiltInParameter.PROJECT_NAME),
            ("number", BuiltInParameter.PROJECT_NUMBER),
            ("client", BuiltInParameter.CLIENT_NAME),
            ("address", BuiltInParameter.PROJECT_ADDRESS),
            ("status", BuiltInParameter.PROJECT_STATUS),
            ("issueDate", BuiltInParameter.PROJECT_ISSUE_DATE),
            ("buildingName", BuiltInParameter.PROJECT_BUILDING_NAME),
            ("author", BuiltInParameter.PROJECT_AUTHOR),
            ("organizationName", BuiltInParameter.PROJECT_ORGANIZATION_NAME),
            ("organizationDescription", BuiltInParameter.PROJECT_ORGANIZATION_DESCRIPTION),
        };

        private static readonly (string key, BuiltInCategory cat)[] CountedCategories =
        {
            ("walls", BuiltInCategory.OST_Walls),
            ("floors", BuiltInCategory.OST_Floors),
            ("roofs", BuiltInCategory.OST_Roofs),
            ("ceilings", BuiltInCategory.OST_Ceilings),
            ("doors", BuiltInCategory.OST_Doors),
            ("windows", BuiltInCategory.OST_Windows),
            ("rooms", BuiltInCategory.OST_Rooms),
            ("areas", BuiltInCategory.OST_Areas),
            ("furniture", BuiltInCategory.OST_Furniture),
            ("columns", BuiltInCategory.OST_Columns),
            ("structuralColumns", BuiltInCategory.OST_StructuralColumns),
            ("structuralFraming", BuiltInCategory.OST_StructuralFraming),
            ("stairs", BuiltInCategory.OST_Stairs),
            ("grids", BuiltInCategory.OST_Grids),
            ("levels", BuiltInCategory.OST_Levels),
            ("sheets", BuiltInCategory.OST_Sheets),
        };

        protected override object Run(ToolContext ctx, GetProjectInfoArgs args)
        {
            var doc = ctx.Doc;
            var include = new HashSet<string>((args.Include ?? new List<string>()).Select(s => s.Trim().ToLowerInvariant()));
            var result = new Dictionary<string, object>();

            string centralPath = null;
            if (doc.IsWorkshared)
            {
                try
                {
                    var mp = doc.GetWorksharingCentralModelPath();
                    if (mp != null) centralPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(mp);
                }
                catch
                {
                    // cloud models may not expose a user-visible path
                }
            }

            result["document"] = new
            {
                docId = RevitDocumentContext.GetDocId(doc),
                title = doc.Title,
                pathName = doc.PathName ?? string.Empty,
                isFamilyDocument = doc.IsFamilyDocument,
                isWorkshared = doc.IsWorkshared,
                centralModelPath = centralPath,
                isReadOnly = doc.IsReadOnly,
                isModified = doc.IsModified,
                isCloudModel = SafeBool(() => doc.IsModelInCloud),
                revitVersion = doc.Application.VersionNumber,
                revitBuild = doc.Application.VersionBuild
            };

            var pi = doc.ProjectInformation;
            if (pi != null)
            {
                var info = new Dictionary<string, object> { ["id"] = pi.Id.GetValue() };
                foreach (var (key, bip) in InfoFields)
                {
                    try { info[key] = pi.get_Parameter(bip)?.AsString(); }
                    catch { info[key] = null; }
                }
                result["projectInformation"] = info;

                if (include.Contains("parameters"))
                {
                    var ps = ctx.Services.Parameters;
                    result["projectInformationParameters"] = pi.Parameters.Cast<Parameter>()
                        .OrderBy(p => p.Definition?.Name, StringComparer.CurrentCultureIgnoreCase)
                        .Select(p => ps.Describe(p, "instance", true)).ToList();
                }
            }

            var uiDoc = ctx.UiApp?.ActiveUIDocument;
            if (uiDoc != null && uiDoc.Document != null && uiDoc.Document.Equals(doc) && uiDoc.ActiveView != null)
            {
                var v = uiDoc.ActiveView;
                result["activeView"] = new { id = v.Id.GetValue(), name = v.Name, viewType = v.ViewType.ToString() };
            }
            else
            {
                result["activeView"] = null;
            }

            if (include.Contains("phases"))
                result["phases"] = doc.Phases.Cast<Phase>().Select((p, i) => new { id = p.Id.GetValue(), name = p.Name, order = i }).ToList();

            if (include.Contains("worksets"))
            {
                result["worksets"] = doc.IsWorkshared
                    ? new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets()
                        .Select(w => (object)new { id = (long)w.Id.IntegerValue, name = w.Name, isOpen = w.IsOpen }).ToList()
                    : new List<object>();
            }

            if (include.Contains("levels"))
            {
                result["levels"] = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .OrderBy(l => l.ProjectElevation)
                    .Select(l => new { id = l.Id.GetValue(), name = l.Name, elevationMm = QueryHelpers.Mm(l.ProjectElevation) })
                    .ToList();
            }

            if (include.Contains("links"))
            {
                result["links"] = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
                    .Select(l =>
                    {
                        var ld = SafeLinkDoc(l);
                        return new
                        {
                            id = l.Id.GetValue(),
                            name = l.Name,
                            typeId = l.GetTypeId().GetValue(),
                            loaded = ld != null,
                            title = ld?.Title
                        };
                    }).ToList();
            }

            if (include.Contains("counts"))
            {
                var counts = new Dictionary<string, int>();
                foreach (var (key, cat) in CountedCategories)
                {
                    try
                    {
                        counts[key] = new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsNotElementType().GetElementCount();
                    }
                    catch
                    {
                        counts[key] = 0;
                    }
                }
                counts["views"] = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Count(v => !v.IsTemplate);
                counts["families"] = new FilteredElementCollector(doc).OfClass(typeof(Family)).GetElementCount();
                counts["revitLinks"] = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).GetElementCount();
                result["counts"] = counts;
            }

            if (include.Contains("units"))
            {
                var units = doc.GetUnits();
                var d = new Dictionary<string, object>();
                foreach (var (key, spec) in new[]
                         {
                             ("length", SpecTypeId.Length), ("area", SpecTypeId.Area), ("volume", SpecTypeId.Volume),
                             ("angle", SpecTypeId.Angle), ("slope", SpecTypeId.Slope)
                         })
                {
                    try
                    {
                        var fo = units.GetFormatOptions(spec);
                        d[key] = new { unit = fo.GetUnitTypeId()?.TypeId, accuracy = fo.Accuracy };
                    }
                    catch
                    {
                        d[key] = null;
                    }
                }
                d["note"] = "Display units of the model. The MCP wire always uses mm, m2, m3 and degrees.";
                result["units"] = d;
            }

            return result;
        }

        private static Document SafeLinkDoc(RevitLinkInstance l)
        {
            try { return l.GetLinkDocument(); }
            catch { return null; }
        }

        private static bool? SafeBool(Func<bool> f)
        {
            try { return f(); }
            catch { return null; }
        }
    }
}
