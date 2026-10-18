using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Architecture.Support;
using RevitMCPCommandSet.Tools.Editing.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Families
{
    public sealed class FamilyBoxType
    {
        [JsonProperty("name", Required = Required.Always)]
        [Description("Type name (e.g. \"600x550x720\").")]
        public string Name { get; set; }

        [JsonProperty("widthMm")]
        [Unit("mm")]
        [Range(1, 100000)]
        [Description("Width of this type. Default: the family's widthMm.")]
        public double? WidthMm { get; set; }

        [JsonProperty("depthMm")]
        [Unit("mm")]
        [Range(1, 100000)]
        [Description("Depth of this type. Default: the family's depthMm.")]
        public double? DepthMm { get; set; }

        [JsonProperty("heightMm")]
        [Unit("mm")]
        [Range(1, 100000)]
        [Description("Height of this type. Default: the family's heightMm.")]
        public double? HeightMm { get; set; }
    }

    public sealed class CreateFamilyFromTemplateArgs
    {
        [JsonProperty("name", Required = Required.Always)]
        [Required]
        [Description("Family name (also the .rfa file name).")]
        public string Name { get; set; }

        [JsonProperty("template")]
        [Description("Family template: a .rft path (allowed folders or Revit's Family Templates folder) or a name searched there (e.g. \"Modelo genérico métrico\", \"Mobiliário métrico\", \"Metric Casework\"). Default: the metric Generic Model template.")]
        public string Template { get; set; }

        [JsonProperty("category")]
        [Description("Family category (pt-BR or English), if the template allows changing it (Generic Model does), e.g. \"Furniture\" / \"Mobiliário\", \"Casework\". Default: the template's.")]
        public string Category { get; set; }

        [JsonProperty("widthMm")]
        [Unit("mm")]
        [Range(1, 100000)]
        [Description("Box width along X (rectangle profile, centered on the origin). Required unless polygon is given.")]
        public double? WidthMm { get; set; }

        [JsonProperty("depthMm")]
        [Unit("mm")]
        [Range(1, 100000)]
        [Description("Box depth along Y. Required unless polygon is given.")]
        public double? DepthMm { get; set; }

        [JsonProperty("heightMm", Required = Required.Always)]
        [Unit("mm")]
        [Range(1, 100000)]
        [Description("Extrusion height from the reference level.")]
        public double HeightMm { get; set; }

        [JsonProperty("polygon")]
        [MinLength(3)]
        [MaxLength(200)]
        [Description("Closed polygon profile in plan (mm, family origin) instead of the rectangle. Only the height is parametric then.")]
        public List<PointMm> Polygon { get; set; }

        [JsonProperty("widthParameter")]
        [DefaultValue("Largura")]
        [Description("Name of the width length parameter. Default \"Largura\".")]
        public string WidthParameter { get; set; } = "Largura";

        [JsonProperty("depthParameter")]
        [DefaultValue("Profundidade")]
        [Description("Name of the depth length parameter. Default \"Profundidade\".")]
        public string DepthParameter { get; set; } = "Profundidade";

        [JsonProperty("heightParameter")]
        [DefaultValue("Altura")]
        [Description("Name of the height length parameter. Default \"Altura\".")]
        public string HeightParameter { get; set; } = "Altura";

        [JsonProperty("materialParameter")]
        [DefaultValue("Material")]
        [Description("Name of a material parameter bound to the extrusion. Default \"Material\"; empty string = none.")]
        public string MaterialParameter { get; set; } = "Material";

        [JsonProperty("void")]
        [DefaultValue(false)]
        [Description("true: an unattached VOID extrusion instead of a solid, with 'Cut with voids when loaded' on: a cutting family (sink or cooktop cutout, opening) to use with create_void_cut. No material parameter. Default false.")]
        public bool Void { get; set; }

        [JsonProperty("instanceParameters")]
        [DefaultValue(false)]
        [Description("Make the parameters instance parameters (default false: type parameters).")]
        public bool InstanceParameters { get; set; }

        [JsonProperty("types")]
        [MaxLength(100)]
        [Description("Family types to create with their sizes. Default: one type named \"<W>x<D>x<H>\".")]
        public List<FamilyBoxType> Types { get; set; }

        [JsonProperty("outputPath")]
        [Description("Where to save the .rfa: absolute, or relative to Documents\\RevitMCP\\families. Default: families\\<name>.rfa.")]
        public string OutputPath { get; set; }

        [JsonProperty("overwrite")]
        [DefaultValue(false)]
        [Description("Overwrite an existing file and reload a family of the same name already in the project. Default false.")]
        public bool Overwrite { get; set; }

        [JsonProperty("loadIntoProject")]
        [DefaultValue(true)]
        [Description("Load the new family into the target document. Default true.")]
        public bool LoadIntoProject { get; set; } = true;
    }

    /// <summary>FAM-16: simple parametric box (or polygon) family from a template, saved and optionally loaded.</summary>
    [McpTool("create_family_from_template", Kind = ToolKind.Write, Risk = Risk.Medium, Toolset = "families", Batchable = false, TimeoutSeconds = 300,
        Description = "Create a loadable family from an installed .rft template (criar família / extrusão travada / vazio de corte): a solid " +
                      "(or, with void:true, an unattached void for create_void_cut) extrusion from a centered rectangle (width x depth) or a polygon, with height, width and depth " +
                      "length parameters (pt-BR names by default) and a material parameter. For the rectangle, reference " +
                      "planes are added, the faces are locked to them, labeled dimensions drive width/depth and EQ " +
                      "dimensions keep it centered; height drives the extrusion end. A flex test reports which parameters " +
                      "really move the geometry. Saves to the allowed folders and optionally loads it into the project. " +
                      "dryRun builds and flexes in memory without saving or loading. Undo the load with Ctrl+Z.")]
    public sealed class CreateFamilyFromTemplateTool : McpTool<CreateFamilyFromTemplateArgs>, ISelfTransactedTool
    {
        private const double Ft = 304.8;

        private sealed class LoadOptions : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = true;
                return true;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = true;
                return true;
            }
        }

        protected override void Validate(CreateFamilyFromTemplateArgs a)
        {
            if (a.Polygon == null && (!a.WidthMm.HasValue || !a.DepthMm.HasValue))
                throw new ToolException("INVALID_ARGUMENTS", "Give widthMm and depthMm (rectangle) or polygon.");
            if (Path.GetInvalidFileNameChars().Any(c => a.Name.Contains(c)))
                throw new ToolException("INVALID_ARGUMENTS", $"'{a.Name}' can't be a file name.");
            var names = new[] { a.WidthParameter, a.DepthParameter, a.HeightParameter, a.MaterialParameter }
                .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList();
            if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
                throw new ToolException("INVALID_ARGUMENTS", "Parameter names must be different.");
        }

        protected override object Run(ToolContext ctx, CreateFamilyFromTemplateArgs a)
        {
            var doc = ctx.Doc;
            var app = doc.Application;
            var name = a.Name.Trim();
            var template = ResolveTemplate(app, a.Template);
            var output = PathPolicy.Resolve(string.IsNullOrWhiteSpace(a.OutputPath) ? name + ".rfa" : a.OutputPath, "families", !ctx.DryRun);
            if (!output.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase)) output = Path.Combine(output, name + ".rfa");
            if (File.Exists(output) && !a.Overwrite)
                throw new ToolException("FILE_EXISTS", $"'{output}' already exists.", "Pass overwrite:true or another outputPath.");
            var familyName = Path.GetFileNameWithoutExtension(output);
            if (a.LoadIntoProject && !a.Overwrite && new FilteredElementCollector(doc).OfClass(typeof(Family))
                    .Any(f => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase)))
                throw new ToolException("NAME_COLLISION", $"The project already has a family '{familyName}'.", "Use another name or overwrite:true to replace it.");
            BuiltInCategory? category = null;
            if (!string.IsNullOrWhiteSpace(a.Category))
                category = ctx.Services.Categories.Resolve(a.Category).BuiltIn
                           ?? throw new ToolException("INVALID_ARGUMENTS", $"'{a.Category}' is not a built-in category a family can use.");

            bool rect = a.Polygon == null;
            double w = (a.WidthMm ?? 0) / Ft, d = (a.DepthMm ?? 0) / Ft, h = a.HeightMm / Ft;
            var locks = new Dictionary<string, object>();
            var notes = new List<string>();
            object flex = null;
            List<string> typeNames;

            Document fam;
            try { fam = app.NewFamilyDocument(template); }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                throw new ToolException("TEMPLATE_FAILED", $"Revit could not create a family from '{template}': {ex.Message}");
            }
            try
            {
                if (!fam.IsFamilyDocument || fam.OwnerFamily == null)
                    throw new ToolException("TEMPLATE_FAILED", $"'{template}' is not a family template.");
                var mgr = fam.FamilyManager;
                Extrusion extrusion;
                FamilyParameter pw = null, pd = null, ph, pm = null;
                using (var tx = new Transaction(fam, "MCP: build family"))
                {
                    var failures = SafeFailuresPreprocessor.Attach(tx, ctx.Warnings);
                    tx.Start();
                    if (mgr.CurrentType == null) mgr.NewType(DefaultTypeName(a));
                    if (category.HasValue)
                    {
                        try { fam.OwnerFamily.FamilyCategory = fam.Settings.Categories.get_Item(category.Value); }
                        catch (Exception ex) { throw new ToolException("CATEGORY_NOT_ALLOWED", $"This template can't use category '{a.Category}': {ex.Message}", "Use a Generic Model template, or the template of that category."); }
                    }

                    var level = new FilteredElementCollector(fam).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => Math.Abs(l.Elevation)).FirstOrDefault();
                    var plan = new FilteredElementCollector(fam).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                        .Where(v => !v.IsTemplate).OrderBy(v => level != null && v.GenLevel?.Id == level.Id ? 0 : 1).FirstOrDefault();
                    var sketch = level != null ? SketchPlane.Create(fam, level.Id) : SketchPlane.Create(fam, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero));

                    var loop = new CurveArray();
                    var pts = rect
                        ? new List<XYZ> { new XYZ(-w / 2, -d / 2, 0), new XYZ(w / 2, -d / 2, 0), new XYZ(w / 2, d / 2, 0), new XYZ(-w / 2, d / 2, 0) }
                        : a.Polygon.Select(p => new XYZ(p.X / Ft, p.Y / Ft, 0)).ToList();
                    for (int i = 0; i < pts.Count; i++)
                    {
                        var p0 = pts[i];
                        var p1 = pts[(i + 1) % pts.Count];
                        if (p0.DistanceTo(p1) < app.ShortCurveTolerance)
                            throw new ToolException("INVALID_GEOMETRY", $"Profile edge {i + 1} is too short.");
                        loop.Append(Line.CreateBound(p0, p1));
                    }
                    var profile = new CurveArrArray();
                    profile.Append(loop);
                    try { extrusion = fam.FamilyCreate.NewExtrusion(!a.Void, profile, sketch, h); }
                    catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                    {
                        throw new ToolException("INVALID_GEOMETRY", $"Revit could not extrude the profile: {ex.Message}", "Check the polygon is closed and does not self-intersect.");
                    }

                    // Parameters
                    ph = Param(mgr, a.HeightParameter, GroupTypeId.Geometry, SpecTypeId.Length, a.InstanceParameters);
                    mgr.Set(ph, h);
                    mgr.AssociateElementParameterToFamilyParameter(extrusion.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM), ph);
                    locks["height"] = "extrusion end bound to " + ph.Definition.Name;
                    if (a.Void)
                    {
                        // Unattached void: it only cuts hosts it is applied to (create_void_cut) once this is on.
                        var cut = fam.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_ALLOW_CUT_WITH_VOIDS);
                        if (cut != null && !cut.IsReadOnly) cut.Set(1);
                        else notes.Add("This template does not allow 'Cut with voids when loaded'; the void may not cut hosts.");
                    }
                    else if (!string.IsNullOrWhiteSpace(a.MaterialParameter))
                    {
                        pm = Param(mgr, a.MaterialParameter, GroupTypeId.Materials, SpecTypeId.Reference.Material, a.InstanceParameters);
                        mgr.AssociateElementParameterToFamilyParameter(extrusion.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM), pm);
                    }
                    if (rect)
                    {
                        pw = Param(mgr, a.WidthParameter, GroupTypeId.Geometry, SpecTypeId.Length, a.InstanceParameters);
                        pd = Param(mgr, a.DepthParameter, GroupTypeId.Geometry, SpecTypeId.Length, a.InstanceParameters);
                        mgr.Set(pw, w);
                        mgr.Set(pd, d);
                        fam.Regenerate();
                        if (plan == null)
                        {
                            notes.Add("The template has no floor plan view, so width/depth could not be dimensioned and locked (they are parameters only).");
                            locks["width"] = locks["depth"] = "not locked (no plan view)";
                        }
                        else
                        {
                            locks["width"] = LockAxis(fam, plan, extrusion, pw, true, w, d, notes);
                            locks["depth"] = LockAxis(fam, plan, extrusion, pd, false, w, d, notes);
                        }
                    }
                    else notes.Add("Polygon profile: only the height is parametric; width/depth parameters were not created.");

                    // Types
                    typeNames = new List<string>();
                    var firstName = a.Types != null && a.Types.Count > 0 ? a.Types[0].Name.Trim() : DefaultTypeName(a);
                    try { if (mgr.CurrentType?.Name != firstName) mgr.RenameCurrentType(firstName); }
                    catch (Exception ex) { notes.Add("The default type could not be renamed: " + ex.Message); }
                    var first = mgr.CurrentType;
                    if (a.Types != null)
                    {
                        for (int i = 0; i < a.Types.Count; i++)
                        {
                            var t = a.Types[i];
                            if (i > 0)
                            {
                                var existing = mgr.Types.Cast<FamilyType>().FirstOrDefault(x => string.Equals(x.Name, t.Name.Trim(), StringComparison.OrdinalIgnoreCase));
                                if (existing != null) mgr.CurrentType = existing;
                                else mgr.NewType(t.Name.Trim());
                            }
                            mgr.Set(ph, (t.HeightMm ?? a.HeightMm) / Ft);
                            if (pw != null) mgr.Set(pw, (t.WidthMm ?? a.WidthMm.Value) / Ft);
                            if (pd != null) mgr.Set(pd, (t.DepthMm ?? a.DepthMm.Value) / Ft);
                        }
                        mgr.CurrentType = first;
                    }
                    typeNames = mgr.Types.Cast<FamilyType>().Select(t => t.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();

                    if (tx.Commit() != TransactionStatus.Committed)
                        throw new ToolException(ToolErrorCodes.TransactionRolledBack, "Revit rejected the family geometry: " + string.Join(" | ", failures.Errors));
                }

                flex = Flex(fam, extrusion, pw, pd, ph, w, d, h);

                if (!ctx.DryRun)
                {
                    try { fam.SaveAs(output, new SaveAsOptions { OverwriteExistingFile = a.Overwrite, MaximumBackups = 1 }); }
                    catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                    {
                        throw new ToolException("SAVE_FAILED", $"Revit could not save '{output}': {ex.Message}");
                    }
                }
            }
            finally
            {
                fam.Close(false);
            }

            object loaded = null;
            if (a.LoadIntoProject && !ctx.DryRun)
            {
                using (var own = new OwnTransactions(ctx, "create_family_from_template"))
                {
                    var family = own.Step("MCP: load new family", () =>
                    {
                        if (!doc.LoadFamily(output, new LoadOptions(), out Family f) || f == null)
                            f = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                                .FirstOrDefault(x => string.Equals(x.Name, familyName, StringComparison.OrdinalIgnoreCase));
                        if (f == null) throw new ToolException("LOAD_FAILED", $"Revit did not load '{output}'.");
                        return f;
                    });
                    own.Complete();
                    loaded = new
                    {
                        familyId = family.Id.GetValue(),
                        familyName = family.Name,
                        category = family.FamilyCategory?.Name,
                        types = family.GetFamilySymbolIds().Select(id => doc.GetElement(id)).OfType<FamilySymbol>()
                            .Select(s => new { id = s.Id.GetValue(), name = s.Name }).ToList()
                    };
                }
            }
            foreach (var n in notes) ctx.Warnings.Add(new ToolWarning(n));

            return new
            {
                template,
                path = ctx.DryRun ? null : output,
                familyName,
                profile = rect ? "rectangle" : "polygon",
                types = typeNames,
                locks,
                flex,
                loaded,
                changeSummary = ctx.DryRun
                    ? $"[dry run] Family '{familyName}' built and flexed in memory; nothing saved or loaded."
                    : $"Family '{familyName}' saved to {output}" + (loaded != null ? " and loaded." : ".")
            };
        }

        private static string DefaultTypeName(CreateFamilyFromTemplateArgs a)
        {
            string F(double v) => Math.Round(v).ToString(CultureInfo.InvariantCulture);
            return a.Polygon == null ? $"{F(a.WidthMm.Value)}x{F(a.DepthMm.Value)}x{F(a.HeightMm)}" : $"H{F(a.HeightMm)}";
        }

        private static FamilyParameter Param(FamilyManager mgr, string name, ForgeTypeId group, ForgeTypeId spec, bool instance)
        {
            name = name.Trim();
            var existing = mgr.get_Parameter(name);
            if (existing != null)
            {
                if (!existing.Definition.GetDataType().Equals(spec))
                    throw new ToolException("NAME_COLLISION", $"The template already has a parameter '{name}' of another type.", "Choose another parameter name.");
                return existing;
            }
            return mgr.AddParameter(name, group, spec, instance);
        }

        /// <summary>
        /// Width (alongX) or depth: two reference planes at ±size/2, extrusion faces locked to them, a labeled
        /// dimension and, with the template's center plane, an EQ dimension. Returns what was achieved.
        /// </summary>
        private static string LockAxis(Document fam, View plan, Extrusion extrusion, FamilyParameter p, bool alongX, double w, double d, List<string> notes)
        {
            var axisName = alongX ? "width" : "depth";
            try
            {
                double half = (alongX ? w : d) / 2, other = (alongX ? d : w) / 2, reach = Math.Max(w, d) + 1.0;
                XYZ At(double along, double across) => alongX ? new XYZ(along, across, 0) : new XYZ(across, along, 0);
                var normal = alongX ? XYZ.BasisX : XYZ.BasisY;
                var centers = new FilteredElementCollector(fam).OfClass(typeof(ReferencePlane)).Cast<ReferencePlane>()
                    .Where(r => Math.Abs(Math.Abs(r.GetPlane().Normal.DotProduct(normal)) - 1) < 1e-6 && Math.Abs(r.GetPlane().Origin.DotProduct(normal)) < 1e-6)
                    .ToList();
                var minus = fam.FamilyCreate.NewReferencePlane(At(-half, -reach), At(-half, reach), XYZ.BasisZ, plan);
                var plus = fam.FamilyCreate.NewReferencePlane(At(half, -reach), At(half, reach), XYZ.BasisZ, plan);
                Rename(fam, minus, alongX ? "Esquerda" : "Frente");
                Rename(fam, plus, alongX ? "Direita" : "Fundo");
                fam.Regenerate();

                var faces = ReferencePicker.PlanarFaces(extrusion);
                PlanarFace Face(XYZ dir) => faces.Select(f => f.Item1).FirstOrDefault(f => f.FaceNormal.DotProduct(dir) > 0.999);
                var fMinus = Face(-normal);
                var fPlus = Face(normal);
                if (fMinus == null || fPlus == null) throw new InvalidOperationException("the extrusion's side faces were not found");
                fam.FamilyCreate.NewAlignment(plan, minus.GetReference(), fMinus.Reference).IsLocked = true;
                fam.FamilyCreate.NewAlignment(plan, plus.GetReference(), fPlus.Reference).IsLocked = true;

                var off = 300 / Ft;
                var refs = new ReferenceArray();
                refs.Append(minus.GetReference());
                refs.Append(plus.GetReference());
                var dim = fam.FamilyCreate.NewDimension(plan, Line.CreateBound(At(-half, -other - off), At(half, -other - off)), refs);
                dim.FamilyLabel = p;

                if (centers.Count == 0)
                {
                    notes.Add($"No center reference plane for {axisName} in the template: {axisName} is labeled and locked but not kept centered (Revit picks which side moves).");
                    return "labeled and locked (not centered)";
                }
                var eqRefs = new ReferenceArray();
                eqRefs.Append(minus.GetReference());
                eqRefs.Append(centers[0].GetReference());
                eqRefs.Append(plus.GetReference());
                var eq = fam.FamilyCreate.NewDimension(plan, Line.CreateBound(At(-half, -other - 2 * off), At(half, -other - 2 * off)), eqRefs);
                eq.AreSegmentsEqual = true;
                return "labeled, locked and centered (EQ)";
            }
            catch (Exception ex)
            {
                notes.Add($"The {axisName} could not be locked to the geometry ({ex.Message}); '{p.Definition.Name}' exists but may not drive the shape.");
                return "not locked";
            }
        }

        private static void Rename(Document fam, ReferencePlane rp, string name)
        {
            var taken = new HashSet<string>(new FilteredElementCollector(fam).OfClass(typeof(ReferencePlane)).Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
            var n = name;
            for (int i = 2; taken.Contains(n); i++) n = $"{name} {i}";
            try { rp.Name = n; } catch { /* names are cosmetic */ }
        }

        /// <summary>Changes each size parameter by +100 mm, checks the extrusion follows, and rolls back.</summary>
        private static object Flex(Document fam, Extrusion extrusion, FamilyParameter pw, FamilyParameter pd, FamilyParameter ph, double w, double d, double h)
        {
            var result = new Dictionary<string, bool?>();
            var delta = 100 / Ft;
            void Test(string key, FamilyParameter p, double baseValue, Func<BoundingBoxXYZ, double> size)
            {
                if (p == null) return;
                using (var tx = new Transaction(fam, "MCP: flex"))
                {
                    try
                    {
                        tx.Start();
                        var before = size(extrusion.get_BoundingBox(null));
                        fam.FamilyManager.Set(p, baseValue + delta);
                        fam.Regenerate();
                        var after = size(extrusion.get_BoundingBox(null));
                        result[key] = Math.Abs(after - before - delta) < 1e-4;
                    }
                    catch { result[key] = false; }
                    finally { if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack(); }
                }
            }
            Test("width", pw, w, b => b.Max.X - b.Min.X);
            Test("depth", pd, d, b => b.Max.Y - b.Min.Y);
            Test("height", ph, h, b => b.Max.Z - b.Min.Z);
            return result;
        }

        // ---- templates ----------------------------------------------------------------------------------------

        private static List<string> TemplateRoots(Autodesk.Revit.ApplicationServices.Application app)
        {
            var roots = new List<string>();
            try { if (!string.IsNullOrWhiteSpace(app.FamilyTemplatePath)) roots.Add(app.FamilyTemplatePath); } catch { }
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk", "RVT " + app.VersionNumber, "Family Templates"));
            roots.AddRange(PathPolicy.AllowedRoots);
            return roots.Where(Directory.Exists).Select(r => Path.GetFullPath(r).TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string ResolveTemplate(Autodesk.Revit.ApplicationServices.Application app, string text)
        {
            var roots = TemplateRoots(app);
            if (!string.IsNullOrWhiteSpace(text) && (Path.IsPathRooted(text) || text.IndexOfAny(new[] { '\\', '/' }) >= 0))
            {
                var full = Path.GetFullPath(Path.IsPathRooted(text) ? text : Path.Combine(PathPolicy.DefaultRoot, text));
                bool allowed = PathPolicy.IsAllowed(full) || roots.Any(r => full.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase));
                if (!allowed) throw new ToolException("PATH_NOT_ALLOWED", $"'{full}' is outside the allowed folders and Revit's family template folders.");
                if (!full.EndsWith(".rft", StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                    throw new ToolException("FILE_NOT_FOUND", $"'{full}' is not an existing .rft file.");
                return full;
            }

            var all = new List<string>();
            foreach (var r in roots)
            {
                try { all.AddRange(Directory.EnumerateFiles(r, "*.rft", SearchOption.AllDirectories)); } catch { }
            }
            all = all.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (all.Count == 0)
                throw new ToolException("TEMPLATE_NOT_FOUND", "No family templates (.rft) were found in Revit's template folder or the allowed folders.",
                    "Pass template with the full path of an .rft in the allowed folders.");

            string Key(string p) => TextMatch.Fold(Path.GetFileNameWithoutExtension(p));
            if (string.IsNullOrWhiteSpace(text))
            {
                foreach (var wanted in new[] { "Modelo genérico métrico", "Metric Generic Model", "Generic Model", "Modelo genérico" })
                {
                    var hit = all.Where(p => Key(p) == TextMatch.Fold(wanted)).OrderBy(p => p.IndexOf("Portuguese", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1).FirstOrDefault();
                    if (hit != null) return hit;
                }
                throw new ToolException("TEMPLATE_NOT_FOUND", "The Generic Model family template was not found.", "Pass template (name or path).");
            }
            var key = TextMatch.Fold(text.EndsWith(".rft", StringComparison.OrdinalIgnoreCase) ? text.Substring(0, text.Length - 4) : text);
            var exact = all.Where(p => Key(p) == key).ToList();
            if (exact.Count > 0) return exact.OrderBy(p => p.IndexOf("Portuguese", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1).First();
            var partial = all.Where(p => Key(p).Contains(key)).ToList();
            if (partial.Count == 1) return partial[0];
            var suggestions = (partial.Count > 0 ? partial : all).Select(Path.GetFileNameWithoutExtension).Distinct().OrderBy(n => n).Take(25);
            throw new ToolException(partial.Count > 1 ? "AMBIGUOUS_NAME" : "TEMPLATE_NOT_FOUND",
                $"Template '{text}' " + (partial.Count > 1 ? "matches several" : "was not found") + ". Some available: " + string.Join("; ", suggestions) + ".");
        }
    }
}
