using Autodesk.Revit.DB;

namespace RevitMCPCommandSet.Framework.Services
{
    /// <summary>
    /// Shared services handed to every tool through <see cref="ToolContext.Services"/>
    /// (categories, units, lookup, names, parameters, paging). Owned by front F2 of Phase 0.
    /// </summary>
    public sealed class ToolServices
    {
        public ToolServices(Document doc)
        {
            Doc = doc;
        }

        public Document Doc { get; }
    }
}
