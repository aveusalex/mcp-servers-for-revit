using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Framework
{
    /// <summary>
    /// Failure handling for MCP transactions: warnings are deleted and their text copied to the call's warnings;
    /// any error rolls the transaction back silently. Revit never shows a dialog for an MCP call.
    /// </summary>
    /// <remarks>Idea from bimwright's SafeFailuresPreprocessor (Apache-2.0), rewritten.</remarks>
    public sealed class SafeFailuresPreprocessor : IFailuresPreprocessor
    {
        private readonly IList<ToolWarning> _warnings;
        private readonly List<string> _errors = new List<string>();

        public SafeFailuresPreprocessor(IList<ToolWarning> warnings)
        {
            _warnings = warnings;
        }

        /// <summary>Text of the errors that forced the rollback.</summary>
        public IReadOnlyList<string> Errors => _errors;

        public bool HadErrors => _errors.Count > 0;

        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            bool hasError = false;
            foreach (var failure in accessor.GetFailureMessages())
            {
                var severity = failure.GetSeverity();
                var text = SafeText(failure);
                var ids = SafeIds(failure);

                if (severity == FailureSeverity.Warning)
                {
                    _warnings.Add(new ToolWarning(text, ids));
                    accessor.DeleteWarning(failure);
                }
                else if (severity != FailureSeverity.None)
                {
                    hasError = true;
                    _errors.Add(text);
                    _warnings.Add(new ToolWarning(text, ids, "error"));
                }
            }

            if (!hasError)
            {
                return FailureProcessingResult.Continue;
            }

            var options = accessor.GetFailureHandlingOptions();
            options.SetClearAfterRollback(true);
            accessor.SetFailureHandlingOptions(options);
            return FailureProcessingResult.ProceedWithRollBack;
        }

        /// <summary>Attaches this preprocessor to a transaction with modal handling off.</summary>
        public static SafeFailuresPreprocessor Attach(Transaction transaction, IList<ToolWarning> warnings)
        {
            var preprocessor = new SafeFailuresPreprocessor(warnings);
            var options = transaction.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(preprocessor);
            options.SetForcedModalHandling(false);
            options.SetClearAfterRollback(true);
            transaction.SetFailureHandlingOptions(options);
            return preprocessor;
        }

        private static string SafeText(FailureMessageAccessor failure)
        {
            try
            {
                return failure.GetDescriptionText();
            }
            catch
            {
                return "Revit reported a failure without a description.";
            }
        }

        private static IList<long> SafeIds(FailureMessageAccessor failure)
        {
            try
            {
                return failure.GetFailingElementIds().Select(id => id.GetValue()).ToList();
            }
            catch
            {
                return new List<long>();
            }
        }
    }
}
