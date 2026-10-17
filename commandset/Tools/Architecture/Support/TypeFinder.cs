using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Architecture.Support
{
    /// <summary>
    /// Resolves a type the caller named explicitly ("Family: Type", type name or id). Never falls back to the first
    /// type of the category: a missing name is an error listing what exists.
    /// </summary>
    public static class TypeFinder
    {
        public static T Resolve<T>(Document doc, string text, string what, Func<T, bool> filter = null) where T : ElementType
        {
            var list = new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>()
                .Where(t => filter == null || filter(t)).ToList();
            if (string.IsNullOrWhiteSpace(text))
                throw Required(what, list.Select(LookupService.FullName));
            if (long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0)
            {
                var byId = list.FirstOrDefault(t => t.Id.GetValue() == id);
                if (byId != null) return byId;
            }
            FamilyTypeName.Parse(text, out var family, out var type);
            var pool = family == null
                ? list
                : list.Where(t => string.Equals(TextMatch.Key(LookupService.SafeFamilyName(t) ?? ""), TextMatch.Key(family), StringComparison.Ordinal)).ToList();
            if (family != null && pool.Count == 0)
                throw NameMatcher.NotFound(text, list.Select(LookupService.FullName), what);
            try
            {
                return NameMatcher.Single(type, pool.Select(t => new NamedCandidate<T>(t.Name, t, LookupService.FullName(t) + ", id " + t.Id.GetValue())), what);
            }
            catch (ToolException ex) when (ex.Code == "NOT_FOUND")
            {
                var names = list.Select(LookupService.FullName).Take(30).ToList();
                throw new ToolException("TYPE_NOT_FOUND", ex.Message,
                    (ex.Hint ?? "") + (names.Count > 0 ? " Available: " + string.Join("; ", names) + "." : $" The document has no {what}."));
            }
        }

        /// <summary>Any element type of a category (e.g. wall sweep types), by "Family: Type", type name or id.</summary>
        public static ElementType ResolveInCategory(Document doc, string text, BuiltInCategory category, string what)
        {
            var list = new FilteredElementCollector(doc).WhereElementIsElementType().OfCategory(category)
                .Cast<ElementType>().ToList();
            if (string.IsNullOrWhiteSpace(text))
                throw Required(what, list.Select(LookupService.FullName));
            if (long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0)
            {
                var byId = list.FirstOrDefault(t => t.Id.GetValue() == id);
                if (byId != null) return byId;
            }
            FamilyTypeName.Parse(text, out var family, out var type);
            var pool = family == null
                ? list
                : list.Where(t => string.Equals(TextMatch.Key(LookupService.SafeFamilyName(t) ?? ""), TextMatch.Key(family), StringComparison.Ordinal)).ToList();
            if (pool.Count == 0)
                throw new ToolException("TYPE_NOT_FOUND", $"No {what} '{text}' exists.",
                    list.Count > 0 ? "Available: " + string.Join("; ", list.Take(30).Select(LookupService.FullName)) + "." : $"The document has no {what}.");
            return NameMatcher.Single(type, pool.Select(t => new NamedCandidate<ElementType>(t.Name, t, LookupService.FullName(t) + ", id " + t.Id.GetValue())), what);
        }

        public static ToolException Required(string what, IEnumerable<string> available)
        {
            var names = available.Take(30).ToList();
            return new ToolException("TYPE_REQUIRED", $"Name the {what} explicitly; it is never chosen for you.",
                names.Count > 0 ? "Available: " + string.Join("; ", names) + "." : $"The document has no {what}.");
        }

        /// <summary>
        /// A family type, optionally restricted to categories. Ambiguity returns the candidates instead of throwing so a
        /// batch item can report them (placed:false).
        /// </summary>
        public static FamilySymbol Symbol(Document doc, string text, IList<BuiltInCategory> categories, out IList<object> candidates)
        {
            candidates = null;
            var collector = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol));
            if (categories != null && categories.Count == 1) collector = collector.OfCategory(categories[0]);
            else if (categories != null && categories.Count > 1)
                collector = collector.WherePasses(new ElementMulticategoryFilter(categories.ToList()));
            var list = collector.Cast<FamilySymbol>().ToList();
            if (string.IsNullOrWhiteSpace(text))
                throw Required("family type", list.Select(LookupService.FullName));
            if (long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0)
            {
                var byId = list.FirstOrDefault(s => s.Id.GetValue() == id);
                if (byId != null) return byId;
            }
            FamilyTypeName.Parse(text, out var family, out var type);
            var pool = family == null
                ? list
                : list.Where(s => string.Equals(TextMatch.Key(s.FamilyName), TextMatch.Key(family), StringComparison.Ordinal)).ToList();
            var named = pool.Select(s => new NamedCandidate<FamilySymbol>(s.Name, s)).ToList();
            var hits = family != null && pool.Count == 0 ? new List<NamedCandidate<FamilySymbol>>() : NameMatcher.FindBest(type, named);
            if (hits.Count == 1) return hits[0].Item;
            if (hits.Count > 1)
            {
                candidates = hits.Take(30).Select(h => (object)new
                {
                    id = h.Item.Id.GetValue(),
                    familyType = LookupService.FullName(h.Item),
                    category = h.Item.Category?.Name
                }).ToList();
                return null;
            }
            var suggestions = TextMatch.Suggest(text, list.Select(LookupService.FullName));
            throw new ToolException("TYPE_NOT_FOUND", $"No family type '{text}' is loaded" +
                                                      (categories != null && categories.Count > 0 ? " in the requested category." : "."),
                suggestions.Count > 0
                    ? "Did you mean: " + string.Join(", ", suggestions.Select(s => $"'{s}'")) + "?"
                    : "Load the family first, or list the loaded types (get_available_family_types).",
                "get_available_family_types");
        }

        public static void Activate(Document doc, FamilySymbol symbol)
        {
            if (!symbol.IsActive)
            {
                symbol.Activate();
                doc.Regenerate();
            }
        }
    }
}
