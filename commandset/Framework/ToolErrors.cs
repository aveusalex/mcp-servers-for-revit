using System;
using System.Linq;

namespace RevitMCPCommandSet.Framework
{
    /// <summary>Stable error codes produced by the framework itself. Tools add their own (NOT_FOUND, ...).</summary>
    /// <remarks>No Revit dependency: shared with the framework tests.</remarks>
    public static class ToolErrorCodes
    {
        public const string InvalidArguments = "INVALID_ARGUMENTS";
        public const string InternalError = "INTERNAL_ERROR";
        public const string UnknownTool = "UNKNOWN_TOOL";
        public const string DocumentNotFound = "DOCUMENT_NOT_FOUND";
        public const string DocumentReadOnly = "DOCUMENT_READ_ONLY";
        public const string DocumentBusy = "DOCUMENT_BUSY";
        public const string RequiresActiveDocument = "REQUIRES_ACTIVE_DOCUMENT";
        public const string RevitBusy = "REVIT_BUSY";
        public const string TransactionRolledBack = "TRANSACTION_ROLLED_BACK";
        public const string BatchEmpty = "BATCH_EMPTY";
        public const string BatchTooLarge = "BATCH_TOO_LARGE";
        public const string StepNotBatchable = "STEP_NOT_BATCHABLE";
        public const string BatchMixesUiAndWrite = "BATCH_MIXES_UI_AND_WRITE";
        public const string BatchReferenceInvalid = "BATCH_REFERENCE_INVALID";
        public const string BatchReferenceUnresolved = "BATCH_REFERENCE_UNRESOLVED";
        public const string BatchStepFailed = "BATCH_STEP_FAILED";
    }

    /// <summary>Maps exceptions to the result envelope. Only <see cref="ToolException"/> is an "expected" failure.</summary>
    public static class ToolErrors
    {
        /// <summary>
        /// " (in Revit's X, called from our Y)": the API method that threw and the add-in method that called it, so an
        /// unexpected failure can be located without a debugger.
        /// </summary>
        private static string Where(Exception ex)
        {
            try
            {
                var frames = new System.Diagnostics.StackTrace(ex, false).GetFrames();
                if (frames == null || frames.Length == 0) return string.Empty;
                string api = ex.TargetSite?.Name;
                string ours = frames.Select(f => f.GetMethod())
                    .Where(m => m?.DeclaringType != null && m.DeclaringType.Namespace != null &&
                                m.DeclaringType.Namespace.StartsWith("RevitMCPCommandSet", StringComparison.Ordinal))
                    .Select(m => m.DeclaringType.Name + "." + m.Name).FirstOrDefault();
                if (api == null && ours == null) return string.Empty;
                return " (in " + (api ?? "?") + (ours != null ? ", called from " + ours : string.Empty) + ")";
            }
            catch
            {
                return string.Empty;
            }
        }

        public static ToolError FromException(Exception ex)
        {
            if (ex is ToolException te)
            {
                return new ToolError
                {
                    Code = te.Code,
                    Message = te.Message,
                    Hint = te.Hint,
                    RecommendedTool = te.RecommendedTool
                };
            }

            var inner = ex;
            while (inner is System.Reflection.TargetInvocationException && inner.InnerException != null)
            {
                inner = inner.InnerException;
            }

            if (!ReferenceEquals(inner, ex))
            {
                return FromException(inner);
            }

            return new ToolError
            {
                Code = ToolErrorCodes.InternalError,
                Message = $"{inner.GetType().Name}: {inner.Message}" + Where(inner),
                Hint = "Unexpected failure inside the add-in. Any change made by this call was rolled back. " +
                       "Do not retry blindly; report the message to the user."
            };
        }

        /// <summary>Builds a failed envelope from an exception.</summary>
        public static ToolResult Fail(Exception ex, bool dryRun = false)
        {
            return new ToolResult { Ok = false, Error = FromException(ex), DryRun = dryRun };
        }

        public static ToolResult Fail(string code, string message, string hint = null, string recommendedTool = null)
        {
            return Fail(new ToolException(code, message, hint, recommendedTool));
        }
    }
}
