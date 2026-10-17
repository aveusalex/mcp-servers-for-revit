using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitMCPCommandSet.Tools.QA.Support
{
    /// <summary>What Revit's Purge Unused would offer.</summary>
    public static class UnusedFinder
    {
        /// <summary>
        /// "families", "types", "materials", "fillPatterns", "linePatterns", "viewTemplates", "other".
        /// </summary>
        public static readonly string[] Targets = { "families", "types", "materials", "fillPatterns", "linePatterns", "viewTemplates", "other" };

        /// <summary>
        /// Revit 2024+: <c>Document.GetUnusedElements</c> (the Purge Unused list). Earlier: the Performance Adviser rule
        /// "Project contains unused families and types" (families and types only).
        /// </summary>
        public static IList<ElementId> Find(Document doc, out string source)
        {
#if REVIT2024_OR_GREATER
            source = "Document.GetUnusedElements (same list as Purge Unused)";
            return doc.GetUnusedElements(new HashSet<ElementId>()).ToList();
#else
            source = "Performance Adviser purge rule (families and types only; Revit 2024+ also finds materials and patterns)";
            var rule = new PerformanceAdviserRuleId(new Guid("e8c63650-70b7-435a-9010-ec97660c1bda"));
            var adviser = PerformanceAdviser.GetPerformanceAdviser();
            var result = new List<ElementId>();
            var messages = adviser.ExecuteRules(doc, new List<PerformanceAdviserRuleId> { rule });
            foreach (var m in messages ?? new List<FailureMessage>())
                result.AddRange(m.GetFailingElements() ?? new List<ElementId>());
            return result.Distinct().ToList();
#endif
        }

        public static string Classify(Element e)
        {
            switch (e)
            {
                case Family _: return "families";
                case Material _: return "materials";
                case FillPatternElement _: return "fillPatterns";
                case LinePatternElement _: return "linePatterns";
                case View v when v.IsTemplate: return "viewTemplates";
                case ElementType _: return "types";
                default: return "other";
            }
        }
    }
}
