using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Parameters
{
    public sealed class SetElementPhaseArgs
    {
        [JsonProperty("elementIds", Required = Required.Always)]
        [Required]
        [MinLength(1)]
        [MaxLength(2000)]
        [Description("Elements to change.")]
        public List<long> ElementIds { get; set; }

        [JsonProperty("phaseCreated")]
        [Description("Phase Created, by name (as in the project, e.g. \"Existente\", \"Construção nova\") or id.")]
        public string PhaseCreated { get; set; }

        [JsonProperty("phaseDemolished")]
        [Description("Phase Demolished, by name or id; \"none\" clears it (element not demolished).")]
        public string PhaseDemolished { get; set; }

        [JsonProperty("atomic")]
        [Description("true (default): any element that can't take the phases rolls everything back. false: change the others and report the failures.")]
        [DefaultValue(true)]
        public bool Atomic { get; set; } = true;
    }

    /// <summary>PAR-16: the explicit path for the critical PHASE_CREATED / PHASE_DEMOLISHED parameters.</summary>
    [McpTool("set_element_phase", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "parameters",
        Description = "Set Phase Created and/or Phase Demolished of elements (renovation work) by phase name or id; " +
                      "phaseDemolished \"none\" clears demolition. Checks that demolition is not before creation and " +
                      "that each element accepts phases, reads both back and returns old/new phase per element. This " +
                      "is the dedicated tool for phases (set_parameters blocks them). Undo: Ctrl+Z, or call again " +
                      "with the returned old phases.")]
    public sealed class SetElementPhaseTool : McpTool<SetElementPhaseArgs>
    {
        protected override void Validate(SetElementPhaseArgs a)
        {
            if (string.IsNullOrWhiteSpace(a.PhaseCreated) && string.IsNullOrWhiteSpace(a.PhaseDemolished))
                throw new ToolException("INVALID_ARGUMENTS", "Give phaseCreated and/or phaseDemolished.");
        }

        protected override object Run(ToolContext ctx, SetElementPhaseArgs a)
        {
            var doc = ctx.Doc;
            var phases = doc.Phases.Cast<Phase>().ToList();
            int IndexOf(ElementId id) => phases.FindIndex(p => p.Id == id);

            Phase created = string.IsNullOrWhiteSpace(a.PhaseCreated) ? null : ctx.Services.Lookup.Phase(a.PhaseCreated);
            bool clearDemolished = a.PhaseDemolished != null && a.PhaseDemolished.Trim().Equals("none", StringComparison.OrdinalIgnoreCase);
            Phase demolished = string.IsNullOrWhiteSpace(a.PhaseDemolished) || clearDemolished ? null : ctx.Services.Lookup.Phase(a.PhaseDemolished);

            var items = new List<object>();
            var failures = new List<string>();
            int changed = 0;
            foreach (var id in a.ElementIds.Distinct())
            {
                var e = doc.GetElement(id.ToRevitElementId());
                string Fail(string code, string message)
                {
                    failures.Add($"{id}: {message}");
                    items.Add(new { elementId = id, status = code, message });
                    return code;
                }
                if (e == null) { Fail("not_found", "element not found"); continue; }
                if (!e.ArePhasesModifiable()) { Fail("not_phased", "this element does not have modifiable phases"); continue; }

                var oldCreated = e.CreatedPhaseId;
                var oldDemolished = e.DemolishedPhaseId;
                var newCreated = created?.Id ?? oldCreated;
                var newDemolished = clearDemolished ? ElementId.InvalidElementId : demolished?.Id ?? oldDemolished;

                if (newDemolished != ElementId.InvalidElementId && IndexOf(newDemolished) < IndexOf(newCreated))
                {
                    Fail("invalid_order", $"demolition phase '{doc.GetElement(newDemolished)?.Name}' is before creation phase '{doc.GetElement(newCreated)?.Name}'");
                    continue;
                }
                if (created != null && !e.IsPhaseCreatedValid(newCreated)) { Fail("invalid_phase", "Revit refuses this creation phase for the element"); continue; }
                if (newDemolished != ElementId.InvalidElementId && !e.IsPhaseDemolishedValid(newDemolished))
                {
                    Fail("invalid_phase", "Revit refuses this demolition phase for the element");
                    continue;
                }

                try
                {
                    // Order matters: the pair must stay valid (demolished >= created) after each assignment.
                    bool demolishedFirst = newDemolished == ElementId.InvalidElementId || IndexOf(newCreated) > IndexOf(oldCreated);
                    if (demolishedFirst && newDemolished != oldDemolished) e.DemolishedPhaseId = newDemolished;
                    if (newCreated != oldCreated) e.CreatedPhaseId = newCreated;
                    if (e.DemolishedPhaseId != newDemolished) e.DemolishedPhaseId = newDemolished;
                }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is ArgumentException)
                {
                    Fail("set_failed", ex.Message);
                    continue;
                }

                if (e.CreatedPhaseId != newCreated || e.DemolishedPhaseId != newDemolished)
                {
                    Fail("mismatch", "Revit kept different phases than requested");
                    continue;
                }
                bool didChange = oldCreated != newCreated || oldDemolished != newDemolished;
                if (didChange) changed++;
                items.Add(new
                {
                    elementId = id,
                    status = "ok",
                    changed = didChange,
                    before = new { phaseCreated = NameOf(doc, oldCreated), phaseDemolished = NameOf(doc, oldDemolished) ?? "none" },
                    after = new { phaseCreated = NameOf(doc, newCreated), phaseDemolished = NameOf(doc, newDemolished) ?? "none" }
                });
            }

            if (failures.Count > 0 && (a.Atomic || changed == 0 && items.Count == failures.Count))
                throw new ToolException("PHASE_WRITE_FAILED",
                    $"{failures.Count} element(s) can't take these phases; nothing was changed. " + string.Join("; ", failures.Take(10)),
                    a.Atomic ? "Fix or remove those elements, or pass atomic:false." : "Check the phases with get_element_parameters.");

            return new
            {
                changed,
                failed = failures.Count,
                items,
                changeSummary = (ctx.DryRun ? "[dry run, rolled back] " : string.Empty) + $"{changed} element(s) re-phased; {failures.Count} failed."
            };
        }

        private static string NameOf(Document doc, ElementId id) =>
            id == null || id == ElementId.InvalidElementId ? null : doc.GetElement(id)?.Name;
    }
}
