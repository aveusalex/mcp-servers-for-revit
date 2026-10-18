namespace RevitMCPCommandSet.Framework
{
    /// <summary>
    /// A tool that knows a second way to do its job when Revit rejects the first (e.g. create_walls without joining
    /// the new walls to existing ones when that join would invalidate the user's dimensions). The runner rolls the
    /// first attempt back, asks the tool, and if it agrees runs the same instance once more in a new transaction.
    /// </summary>
    public interface IRetryAfterRollback
    {
        /// <summary>Return true (and switch to the other way, adding a warning that says so) to be run again once.</summary>
        bool PrepareRetry(ToolContext ctx, SafeFailuresPreprocessor failures);
    }
}
