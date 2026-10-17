using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Architecture.Support
{
    /// <summary>Writes instance parameters on a newly created element through the shared ParameterService (with read-back).</summary>
    public static class ParamWriter
    {
        public static object Apply(ToolContext ctx, Element element, IDictionary<string, JToken> values)
        {
            if (values == null || values.Count == 0) return null;
            var requests = values.Select(kv => new ParameterWriteRequest
            {
                Element = element.Id.GetValue().ToString(CultureInfo.InvariantCulture),
                Parameter = ParameterRef.ByName(kv.Key),
                Value = kv.Value
            }).ToList();
            var plan = ctx.Services.Parameters.Plan(requests, new ParameterPlanOptions { Target = ParameterTarget.Instance });
            if (plan.HasErrors)
                throw new ToolException("PARAMETER_WRITE_REFUSED", "Parameters are invalid: " + plan.FailureSummary(),
                    "Use get_element_parameters on a similar element to see the exact names and units.", "get_element_parameters");
            var written = ctx.Services.Parameters.Apply(plan);
            if (written.Failed > 0)
                throw new ToolException("PARAMETER_WRITE_FAILED", "Parameters did not stick: " +
                    string.Join(" ", written.Items.Where(i => i.Status != "ok").Select(i => $"'{i.Parameter?.Name}': {i.Status}")));
            return written.Items.Select(i => new { parameter = i.Parameter?.Name, value = i.New, unit = i.Unit }).ToList();
        }
    }
}
