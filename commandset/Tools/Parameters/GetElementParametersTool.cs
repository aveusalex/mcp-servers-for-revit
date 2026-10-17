using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Parameters
{
    public sealed class GetElementParametersArgs
    {
        [JsonProperty("elementIds", Required = Required.Always)]
        [Description("Elements to read, as strings: element id (\"123456\"), UniqueId, or \"linkInstanceId:elementId\" for an element inside a Revit link. Type ids work too.")]
        public List<string> ElementIds { get; set; }

        [JsonProperty("scope")]
        [Description("instance = the element's own parameters; type = its type's; both = both (default).")]
        [EnumValues("instance", "type", "both")]
        [DefaultValue("both")]
        public string Scope { get; set; } = "both";

        [JsonProperty("parameters")]
        [Description("Only these parameters, each {guid} | {builtIn} | {name}. Omit for all parameters.")]
        public List<ParameterRef> Parameters { get; set; }

        [JsonProperty("includeReadOnly")]
        [Description("Include read-only parameters (default true).")]
        [DefaultValue(true)]
        public bool IncludeReadOnly { get; set; } = true;

        [JsonProperty("includeEmpty")]
        [Description("Include parameters without a value (default true).")]
        [DefaultValue(true)]
        public bool IncludeEmpty { get; set; } = true;

        [JsonProperty("compact")]
        [Description("Return only name/builtIn/guid/on/storageType/readOnly/value/unit per parameter (default false).")]
        [DefaultValue(false)]
        public bool Compact { get; set; }

        [JsonProperty("limit")]
        [Description("Maximum number of elements returned (1-200, default 50).")]
        [DefaultValue(50)]
        public int Limit { get; set; } = 50;

        [JsonProperty("offset")]
        [Description("Number of elements to skip, for paging (default 0).")]
        [DefaultValue(0)]
        public int Offset { get; set; }
    }

    /// <summary>PAR-01: read instance and/or type parameters of elements with full metadata.</summary>
    [McpTool("get_element_parameters", Kind = ToolKind.Read, Toolset = "parameters", Idempotent = true,
        Description = "Read the parameters of one or more elements (instance and/or type), with metadata: name, builtIn, " +
                      "guid, storage type, data type, group, read-only flag, value in wire units (mm, m2, m3, deg, %; " +
                      "ElementId values as the referenced element's name, raw id in 'raw'), and Revit's display text. " +
                      "Use builtIn or guid from the result to reference a parameter unambiguously in set_parameters. " +
                      "Elements inside links are readable with \"linkInstanceId:elementId\". Paged by element. Read-only.")]
    public sealed class GetElementParametersTool : McpTool<GetElementParametersArgs>
    {
        protected override void Validate(GetElementParametersArgs args)
        {
            if (args.ElementIds == null || args.ElementIds.Count == 0)
                throw new ToolException("INVALID_ARGUMENTS", "elementIds must list at least one element.");
            if (args.Limit < 1 || args.Limit > 200)
                throw new ToolException("INVALID_ARGUMENTS", "limit must be between 1 and 200.");
            var scope = (args.Scope ?? "both").ToLowerInvariant();
            if (scope != "instance" && scope != "type" && scope != "both")
                throw new ToolException("INVALID_ARGUMENTS", $"scope must be instance, type or both (got '{args.Scope}').");
            args.Scope = scope;
        }

        protected override object Run(ToolContext ctx, GetElementParametersArgs args)
        {
            var services = ctx.Services;
            var page = Paging.Page(args.ElementIds.Distinct().ToList(), args.Limit, args.Offset, 200);
            var elements = new List<object>();
            var notFound = new List<object>();

            foreach (var reference in page.Items)
            {
                ResolvedElement resolved;
                try
                {
                    resolved = services.Lookup.Resolve(reference);
                }
                catch (ToolException ex)
                {
                    notFound.Add(new { elementId = reference, code = ex.Code, message = ex.Message });
                    continue;
                }
                elements.Add(Describe(services.Parameters, resolved, reference, args));
            }

            return new
            {
                elements,
                notFound,
                total = page.Total,
                limit = page.Limit,
                offset = page.Offset,
                truncated = page.Truncated
            };
        }

        private static object Describe(ParameterService ps, ResolvedElement resolved, string reference, GetElementParametersArgs args)
        {
            var e = resolved.Element;
            var isType = e is ElementType;
            var type = isType ? null : ParameterService.TypeOf(e);
            var parameters = new List<ParameterInfo>();
            var errors = new List<object>();
            var resolvedRefs = new HashSet<int>();
            var failedRefs = new HashSet<int>();

            var sources = new List<(Element owner, string on)>();
            if (isType)
                sources.Add((e, "type"));
            else
            {
                if (args.Scope != "type") sources.Add((e, "instance"));
                if (args.Scope != "instance" && type != null) sources.Add((type, "type"));
            }

            foreach (var (owner, on) in sources)
            {
                IEnumerable<Parameter> found;
                if (args.Parameters != null && args.Parameters.Count > 0)
                {
                    var list = new List<Parameter>();
                    for (var i = 0; i < args.Parameters.Count; i++)
                    {
                        var r = args.Parameters[i];
                        try
                        {
                            var p = ps.Find(owner, r);
                            if (p == null) continue;
                            list.Add(p);
                            resolvedRefs.Add(i);
                        }
                        catch (ToolException ex)
                        {
                            failedRefs.Add(i);
                            errors.Add(new { parameter = r.ToString(), on, code = ex.Code, message = ex.Message, hint = ex.Hint });
                        }
                    }
                    found = list;
                }
                else
                {
                    found = owner.Parameters.Cast<Parameter>()
                        .OrderBy(p => p.Definition?.Name, System.StringComparer.CurrentCultureIgnoreCase);
                }

                foreach (var p in found)
                {
                    if (!args.IncludeReadOnly && p.IsReadOnly) continue;
                    if (!args.IncludeEmpty && !p.HasValue) continue;
                    parameters.Add(ps.Describe(p, on, args.Compact));
                }
            }

            if (args.Parameters != null)
            {
                for (var i = 0; i < args.Parameters.Count; i++)
                    if (!resolvedRefs.Contains(i) && !failedRefs.Contains(i))
                        errors.Add(new { parameter = args.Parameters[i].ToString(), code = "PARAMETER_NOT_FOUND",
                            message = $"Not found on the element (scope '{args.Scope}')." });
            }

            return new
            {
                elementId = e.Id.GetValue(),
                reference,
                uniqueId = e.UniqueId,
                linkInstanceId = resolved.Link?.Id.GetValue(),
                name = SafeName(e),
                category = e.Category?.Name,
                categoryBuiltIn = CategoryService.BuiltInNameOf(e.Category),
                isType,
                typeId = type?.Id.GetValue(),
                typeName = type == null ? null : LookupService.FullName(type),
                parameters,
                errors = errors.Count > 0 ? errors : null
            };
        }

        private static string SafeName(Element e)
        {
            try { return e.Name; }
            catch { return null; }
        }
    }
}
