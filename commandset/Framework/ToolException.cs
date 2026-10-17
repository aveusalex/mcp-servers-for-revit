using System;

namespace RevitMCPCommandSet.Framework
{
    /// <summary>
    /// The expected way for a tool to fail. The code is stable, the message is ready to show the user, and the
    /// hint / recommended tool tell the AI what to do next. Any other exception becomes INTERNAL_ERROR.
    /// </summary>
    public sealed class ToolException : Exception
    {
        public ToolException(string code, string message, string hint = null, string recommendedTool = null)
            : base(message)
        {
            Code = code;
            Hint = hint;
            RecommendedTool = recommendedTool;
        }

        public string Code { get; }
        public string Hint { get; }
        public string RecommendedTool { get; }
    }
}
