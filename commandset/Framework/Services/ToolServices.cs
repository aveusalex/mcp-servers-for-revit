using Autodesk.Revit.DB;

namespace RevitMCPCommandSet.Framework.Services
{
    /// <summary>
    /// Shared services handed to every tool through <see cref="ToolContext.Services"/>
    /// (categories, units, lookup, names, parameters, paging). Owned by front F2 of Phase 0.
    /// Each service is created on first use and bound to the target document.
    /// </summary>
    public sealed class ToolServices
    {
        private CategoryService _categories;
        private UnitsService _units;
        private LookupService _lookup;
        private NamesService _names;
        private ParameterService _parameters;
        private Paging _paging;

        public ToolServices(Document doc)
        {
            Doc = doc;
        }

        public Document Doc { get; }

        /// <summary>Language-neutral category resolver ("OST_Walls", "Walls", "Paredes").</summary>
        public CategoryService Categories => _categories ??= new CategoryService(Doc);

        /// <summary>Wire units (mm, m², m³, deg, %) ↔ internal, fail-closed.</summary>
        public UnitsService Units => _units ??= new UnitsService();

        /// <summary>Elements by id/UniqueId/link reference; levels, phases, views, worksets, types by name.</summary>
        public LookupService Lookup => _lookup ??= new LookupService(Doc);

        /// <summary>Forbidden characters, uniqueness and read-back of names.</summary>
        public NamesService Names => _names ??= new NamesService(Doc);

        /// <summary>The parameter contract: resolve, describe, plan and apply writes with read-back.</summary>
        public ParameterService Parameters => _parameters ??= new ParameterService(Doc, Units, Lookup);

        /// <summary>limit/offset paging.</summary>
        public Paging Paging => _paging ??= new Paging();
    }
}
