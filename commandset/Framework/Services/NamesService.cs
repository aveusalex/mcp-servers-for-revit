using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Framework.Services
{
    /// <summary>
    /// Name validation and application against the document: forbidden characters (INVALID_NAME), uniqueness in the
    /// scope Revit uses (NAME_COLLISION), and a setter that reads the value back instead of trusting it — never
    /// <c>try { x.Name = n } catch { }</c>.
    /// </summary>
    public sealed class NamesService
    {
        private readonly Document _doc;

        public NamesService(Document doc)
        {
            _doc = doc;
        }

        public void Validate(string name, string what = "name") => NameRules.Validate(name, what);

        /// <summary>Sheet numbers are unique across the document (placeholders included).</summary>
        public IList<string> SheetNumbers(ElementId except = null) =>
            new FilteredElementCollector(_doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Where(s => except == null || s.Id != except).Select(s => s.SheetNumber).ToList();

        public void EnsureSheetNumberFree(string number, ElementId except = null)
        {
            NameRules.Validate(number, "sheet number");
            NameRules.EnsureUnique(number, SheetNumbers(except), "sheet number");
        }

        /// <summary>View names are unique per view type among non-template views; templates among templates.</summary>
        public void EnsureViewNameFree(string name, ViewType type, bool isTemplate = false, ElementId except = null)
        {
            NameRules.Validate(name, "view name");
            var names = new FilteredElementCollector(_doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => (except == null || v.Id != except) && v.IsTemplate == isTemplate &&
                            (isTemplate || v.ViewType == type))
                .Select(v => v.Name);
            NameRules.EnsureUnique(name, names, "view name");
        }

        /// <summary>Type names are unique per family (loadable) or per system family / category.</summary>
        public void EnsureTypeNameFree(ElementType type, string name)
        {
            NameRules.Validate(name, "type name");
            var family = LookupService.SafeFamilyName(type);
            var names = new FilteredElementCollector(_doc).WhereElementIsElementType().Cast<ElementType>()
                .Where(t => t.Id != type.Id && t.GetType() == type.GetType() &&
                            string.Equals(LookupService.SafeFamilyName(t), family, StringComparison.Ordinal))
                .Select(t => t.Name);
            NameRules.EnsureUnique(name, names, "type name");
        }

        /// <summary>Names unique across the document for these classes (levels, grids, materials...).</summary>
        public void EnsureUniqueInClass(Type elementClass, string name, string what, ElementId except = null)
        {
            NameRules.Validate(name, what);
            var names = new FilteredElementCollector(_doc).OfClass(elementClass).ToElements()
                .Where(e => except == null || e.Id != except).Select(e => e.Name);
            NameRules.EnsureUnique(name, names, what);
        }

        /// <summary>Sets <see cref="Element.Name"/> and reads it back. Must run inside the tool's transaction.</summary>
        public void ApplyName(Element element, string name, string what = "name") =>
            Apply(() => element.Name, v => element.Name = v, name, what);

        public void ApplySheetNumber(ViewSheet sheet, string number) =>
            Apply(() => sheet.SheetNumber, v => sheet.SheetNumber = v, number, "sheet number");

        private static void Apply(Func<string> get, Action<string> set, string requested, string what)
        {
            NameRules.Validate(requested, what);
            try
            {
                set(requested);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex)
            {
                throw new ToolException("INVALID_NAME", $"Revit rejected the {what} '{requested}': {ex.Message}");
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
            {
                throw new ToolException("INVALID_NAME", $"Revit rejected the {what} '{requested}': {ex.Message}");
            }
            var actual = get();
            if (!string.Equals(actual, requested, StringComparison.Ordinal))
                throw new ToolException("NAME_NOT_APPLIED", $"Revit kept the {what} '{actual}' instead of '{requested}'.");
        }
    }
}
