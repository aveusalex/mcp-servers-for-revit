using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Sheets.Support;
using RevitMCPCommandSet.Tools.Views.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Sheets
{
    internal static class RevisionSupport
    {
        public static List<Revision> All(Document doc) =>
            Revision.GetAllRevisionIds(doc).Select(id => doc.GetElement(id) as Revision).Where(r => r != null).ToList();

        /// <summary>A revision by element id, "seq:N" / "#N" (sequence number) or description.</summary>
        public static Revision Find(ToolContext ctx, string reference)
        {
            var doc = ctx.Doc;
            var all = All(doc);
            var text = (reference ?? string.Empty).Trim();
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                var byId = all.FirstOrDefault(r => r.Id.GetValue() == id);
                if (byId != null) return byId;
            }
            var seqText = text.StartsWith("#") ? text.Substring(1) : text.StartsWith("seq:", StringComparison.OrdinalIgnoreCase) ? text.Substring(4) : null;
            if (seqText != null && int.TryParse(seqText.Trim(), out var seq))
                return all.FirstOrDefault(r => r.SequenceNumber == seq)
                       ?? throw new ToolException("REVISION_NOT_FOUND", $"No revision with sequence {seq}.", "Use list_revisions.", "list_revisions");
            return NameMatcher.Single(text, all.Select(r => new NamedCandidate<Revision>(r.Description, r, $"seq {r.SequenceNumber}, id {r.Id.GetValue()}")), "revision");
        }

        public static object Describe(Document doc, Revision r) => new
        {
            revisionId = r.Id.GetValue(),
            sequence = r.SequenceNumber,
            number = r.RevisionNumber,
            description = r.Description,
            date = r.RevisionDate,
            issuedBy = r.IssuedBy,
            issuedTo = r.IssuedTo,
            issued = r.Issued,
            visibility = Visibility(r.Visibility),
            numberingSequence = doc.GetElement(r.RevisionNumberingSequenceId)?.Name
        };

        public static string Visibility(RevisionVisibility v) =>
            v == RevisionVisibility.Hidden ? "hidden" : v == RevisionVisibility.TagVisible ? "tag_only" : "cloud_and_tag";
    }

    // ---- FOL-13 ------------------------------------------------------------------------------------------------------

    public sealed class ListRevisionsArgs
    {
        [JsonProperty("includeSheets")]
        [Description("List the sheets each revision is on, and whether it comes from clouds or was added manually (default true).")]
        [DefaultValue(true)]
        public bool IncludeSheets { get; set; } = true;

        [JsonProperty("includeSequences")]
        [Description("Also list the revision numbering sequences (default false).")]
        [DefaultValue(false)]
        public bool IncludeSequences { get; set; }

        [JsonProperty("limit")]
        [Description("Page size, 1-500 (default 100).")]
        [DefaultValue(100)]
        public int Limit { get; set; } = 100;

        [JsonProperty("offset")]
        [Description("Revisions to skip, for paging (default 0).")]
        [DefaultValue(0)]
        public int Offset { get; set; }
    }

    [McpTool("list_revisions", Kind = ToolKind.Read, Toolset = "sheets", Idempotent = true,
        Description = "List revisions in sequence order: id, sequence, number, description, date, issued by/to, issued " +
                      "flag, visibility and numbering sequence; with includeSheets the sheets that carry each revision " +
                      "(manual vs from clouds); with includeSequences the numbering sequences. Paged. Read-only.")]
    public sealed class ListRevisionsTool : McpTool<ListRevisionsArgs>
    {
        protected override object Run(ToolContext ctx, ListRevisionsArgs args)
        {
            var doc = ctx.Doc;
            var page = Paging.Page(RevisionSupport.All(doc), args.Limit, args.Offset, 500);
            List<ViewSheet> sheets = args.IncludeSheets ? SheetSupport.AllSheets(doc) : null;
            var items = page.Items.Select(r => new
            {
                revision = RevisionSupport.Describe(doc, r),
                sheets = sheets?.Where(s => s.GetAllRevisionIds().Contains(r.Id))
                    .OrderBy(s => s.SheetNumber, new SheetSupport.NaturalComparer())
                    .Select(s => new
                    {
                        sheetId = s.Id.GetValue(),
                        number = s.SheetNumber,
                        name = s.Name,
                        numberOnSheet = s.GetRevisionNumberOnSheet(r.Id),
                        source = s.GetAdditionalRevisionIds().Contains(r.Id) ? "manual" : "clouds"
                    }).ToList()
            }).ToList();
            var sequences = args.IncludeSequences
                ? new FilteredElementCollector(doc).OfClass(typeof(RevisionNumberingSequence)).Cast<RevisionNumberingSequence>()
                    .Select(s => new { sequenceId = s.Id.GetValue(), name = s.Name, numberType = s.NumberType.ToString() }).ToList()
                : null;
            return new { revisions = items, sequences, total = page.Total, limit = page.Limit, offset = page.Offset, truncated = page.Truncated };
        }
    }

    // ---- FOL-14 ------------------------------------------------------------------------------------------------------

    public sealed class RevisionItem
    {
        [JsonProperty("description", Required = Required.Always)]
        [Description("Revision description (e.g. \"Emissão para aprovação\").")]
        public string Description { get; set; }

        [JsonProperty("date")]
        [Description("Revision date as text, as it should appear (e.g. \"17/10/2026\").")]
        public string Date { get; set; }

        [JsonProperty("issuedBy")]
        [Description("Issued by (free text).")]
        public string IssuedBy { get; set; }

        [JsonProperty("issuedTo")]
        [Description("Issued to (free text).")]
        public string IssuedTo { get; set; }

        [JsonProperty("numberingSequence")]
        [Description("Revision numbering sequence name or id. Omit for the default.")]
        public string NumberingSequence { get; set; }

        [JsonProperty("visibility")]
        [Description("cloud_and_tag (default), tag_only or hidden.")]
        [EnumValues("cloud_and_tag", "tag_only", "hidden")]
        public string Visibility { get; set; }

        [JsonProperty("issued")]
        [Description("Mark as issued (locks the revision; default false). Set last.")]
        [DefaultValue(false)]
        public bool Issued { get; set; }
    }

    public sealed class CreateRevisionArgs
    {
        [JsonProperty("items", Required = Required.Always)]
        [Description("Revisions to create, in order, up to 50.")]
        [MinLength(1)]
        [MaxLength(50)]
        public List<RevisionItem> Items { get; set; }

        [JsonProperty("assignToSheets")]
        [Description("Sheets (numbers, names or ids) that get every new revision (manual assignment, no cloud).")]
        public List<string> AssignToSheets { get; set; }
    }

    [McpTool("create_revision", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "sheets",
        Description = "Create revisions (description, date text, issued by/to, numbering sequence, visibility, issued " +
                      "flag set last) and optionally add them to sheets without clouds. Each field is read back. " +
                      "Undo: Ctrl+Z or delete the returned revision ids (issued revisions must be un-issued first).")]
    public sealed class CreateRevisionTool : McpTool<CreateRevisionArgs>
    {
        protected override object Run(ToolContext ctx, CreateRevisionArgs args)
        {
            var doc = ctx.Doc;
            var sheets = (args.AssignToSheets ?? new List<string>()).Select(s => SheetSupport.Sheet(ctx, s)).GroupBy(s => s.Id).Select(g => g.First()).ToList();
            var sequences = new FilteredElementCollector(doc).OfClass(typeof(RevisionNumberingSequence)).Cast<RevisionNumberingSequence>().ToList();
            var created = new List<Revision>();
            for (var i = 0; i < args.Items.Count; i++)
            {
                var item = args.Items[i];
                var label = $"Revision [{i}] '{item.Description}'";
                var r = Revision.Create(doc);
                r.Description = item.Description;
                if (item.Date != null) r.RevisionDate = item.Date;
                if (item.IssuedBy != null) r.IssuedBy = item.IssuedBy;
                if (item.IssuedTo != null) r.IssuedTo = item.IssuedTo;
                if (!string.IsNullOrWhiteSpace(item.NumberingSequence))
                    r.RevisionNumberingSequenceId = ctx.Services.Lookup.ByNameOrId(item.NumberingSequence, sequences, "revision numbering sequence").Id;
                if (item.Visibility != null)
                    r.Visibility = item.Visibility.ToLowerInvariant() == "hidden" ? RevisionVisibility.Hidden
                        : item.Visibility.ToLowerInvariant() == "tag_only" ? RevisionVisibility.TagVisible : RevisionVisibility.CloudAndTagVisible;
                if (r.Description != item.Description || (item.Date != null && r.RevisionDate != item.Date) ||
                    (item.IssuedBy != null && r.IssuedBy != item.IssuedBy) || (item.IssuedTo != null && r.IssuedTo != item.IssuedTo))
                    throw new ToolException("REVISION_NOT_APPLIED", $"{label}: Revit did not keep every field.");
                created.Add(r);
            }

            foreach (var sheet in sheets)
            {
                var ids = sheet.GetAdditionalRevisionIds().ToList();
                ids.AddRange(created.Select(r => r.Id).Where(id => !ids.Contains(id)));
                sheet.SetAdditionalRevisionIds(ids);
                // GetAllRevisionIds only reflects the change after a regeneration (live test: it reported the new
                // revision as missing); read back the list we just wrote, regenerated.
                sheet.Document.Regenerate();
                var now = sheet.GetAdditionalRevisionIds();
                if (created.Any(r => !now.Contains(r.Id)))
                    throw new ToolException("REVISION_NOT_ASSIGNED", $"Revit did not add the revisions to sheet {sheet.SheetNumber}.");
            }

            // Issued last: an issued revision is locked.
            for (var i = 0; i < created.Count; i++)
            {
                if (!args.Items[i].Issued) continue;
                created[i].Issued = true;
                if (!created[i].Issued) throw new ToolException("REVISION_NOT_APPLIED", $"Revit did not mark revision '{created[i].Description}' as issued.");
            }

            return new
            {
                created = created.Select(r => RevisionSupport.Describe(doc, r)).ToList(),
                assignedTo = sheets.Select(s => s.SheetNumber).ToList(),
                dryRun = ctx.DryRun
            };
        }
    }

    // ---- FOL-15 ------------------------------------------------------------------------------------------------------

    public sealed class AssignRevisionArgs
    {
        [JsonProperty("revision", Required = Required.Always)]
        [Description("Revision: element id, \"#N\" / \"seq:N\" for sequence number N, or its description.")]
        public string Revision { get; set; }

        [JsonProperty("sheets", Required = Required.Always)]
        [Description("Sheets (numbers, names or ids), up to 1000.")]
        [MinLength(1)]
        [MaxLength(1000)]
        public List<string> Sheets { get; set; }

        [JsonProperty("mode")]
        [Description("add (default) or remove. Only manual assignments can be removed; revisions that come from clouds stay.")]
        [EnumValues("add", "remove")]
        [DefaultValue("add")]
        public string Mode { get; set; } = "add";

        [JsonProperty("atomic")]
        [Description("true (default): any sheet that fails rolls the whole call back. false: failures are reported per sheet.")]
        [DefaultValue(true)]
        public bool Atomic { get; set; } = true;
    }

    [McpTool("assign_revision_to_sheets", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "sheets",
        Description = "Add a revision to sheets, or remove it (manual assignment, no cloud). The sheet's existing " +
                      "manual revisions are kept (read, merge, write); sheets that already have it are reported as " +
                      "unchanged; revisions that come from clouds cannot be removed this way. Read back per sheet. " +
                      "Undo: Ctrl+Z or call again with the opposite mode.")]
    public sealed class AssignRevisionToSheetsTool : McpTool<AssignRevisionArgs>
    {
        protected override void Validate(AssignRevisionArgs args) => args.Mode = (args.Mode ?? "add").ToLowerInvariant();

        protected override object Run(ToolContext ctx, AssignRevisionArgs args)
        {
            var revision = RevisionSupport.Find(ctx, args.Revision);
            var add = args.Mode == "add";
            var r = ItemLoop.Run(ctx, args.Sheets.Distinct().ToList(), !args.Atomic, (reference, index) =>
            {
                var sheet = SheetSupport.Sheet(ctx, reference);
                var manual = sheet.GetAdditionalRevisionIds().ToList();
                var has = manual.Contains(revision.Id);
                string status;
                if (add && has) status = "unchanged";
                else if (!add && !has)
                {
                    if (sheet.GetAllRevisionIds().Contains(revision.Id))
                        throw new ToolException("REVISION_FROM_CLOUD", $"On sheet {sheet.SheetNumber} the revision comes from revision clouds.",
                            "Delete or reassign the clouds to remove it.");
                    status = "unchanged";
                }
                else
                {
                    if (add) manual.Add(revision.Id); else manual.Remove(revision.Id);
                    sheet.SetAdditionalRevisionIds(manual);
                    var nowManual = sheet.GetAdditionalRevisionIds().Contains(revision.Id);
                    if (nowManual != add)
                        throw new ToolException("REVISION_NOT_ASSIGNED", $"Revit did not {(add ? "add" : "remove")} the revision on sheet {sheet.SheetNumber}.");
                    status = add ? "added" : "removed";
                    sheet.Document.Regenerate(); // GetAllRevisionIds is stale until then
                }
                return new { index, sheetId = sheet.Id.GetValue(), number = sheet.SheetNumber, status, onSheet = sheet.GetAllRevisionIds().Contains(revision.Id) };
            }, s => s, "Sheet");
            return new { revision = RevisionSupport.Describe(ctx.Doc, revision), mode = args.Mode, sheets = r.Done, failed = r.Failed, dryRun = ctx.DryRun };
        }
    }
}
