namespace RevitMCPCommandSet.Commands.ExecuteDynamicCode
{
    /// <summary>
    /// Heuristic, human-readable hints for send_code_to_revit failures. Pure string logic (no Revit API calls),
    /// keyed by compiler diagnostic id or by exception type name, so the AI can fix the snippet in one more try.
    /// </summary>
    public static class CodeErrorHints
    {
        private const string DocumentHint =
            "The target Document is available as 'document' and the call arguments as 'parameters' (object[]). " +
            "There is no 'doc', 'uidoc' or 'uiapp' variable; do not use ActiveUIDocument, it may point at another document.";

        public static IList<string> ForCompileErrors(IEnumerable<CompileError> errors)
        {
            var hints = new List<string>();
            foreach (var error in errors)
            {
                string hint = ForCompileError(error);
                if (hint != null && !hints.Contains(hint)) hints.Add(hint);
            }
            return hints.Count > 0 ? hints : null;
        }

        public static string ForCompileError(CompileError error)
        {
            string message = error.Message ?? string.Empty;
            switch (error.Code)
            {
                case "CS0161": // not all code paths return a value
                    return "The snippet is the body of 'object Execute(Document document, object[] parameters)': it must end with a return statement, e.g. 'return null;'.";
                case "CS0103": // name does not exist in the current context
                    if (ContainsAny(message, "'doc'", "'uidoc'", "'uiapp'", "'app'", "'commandData'"))
                        return DocumentHint;
                    return "A name is not defined. Check spelling, declare the variable, or qualify the type with its namespace.";
                case "CS0246": // type or namespace not found
                case "CS0234": // type does not exist in namespace
                    return "A type was not found. Only System, System.Linq, System.Collections.Generic, Autodesk.Revit.DB and Autodesk.Revit.UI are imported; " +
                           "use full names for others (e.g. Autodesk.Revit.DB.Architecture.Room, Autodesk.Revit.DB.Structure.StructuralType).";
                case "CS1061": // member not found
                case "CS0117":
                    if (ContainsAny(message, "IntegerValue"))
                        return "ElementId.IntegerValue was removed in Revit 2024+; use ElementId.Value (long). new ElementId(int) is also obsolete; use new ElementId((long)x).";
                    return "That member does not exist on the type in this Revit version. The Revit API changes between versions (2022-2027); check the member name and the Revit version.";
                case "CS0619": // obsolete with error
                case "CS0618":
                    return "That API is obsolete in this Revit version; use the replacement named in the message.";
                case "CS0029": // cannot implicitly convert
                case "CS0266":
                    return "Type mismatch. Revit lengths are doubles in internal units (feet); ElementIds are ElementId, not int/long; cast collector results with OfClass/Cast<T>().";
                case "CS1002":
                case "CS1513":
                case "CS1026":
                case "CS1525":
                    return "Syntax error. Send only C# statements (a method body), not a class or a full file, and check braces, parentheses and semicolons.";
                case "CS0136":
                case "CS0128":
                    return "A local variable name collides with 'document' or 'parameters' or is declared twice; rename it.";
            }
            return null;
        }

        public static IList<string> ForException(Exception ex, string transactionMode)
        {
            var hints = new List<string>();
            string typeName = ex.GetType().Name;
            string message = ex.Message ?? string.Empty;

            switch (typeName)
            {
                case "ModificationOutsideTransactionException":
                    hints.Add("The snippet changed the model without an open transaction. Use transactionMode 'auto' to commit the change, or 'rollback' to try it without saving.");
                    break;
                case "ModificationForbiddenException":
                    hints.Add("The document cannot be modified right now (read-only, a linked file, or modification forbidden in this context). Linked models are read-only; open the source RVT to edit it.");
                    break;
                case "InvalidObjectException":
                    hints.Add("A Revit object was used after it was deleted or after its transaction was rolled back. Re-fetch elements by id instead of holding on to them.");
                    break;
                case "NullReferenceException":
                case "ArgumentNullException":
                    hints.Add("Something was null: GetElement, LookupParameter, get_Parameter and FirstOrDefault return null when nothing matches. Check for null, and remember parameter names are localized (e.g. pt-BR) - prefer BuiltInParameter.");
                    break;
                case "InvalidCastException":
                    hints.Add("Wrong cast. Use OfClass(typeof(T)).Cast<T>() or 'as T' with a null check; FamilyInstance, Wall, etc. are not interchangeable.");
                    break;
                case "ArgumentOutOfRangeException":
                case "IndexOutOfRangeException":
                    hints.Add("An index or value was out of range. Check collection counts, and remember Revit lengths are in feet (1 ft = 304.8 mm).");
                    break;
                case "KeyNotFoundException":
                    hints.Add("A dictionary key was missing. Use TryGetValue.");
                    break;
                case "FormatException":
                    hints.Add("A string could not be parsed. Parse with CultureInfo.InvariantCulture; Revit in pt-BR uses ',' as decimal separator in display strings.");
                    break;
                case "ArgumentException":
                case "ArgumentsInconsistentException":
                    hints.Add("Revit rejected an argument. Check units (internal feet / radians), that ids belong to the target document, and that geometry is valid (non-zero length, closed loops).");
                    break;
                case "InvalidOperationException":
                    if (message.IndexOf("transaction", StringComparison.OrdinalIgnoreCase) >= 0 && transactionMode != ExecuteCodeEventHandler.TransactionModeNone)
                        hints.Add("A transaction is already open in 'auto' and 'rollback' modes: do not start your own Transaction (use SubTransaction), or use transactionMode 'none' if the code manages its own transactions.");
                    else
                        hints.Add("Revit refused the operation in the current state. Check that the element/view supports it and the document is not read-only.");
                    break;
                case "NotSupportedException":
                    hints.Add("Operation not supported for this element or Revit version.");
                    break;
            }

            return hints.Count > 0 ? hints : null;
        }

        private static bool ContainsAny(string text, params string[] needles)
        {
            return needles.Any(n => text.IndexOf(n, StringComparison.Ordinal) >= 0);
        }
    }
}
