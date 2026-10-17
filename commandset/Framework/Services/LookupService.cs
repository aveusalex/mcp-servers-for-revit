using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Framework.Services
{
    /// <summary>An element found by reference, possibly inside a Revit link.</summary>
    public sealed class ResolvedElement
    {
        public Element Element { get; set; }

        /// <summary>The document that owns the element (the link's document for a linked element).</summary>
        public Document Document { get; set; }

        /// <summary>Set when the element lives inside a link (read-only).</summary>
        public RevitLinkInstance Link { get; set; }

        public bool IsLinked => Link != null;
        public ElementRef Ref { get; set; }
    }

    /// <summary>
    /// Finds elements by id / UniqueId / "linkInstanceId:elementId", and levels, phases, views, view templates,
    /// worksets and types by name. A name that matches several elements fails with AMBIGUOUS_NAME and the
    /// candidates; an unknown one with NOT_FOUND and suggestions. Ids are accepted wherever a name is.
    /// </summary>
    public sealed class LookupService
    {
        private readonly Document _doc;

        public LookupService(Document doc)
        {
            _doc = doc;
        }

        // ---- elements -------------------------------------------------------------------------------------------

        /// <summary>Resolves any reference, including linked elements. Throws ELEMENT_NOT_FOUND.</summary>
        public ResolvedElement Resolve(string reference)
        {
            var r = ElementRef.Parse(reference);
            switch (r.Kind)
            {
                case ElementRefKind.Id:
                {
                    var e = _doc.GetElement(r.Id.ToRevitElementId());
                    if (e == null) throw ElementNotFound(reference);
                    return new ResolvedElement { Element = e, Document = _doc, Ref = r };
                }
                case ElementRefKind.UniqueId:
                {
                    var e = _doc.GetElement(r.UniqueId);
                    if (e == null) throw ElementNotFound(reference);
                    return new ResolvedElement { Element = e, Document = _doc, Ref = r };
                }
                default:
                {
                    var link = _doc.GetElement(r.LinkInstanceId.ToRevitElementId()) as RevitLinkInstance;
                    if (link == null)
                        throw new ToolException("ELEMENT_NOT_FOUND", $"{r.LinkInstanceId} is not a Revit link instance in this document.",
                            "Use list_revit_links to find link instance ids.");
                    var linkDoc = link.GetLinkDocument();
                    if (linkDoc == null)
                        throw new ToolException("LINK_NOT_LOADED", $"The link {r.LinkInstanceId} is not loaded.");
                    var e = linkDoc.GetElement(r.Id.ToRevitElementId());
                    if (e == null) throw ElementNotFound(reference);
                    return new ResolvedElement { Element = e, Document = linkDoc, Link = link, Ref = r };
                }
            }
        }

        /// <summary>Resolves an element of this document; linked references fail with LINK_READ_ONLY.</summary>
        public Element Element(string reference)
        {
            var r = Resolve(reference);
            if (r.IsLinked)
                throw new ToolException("LINK_READ_ONLY", $"'{reference}' is inside a Revit link, which is read-only.",
                    "Open the linked RVT as a normal document and target it instead.");
            return r.Element;
        }

        public Element Element(long id) => Element(id.ToString(CultureInfo.InvariantCulture));

        private static ToolException ElementNotFound(string reference) =>
            new ToolException("ELEMENT_NOT_FOUND", $"No element '{reference}' exists in the target document.",
                "Check the id, or use find_elements to look it up.", "find_elements");

        // ---- by name --------------------------------------------------------------------------------------------

        public Level Level(string nameOrId) =>
            ByNameOrId(nameOrId, Collect<Level>(), "level");

        public Phase Phase(string nameOrId) =>
            ByNameOrId(nameOrId, _doc.Phases.Cast<Phase>().ToList(), "phase");

        /// <summary>A view that is not a template (sheets included: a sheet is a view).</summary>
        public View View(string nameOrId) =>
            ByNameOrId(nameOrId, Collect<View>().Where(v => !v.IsTemplate).ToList(), "view",
                v => v.ViewType.ToString());

        public View ViewTemplate(string nameOrId) =>
            ByNameOrId(nameOrId, Collect<View>().Where(v => v.IsTemplate).ToList(), "view template",
                v => v.ViewType.ToString());

        public Material Material(string nameOrId) =>
            ByNameOrId(nameOrId, Collect<Material>(), "material");

        public Workset Workset(string nameOrId)
        {
            if (!_doc.IsWorkshared)
                throw new ToolException("NOT_WORKSHARED", "The target document does not use worksets.");
            var worksets = new FilteredWorksetCollector(_doc).OfKind(WorksetKind.UserWorkset).ToWorksets().ToList();
            if (long.TryParse(nameOrId?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var wid))
            {
                var byId = worksets.FirstOrDefault(w => w.Id.IntegerValue == wid);
                if (byId != null) return byId;
            }
            return NameMatcher.Single(nameOrId,
                worksets.Select(w => new NamedCandidate<Workset>(w.Name, w, "id " + w.Id.IntegerValue)), "workset");
        }

        /// <summary>
        /// A family type by "Family: Type" (as Revit shows it) or by type name alone, optionally limited to a category.
        /// </summary>
        public FamilySymbol FamilySymbol(string text, BuiltInCategory? category = null)
        {
            var symbols = new FilteredElementCollector(_doc).OfClass(typeof(FamilySymbol));
            if (category.HasValue) symbols = symbols.OfCategory(category.Value);
            var list = symbols.Cast<FamilySymbol>().ToList();
            if (TryId(text, out var id))
            {
                var byId = list.FirstOrDefault(s => s.Id.GetValue() == id);
                if (byId != null) return byId;
            }

            FamilyTypeName.Parse(text, out var family, out var type);
            if (family != null)
            {
                var inFamily = list.Where(s =>
                    string.Equals(TextMatch.Key(s.FamilyName), TextMatch.Key(family), StringComparison.Ordinal)).ToList();
                if (inFamily.Count == 0)
                    throw NameMatcher.NotFound(text, list.Select(FullName), "family type");
                return NameMatcher.Single(type, inFamily.Select(s => new NamedCandidate<FamilySymbol>(s.Name, s, FullName(s))),
                    "family type");
            }
            return NameMatcher.Single(type, list.Select(s => new NamedCandidate<FamilySymbol>(s.Name, s, FullName(s))),
                "family type");
        }

        /// <summary>Any element type (system or loadable) by "Family: Type" or type name, optionally in a category.</summary>
        public ElementType ElementType(string text, BuiltInCategory? category = null)
        {
            var types = new FilteredElementCollector(_doc).WhereElementIsElementType();
            if (category.HasValue) types = types.OfCategory(category.Value);
            var list = types.Cast<ElementType>().ToList();
            if (TryId(text, out var id))
            {
                var byId = list.FirstOrDefault(t => t.Id.GetValue() == id);
                if (byId != null) return byId;
            }
            FamilyTypeName.Parse(text, out var family, out var type);
            var pool = family == null
                ? list
                : list.Where(t => string.Equals(TextMatch.Key(t.FamilyName), TextMatch.Key(family), StringComparison.Ordinal)).ToList();
            if (family != null && pool.Count == 0)
                throw NameMatcher.NotFound(text, list.Select(FullName), "type");
            return NameMatcher.Single(type, pool.Select(t => new NamedCandidate<ElementType>(t.Name, t, FullName(t))), "type");
        }

        public static string FullName(ElementType t)
        {
            var fam = SafeFamilyName(t);
            return string.IsNullOrEmpty(fam) ? t.Name : fam + ": " + t.Name;
        }

        public static string SafeFamilyName(ElementType t)
        {
            try { return t.FamilyName; }
            catch { return null; }
        }

        /// <summary>Generic "name or id" lookup over a list of elements.</summary>
        public T ByNameOrId<T>(string nameOrId, IList<T> items, string what, Func<T, string> detail = null) where T : Element
        {
            if (string.IsNullOrWhiteSpace(nameOrId))
                throw new ToolException("INVALID_ARGUMENTS", $"A {what} name is required.");
            if (TryId(nameOrId, out var id))
            {
                var byId = items.FirstOrDefault(i => i.Id.GetValue() == id);
                if (byId != null) return byId;
            }
            return NameMatcher.Single(nameOrId,
                items.Select(i => new NamedCandidate<T>(i.Name, i,
                    detail == null ? "id " + i.Id.GetValue() : detail(i) + ", id " + i.Id.GetValue())), what);
        }

        private List<T> Collect<T>() where T : Element =>
            new FilteredElementCollector(_doc).OfClass(typeof(T)).Cast<T>().ToList();

        private static bool TryId(string text, out long id) =>
            long.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out id) && id > 0;
    }
}
