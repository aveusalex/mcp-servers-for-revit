using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Parameters
{
    public sealed class SetProjectInfoArgs
    {
        [JsonProperty("name")]
        [Description("Project Name.")]
        public string Name { get; set; }

        [JsonProperty("number")]
        [Description("Project Number.")]
        public string Number { get; set; }

        [JsonProperty("clientName")]
        [Description("Client Name.")]
        public string ClientName { get; set; }

        [JsonProperty("address")]
        [Description("Project Address (free text; line breaks allowed).")]
        public string Address { get; set; }

        [JsonProperty("status")]
        [Description("Project Status.")]
        public string Status { get; set; }

        [JsonProperty("issueDate")]
        [Description("Project Issue Date (free text, written as given, e.g. \"2026-10-17\" or \"17/10/2026\").")]
        public string IssueDate { get; set; }

        [JsonProperty("buildingName")]
        [Description("Building Name.")]
        public string BuildingName { get; set; }

        [JsonProperty("author")]
        [Description("Author of the project information.")]
        public string Author { get; set; }

        [JsonProperty("organizationName")]
        [Description("Organization Name.")]
        public string OrganizationName { get; set; }

        [JsonProperty("organizationDescription")]
        [Description("Organization Description.")]
        public string OrganizationDescription { get; set; }

        [JsonProperty("extra")]
        [Description("Other Project Information parameters by name, e.g. {\"Responsável técnico\": \"AE\"}; same value rules as set_parameters.")]
        public Dictionary<string, JToken> Extra { get; set; }
    }

    /// <summary>PAR-15: Project Information fields through the ParameterService engine.</summary>
    [McpTool("set_project_info", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "parameters",
        Description = "Set Project Information fields (they feed every title block): name, number, clientName, address, " +
                      "status, issueDate, buildingName, author, organizationName, organizationDescription (by built-in " +
                      "parameter, so it works in any Revit language) plus extra{name: value} for custom project " +
                      "parameters. All or nothing; each value is read back and returned with the old value. Undo: Ctrl+Z " +
                      "or send revert[] to set_parameters.")]
    public sealed class SetProjectInfoTool : McpTool<SetProjectInfoArgs>
    {
        private static IEnumerable<(string builtIn, string value)> Named(SetProjectInfoArgs a)
        {
            if (a.Name != null) yield return ("PROJECT_NAME", a.Name);
            if (a.Number != null) yield return ("PROJECT_NUMBER", a.Number);
            if (a.ClientName != null) yield return ("CLIENT_NAME", a.ClientName);
            if (a.Address != null) yield return ("PROJECT_ADDRESS", a.Address);
            if (a.Status != null) yield return ("PROJECT_STATUS", a.Status);
            if (a.IssueDate != null) yield return ("PROJECT_ISSUE_DATE", a.IssueDate);
            if (a.BuildingName != null) yield return ("PROJECT_BUILDING_NAME", a.BuildingName);
            if (a.Author != null) yield return ("PROJECT_AUTHOR", a.Author);
            if (a.OrganizationName != null) yield return ("PROJECT_ORGANIZATION_NAME", a.OrganizationName);
            if (a.OrganizationDescription != null) yield return ("PROJECT_ORGANIZATION_DESCRIPTION", a.OrganizationDescription);
        }

        protected override void Validate(SetProjectInfoArgs a)
        {
            if (!Named(a).Any() && (a.Extra == null || a.Extra.Count == 0))
                throw new ToolException("INVALID_ARGUMENTS", "Give at least one field to set.");
        }

        protected override object Run(ToolContext ctx, SetProjectInfoArgs a)
        {
            var info = ctx.Doc.ProjectInformation;
            if (info == null)
                throw new ToolException("NOT_A_PROJECT", "The target document has no Project Information (is it a family?).");
            var id = info.Id.GetValue().ToString(CultureInfo.InvariantCulture);

            var requests = Named(a).Select(n => new ParameterWriteRequest
            {
                Element = id, Parameter = new ParameterRef { BuiltIn = n.builtIn }, Value = n.value
            }).ToList();
            foreach (var kv in a.Extra ?? new Dictionary<string, JToken>())
                requests.Add(new ParameterWriteRequest { Element = id, Parameter = ParameterRef.ByName(kv.Key), Value = kv.Value });

            var ps = ctx.Services.Parameters;
            var plan = ps.Plan(requests, new ParameterPlanOptions { Target = ParameterTarget.Instance });
            if (plan.HasErrors)
                throw new ToolException("PARAMETER_WRITE_REFUSED",
                    $"{plan.Failed.Count} field(s) can't be written; nothing was changed. {plan.FailureSummary()}",
                    "Check the extra parameter names with get_element_parameters on the Project Information element (id " + id + ").",
                    "get_element_parameters");
            var result = ps.Apply(plan);
            if (result.Failed > 0)
                throw new ToolException("PARAMETER_WRITE_FAILED",
                    "Revit did not keep some values, so nothing was changed: " +
                    string.Join(" ", result.Items.Where(i => i.Status != "ok").Select(i => $"'{i.Parameter?.Name}': {i.Message}")));

            return new
            {
                projectInfoId = info.Id.GetValue(),
                fields = result.Items.Select(i => new { parameter = i.Parameter, before = i.Old, after = i.New, changed = i.Changed }),
                revert = result.Revert,
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) + result.ChangeSummary
            };
        }
    }
}
