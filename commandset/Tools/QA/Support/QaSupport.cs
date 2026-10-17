using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.QA.Support
{
    /// <summary>One warning of the model, language-neutral key included.</summary>
    public sealed class ModelWarning
    {
        public Guid DefinitionId { get; set; }
        public string Severity { get; set; }
        public string Text { get; set; }
        public List<ElementId> Failing { get; set; }
        public List<ElementId> Additional { get; set; }

        public IEnumerable<ElementId> All => Failing.Concat(Additional);
    }

    /// <summary>Shared helpers for the QA tools.</summary>
    public static class QaSupport
    {
        public static List<ModelWarning> ReadWarnings(Document doc)
        {
            var list = new List<ModelWarning>();
            foreach (var w in doc.GetWarnings())
            {
                Guid def;
                try { def = w.GetFailureDefinitionId().Guid; }
                catch { def = Guid.Empty; }
                list.Add(new ModelWarning
                {
                    DefinitionId = def,
                    Severity = SafeSeverity(w),
                    Text = SafeText(w),
                    Failing = (w.GetFailingElements() ?? new List<ElementId>()).ToList(),
                    Additional = (w.GetAdditionalElements() ?? new List<ElementId>()).ToList()
                });
            }
            return list;
        }

        private static string SafeSeverity(FailureMessage w)
        {
            try { return w.GetSeverity().ToString(); }
            catch { return "Warning"; }
        }

        private static string SafeText(FailureMessage w)
        {
            try { return w.GetDescriptionText(); }
            catch { return string.Empty; }
        }

        /// <summary>Centre of the element's bounding box (in <paramref name="view"/> when given), in mm.</summary>
        public static object CenterMm(Element e, View view = null)
        {
            BoundingBoxXYZ bb = null;
            try { bb = e.get_BoundingBox(view) ?? e.get_BoundingBox(null); }
            catch { /* some elements throw */ }
            if (bb == null) return null;
            var c = (bb.Min + bb.Max) / 2;
            if (bb.Transform != null && !bb.Transform.IsIdentity) c = bb.Transform.OfPoint(c);
            return new { x = Mm(c.X), y = Mm(c.Y), z = Mm(c.Z) };
        }

        public static double Mm(double feet) => Math.Round(UnitPolicy.FromInternal(feet, UnitSpec.Length, out _), 1);

        public static string SafeName(Element e)
        {
            try { return e.Name; }
            catch { return null; }
        }

        /// <summary>Short description of an element for QA results.</summary>
        public static object Brief(Document doc, Element e, View view = null, bool withCenter = false)
        {
            if (e == null) return null;
            var type = ParameterService.TypeOf(e);
            return new
            {
                id = e.Id.GetValue(),
                name = SafeName(e),
                category = e.Category?.Name,
                familyName = type == null || type == e ? null : LookupService.SafeFamilyName(type),
                typeName = type == null || type == e ? null : type.Name,
                center = withCenter ? CenterMm(e, view) : null
            };
        }

        /// <summary>A view that can hold model elements and tags (not a template, schedule or sheet).</summary>
        public static View GraphicView(ToolContext ctx, string reference)
        {
            var v = ctx.Services.Lookup.View(reference);
            if (v is ViewSchedule || v is ViewSheet || v.ViewType == ViewType.ProjectBrowser || v.ViewType == ViewType.SystemBrowser)
                throw new ToolException("VIEW_NOT_SUPPORTED", $"'{v.Name}' is a {v.ViewType}; pick a plan, section, elevation or 3D view.");
            return v;
        }

        /// <summary>Stable key for files that belong to a document (title + hash of its path).</summary>
        public static string DocumentKey(Document doc)
        {
            var title = Export.Support.ExportSupport.SafeFileName(doc.Title, "document");
            var src = string.IsNullOrEmpty(doc.PathName) ? doc.Title : doc.PathName.ToLowerInvariant();
            using (var sha = SHA1.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes(src ?? string.Empty));
                return title + "-" + BitConverter.ToString(h, 0, 4).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        /// <summary>Where office conventions live: REVIT_MCP_KNOWLEDGE_DIR, else a knowledge folder next to / above the add-in.</summary>
        public static string KnowledgeFile(string fileName)
        {
            var env = Environment.GetEnvironmentVariable("REVIT_MCP_KNOWLEDGE_DIR");
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(env)) candidates.Add(env.Trim());
            candidates.Add(Path.Combine(PathPolicy.DefaultRoot, "knowledge"));
            try
            {
                var dir = Path.GetDirectoryName(typeof(QaSupport).Assembly.Location);
                for (int i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
                {
                    candidates.Add(Path.Combine(dir, "knowledge"));
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch
            {
                // no assembly location (e.g. loaded from bytes)
            }
            foreach (var c in candidates)
            {
                try
                {
                    var f = Path.Combine(c, fileName);
                    if (File.Exists(f)) return f;
                }
                catch
                {
                    // invalid path in the environment variable
                }
            }
            return null;
        }
    }

    /// <summary>Wall-clock budget for expensive checks: they stop and say truncated instead of freezing Revit.</summary>
    public sealed class TimeBudget
    {
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private readonly long _ms;

        public TimeBudget(long ms)
        {
            _ms = ms;
        }

        public bool Exceeded => _sw.ElapsedMilliseconds > _ms;
        public long ElapsedMs => _sw.ElapsedMilliseconds;
    }
}
