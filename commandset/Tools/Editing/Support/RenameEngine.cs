using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Parameters.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Editing.Support
{
    /// <summary>One planned rename.</summary>
    public sealed class RenameItem
    {
        public Element Element;
        public string Kind;
        public string Field;
        public string ScopeKey;
        public string Before;
        public string After;
        public string Status = "planned";
        public string Message;
        public int? InstancesAffected;

        public object ToResult() => new
        {
            elementId = Element.Id.GetValue(),
            kind = Kind,
            field = Field,
            before = Before,
            after = After,
            status = Status,
            message = Message,
            instancesAffected = InstancesAffected
        };
    }

    /// <summary>
    /// Batch rename shared by rename_elements and rename_types: computes new names with a <see cref="TextOperation"/>,
    /// validates forbidden characters and collisions (inside the batch and against the document, in the scope Revit
    /// uses), then applies them in two passes when names are swapped, reading every name back.
    /// </summary>
    public sealed class RenameEngine
    {
        private const string TempPrefix = "zz-mcp-tmp-";
        private readonly Document _doc;
        private readonly NamesService _names;
        private readonly Dictionary<string, List<string>> _existing = new Dictionary<string, List<string>>();

        public RenameEngine(Document doc, NamesService names)
        {
            _doc = doc;
            _names = names;
        }

        /// <summary>Plans the new names. <paramref name="sheetField"/> is "name" or "number" for sheets.</summary>
        public List<RenameItem> Plan(IList<Element> elements, TextOperation op, int start, string sheetField, bool countInstances)
        {
            var items = new List<RenameItem>();
            foreach (var e in elements) _renamedIds.Add(e.Id.GetValue().ToString(CultureInfo.InvariantCulture));
            int n = start;
            foreach (var e in elements)
            {
                var item = Describe(e, sheetField);
                items.Add(item);
                if (item.Status != "planned") continue;
                if (op.NeedsCurrent && !op.Matches(item.Before))
                {
                    item.Status = "no_match";
                    item.After = item.Before;
                    continue;
                }
                item.After = op.Apply(item.Before, n++);
                if (string.Equals(item.After, item.Before, StringComparison.Ordinal))
                {
                    item.Status = "unchanged";
                    continue;
                }
                if (countInstances && e is ElementType t) item.InstancesAffected = CountInstances(t);
                if (countInstances && e is Family f) item.InstancesAffected = f.GetFamilySymbolIds().Sum(id => CountInstances((ElementType)_doc.GetElement(id)));
            }

            var renaming = items.Where(i => i.Status == "planned").ToList();
            foreach (var i in renaming)
            {
                if (i.Kind == "room") continue; // room names need not be unique and allow any text
                try
                {
                    NameRules.Validate(i.After, i.Field);
                }
                catch (ToolException ex)
                {
                    i.Status = "invalid_name";
                    i.Message = ex.Message;
                }
            }

            // Collisions inside the batch (same scope, same final name).
            foreach (var g in renaming.Where(i => i.Status == "planned" && i.ScopeKey != null)
                         .GroupBy(i => i.ScopeKey + "\u0001" + i.After.ToLowerInvariant()).Where(g => g.Count() > 1))
            {
                foreach (var i in g)
                {
                    i.Status = "collision";
                    i.Message = $"{g.Count()} elements would be named '{i.After}'.";
                }
            }

            // Collisions with names kept by elements outside the batch (or inside it but not renamed).
            var finalNames = items.ToDictionary(i => i.Element.Id.GetValue(), i => i.Status == "planned" ? i.After : i.Before);
            foreach (var i in renaming.Where(i => i.Status == "planned" && i.ScopeKey != null))
            {
                var clash = ExistingNames(i).Any(x => string.Equals(x, i.After, StringComparison.OrdinalIgnoreCase)) ||
                            items.Any(o => o != i && o.ScopeKey == i.ScopeKey && o.Status != "planned" &&
                                           string.Equals(finalNames[o.Element.Id.GetValue()], i.After, StringComparison.OrdinalIgnoreCase));
                if (clash)
                {
                    i.Status = "collision";
                    i.Message = $"'{i.After}' is already used by another {i.Kind} in the same scope.";
                }
            }
            return items;
        }

        /// <summary>Applies the planned items (two passes when names are swapped). Inside the tool's transaction.</summary>
        public void Apply(IList<RenameItem> items)
        {
            var planned = items.Where(i => i.Status == "planned").ToList();
            var oldNames = new HashSet<string>(planned.Where(i => i.ScopeKey != null).Select(i => i.ScopeKey + "\u0001" + i.Before.ToLowerInvariant()));
            var needsTemp = planned.Where(i => i.ScopeKey != null && oldNames.Contains(i.ScopeKey + "\u0001" + i.After.ToLowerInvariant())).ToList();
            foreach (var i in needsTemp)
                Set(i, TempPrefix + i.Element.Id.GetValue().ToString(CultureInfo.InvariantCulture));
            foreach (var i in planned)
            {
                Set(i, i.After);
                i.Status = "ok";
            }
        }

        private void Set(RenameItem i, string value)
        {
            switch (i.Kind)
            {
                case "room":
                {
                    var p = i.Element.get_Parameter(BuiltInParameter.ROOM_NAME);
                    if (p == null || p.IsReadOnly || !p.Set(value) || p.AsString() != value)
                        throw new ToolException("NAME_NOT_APPLIED", $"Revit did not keep the name '{value}' on element {i.Element.Id.GetValue()}.");
                    break;
                }
                case "sheet" when i.Field == "sheet number":
                    _names.ApplySheetNumber((ViewSheet)i.Element, value);
                    break;
                default:
                    try
                    {
                        _names.ApplyName(i.Element, value, i.Field);
                    }
                    catch (ToolException ex) when (value.StartsWith(TempPrefix, StringComparison.Ordinal))
                    {
                        throw new ToolException(ex.Code, ex.Message + " (temporary name of the swap pass)");
                    }
                    break;
            }
        }

        private RenameItem Describe(Element e, string sheetField)
        {
            var item = new RenameItem { Element = e };
            switch (e)
            {
                case ViewSheet sheet:
                    item.Kind = "sheet";
                    if (sheetField == "number")
                    {
                        item.Field = "sheet number";
                        item.Before = sheet.SheetNumber;
                        item.ScopeKey = "sheetNumber";
                    }
                    else
                    {
                        item.Field = "sheet name";
                        item.Before = sheet.Name;
                        item.ScopeKey = null; // sheet names may repeat
                    }
                    break;
                case View view:
                    item.Kind = view.IsTemplate ? "view template" : "view";
                    item.Field = "view name";
                    item.Before = view.Name;
                    item.ScopeKey = view.IsTemplate ? "viewTemplate" : "view:" + view.ViewType;
                    break;
                case SpatialElement _:
                    item.Kind = "room";
                    item.Field = "room name";
                    item.Before = e.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? string.Empty;
                    item.ScopeKey = null;
                    break;
                case Family family:
                    item.Kind = "family";
                    item.Field = "family name";
                    item.Before = family.Name;
                    item.ScopeKey = "family";
                    break;
                case ElementType type:
                    item.Kind = "type";
                    item.Field = "type name";
                    item.Before = type.Name;
                    item.ScopeKey = "type:" + type.GetType().Name + ":" + (LookupService.SafeFamilyName(type) ?? string.Empty);
                    break;
                default:
                    item.Kind = e.Category?.Name ?? e.GetType().Name;
                    item.Field = "name";
                    item.Before = SafeName(e);
                    item.ScopeKey = "class:" + e.GetType().FullName;
                    if (item.Before == null)
                    {
                        item.Status = "not_renamable";
                        item.Message = "This element has no name of its own.";
                    }
                    break;
            }
            item.Before = item.Before ?? string.Empty;
            return item;
        }

        private IEnumerable<string> ExistingNames(RenameItem i)
        {
            if (_existing.TryGetValue(i.ScopeKey, out var cached))
                return Without(cached, i);
            IEnumerable<Element> pool;
            var e = i.Element;
            switch (i.Kind)
            {
                case "sheet":
                    pool = new FilteredElementCollector(_doc).OfClass(typeof(ViewSheet)).ToElements();
                    break;
                case "view":
                case "view template":
                    var v = (View)e;
                    pool = new FilteredElementCollector(_doc).OfClass(typeof(View)).Cast<View>()
                        .Where(x => !(x is ViewSheet) && x.IsTemplate == v.IsTemplate && (v.IsTemplate || x.ViewType == v.ViewType));
                    break;
                case "family":
                    pool = new FilteredElementCollector(_doc).OfClass(typeof(Family)).ToElements();
                    break;
                case "type":
                    var fam = LookupService.SafeFamilyName((ElementType)e);
                    pool = new FilteredElementCollector(_doc).WhereElementIsElementType().Cast<ElementType>()
                        .Where(t => t.GetType() == e.GetType() && string.Equals(LookupService.SafeFamilyName(t), fam, StringComparison.Ordinal));
                    break;
                default:
                    pool = new FilteredElementCollector(_doc).OfClass(e.GetType()).ToElements();
                    break;
            }
            var list = pool.Select(x => (id: x.Id.GetValue(), name: i.Kind == "sheet" ? ((ViewSheet)x).SheetNumber : SafeName(x)))
                .Where(x => x.name != null).Select(x => x.id.ToString(CultureInfo.InvariantCulture) + "\u0001" + x.name).ToList();
            _existing[i.ScopeKey] = list;
            return Without(list, i);
        }

        /// <summary>Names of the scope minus the element itself and minus elements being renamed (checked separately).</summary>
        private IEnumerable<string> Without(List<string> entries, RenameItem i)
        {
            var self = i.Element.Id.GetValue().ToString(CultureInfo.InvariantCulture) + "\u0001";
            return entries.Where(x => !x.StartsWith(self, StringComparison.Ordinal) && !_renamedIds.Contains(x.Substring(0, x.IndexOf('\u0001'))))
                .Select(x => x.Substring(x.IndexOf('\u0001') + 1));
        }

        /// <summary>Ids in the batch: their names are checked against the batch's final names instead.</summary>
        private readonly HashSet<string> _renamedIds = new HashSet<string>(StringComparer.Ordinal);

        private int CountInstances(ElementType t)
        {
            if (t == null) return 0;
            if (t is FamilySymbol)
                return new FilteredElementCollector(_doc).WherePasses(new FamilyInstanceFilter(_doc, t.Id)).GetElementCount();
            var c = new FilteredElementCollector(_doc).WhereElementIsNotElementType();
            if (t.Category != null) c = c.OfCategoryId(t.Category.Id);
            return c.ToElements().Count(x => x.GetTypeId() == t.Id);
        }

        internal static string SafeName(Element e)
        {
            try { return e.Name; }
            catch { return null; }
        }
    }
}
