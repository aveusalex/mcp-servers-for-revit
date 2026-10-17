using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Framework.Services
{
    /// <summary>A resolved category: the enum (when built-in), the document's category (when it has one) and names.</summary>
    public sealed class ResolvedCategory
    {
        public BuiltInCategory? BuiltIn { get; set; }
        public Category Category { get; set; }
        public ElementId Id { get; set; }

        /// <summary>"OST_Walls" or null for a non built-in category.</summary>
        public string BuiltInName { get; set; }

        /// <summary>Name in the language Revit runs in.</summary>
        public string Name { get; set; }
    }

    /// <summary>
    /// Language-neutral category resolver over a document: "OST_Walls", "Walls", "Paredes" or the localized name.
    /// Unknown names fail with CATEGORY_NOT_FOUND and suggestions (see <see cref="CategoryCatalog"/>).
    /// </summary>
    public sealed class CategoryService
    {
        private readonly Document _doc;
        private List<CategoryEntry> _entries;

        public CategoryService(Document doc)
        {
            _doc = doc;
        }

        public ResolvedCategory Resolve(string name)
        {
            var entry = CategoryCatalog.Resolve(name, Entries(), CanonicalBuiltIn);
            var result = new ResolvedCategory { BuiltInName = entry.BuiltInName, Name = entry.Name };
            if (entry.BuiltInName != null && Enum.TryParse(entry.BuiltInName, out BuiltInCategory bic))
            {
                result.BuiltIn = bic;
                result.Id = new ElementId(bic);
                try { result.Category = Category.GetCategory(_doc, bic); }
                catch { /* some enum values have no Category object */ }
            }
            else
            {
                result.Id = entry.Id.ToRevitElementId();
                result.Category = _doc.Settings.Categories.Cast<Category>().FirstOrDefault(c => c.Id.GetValue() == entry.Id);
            }
            if (result.Name == null) result.Name = result.Category?.Name ?? entry.BuiltInName;
            return result;
        }

        public IList<ResolvedCategory> ResolveAll(IEnumerable<string> names) =>
            (names ?? Enumerable.Empty<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).Select(Resolve).ToList();

        /// <summary>Enum name of a category, or null for a non built-in one.</summary>
        public static string BuiltInNameOf(Category category)
        {
            if (category == null) return null;
            var v = category.Id.GetValue();
            if (v >= 0 || v < int.MinValue) return null;
            var bic = (BuiltInCategory)(int)v;
            return Enum.IsDefined(typeof(BuiltInCategory), bic) ? bic.ToString() : null;
        }

        private List<CategoryEntry> Entries()
        {
            if (_entries != null) return _entries;
            _entries = new List<CategoryEntry>();
            foreach (Category c in _doc.Settings.Categories)
                _entries.Add(new CategoryEntry(BuiltInNameOf(c), c.Name, c.Id.GetValue()));
            return _entries;
        }

        private static string CanonicalBuiltIn(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate) || candidate.IndexOf(',') >= 0) return null;
            if (!Enum.TryParse(candidate, true, out BuiltInCategory bic)) return null;
            if (!Enum.IsDefined(typeof(BuiltInCategory), bic) || bic == BuiltInCategory.INVALID) return null;
            // Reject numeric strings ("OST_" prefix guarantees letters, but be explicit).
            var name = bic.ToString();
            return name.StartsWith("OST_", StringComparison.Ordinal) ? name : null;
        }
    }
}
