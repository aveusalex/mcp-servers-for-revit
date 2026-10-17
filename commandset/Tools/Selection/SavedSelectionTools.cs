using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Query.Support;
using RevitMCPCommandSet.Tools.Selection.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Selection
{
    // SEL-03 "saved_selection" is split in three tools so each has one kind and one risk level:
    // list_saved_selections (read), save_selection (write, low) and delete_saved_selection (write, medium).
    // To select a saved selection in the UI use select_elements { savedSelection }.

    public sealed class ListSavedSelectionsArgs
    {
        [JsonProperty("nameContains")]
        [Description("Only names containing this text (case and accents ignored).")]
        public string NameContains { get; set; }

        [JsonProperty("includeIds")]
        [Description("Return the element ids of each saved selection (up to idsLimit each; default false).")]
        [DefaultValue(false)]
        public bool IncludeIds { get; set; }

        [JsonProperty("idsLimit")]
        [Description("Most ids returned per saved selection, 1-5000 (default 500).")]
        [DefaultValue(500)]
        public int IdsLimit { get; set; } = 500;

        [JsonProperty("limit")]
        [Description("Page size, 1-1000 (default 200).")]
        [DefaultValue(200)]
        public int Limit { get; set; } = 200;

        [JsonProperty("offset")]
        [Description("Items to skip, for paging (default 0).")]
        [DefaultValue(0)]
        public int Offset { get; set; }
    }

    [McpTool("list_saved_selections", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "selection", Idempotent = true,
        Description = "List the saved selections of the model (Manage > Saved Selections, stored as selection filters): " +
                      "id, name, element count (elements that still exist) and optionally their ids. Select one in the UI " +
                      "with select_elements {savedSelection}; create with save_selection; delete with " +
                      "delete_saved_selection. Read-only.")]
    public sealed class ListSavedSelectionsTool : McpTool<ListSavedSelectionsArgs>
    {
        protected override void Validate(ListSavedSelectionsArgs args)
        {
            QueryHelpers.CheckPaging(args.Limit, args.Offset, 1000);
            if (args.IdsLimit < 1 || args.IdsLimit > 5000)
                throw new ToolException("INVALID_ARGUMENTS", "idsLimit must be between 1 and 5000.");
        }

        protected override object Run(ToolContext ctx, ListSavedSelectionsArgs args)
        {
            var doc = ctx.Doc;
            var all = new FilteredElementCollector(doc).OfClass(typeof(SelectionFilterElement)).Cast<SelectionFilterElement>()
                .Where(s => QueryHelpers.Contains(s.Name, args.NameContains))
                .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            var page = Paging.Page(all, args.Limit, args.Offset, 1000);
            var items = page.Items.Select(s =>
            {
                var ids = s.GetElementIds().Where(i => doc.GetElement(i) != null).Select(i => i.GetValue()).ToList();
                return new
                {
                    id = s.Id.GetValue(),
                    name = s.Name,
                    count = ids.Count,
                    ids = args.IncludeIds ? ids.Take(args.IdsLimit).ToList() : null,
                    idsTruncated = args.IncludeIds ? ids.Count > args.IdsLimit : (bool?)null
                };
            }).ToList();
            return new { items, total = page.Total, limit = page.Limit, offset = page.Offset, truncated = page.Truncated };
        }
    }

    public sealed class SaveSelectionArgs
    {
        [JsonProperty("name", Required = Required.Always)]
        [Description("Name of the saved selection.")]
        public string Name { get; set; }

        [JsonProperty("elementIds")]
        [Description("Elements to save, by id or UniqueId (strings). Default: the current UI selection (target must be the active document).")]
        public List<string> ElementIds { get; set; }

        [JsonProperty("overwrite")]
        [Description("Replace the elements of an existing saved selection with the same name (default false).")]
        [DefaultValue(false)]
        public bool Overwrite { get; set; }
    }

    [McpTool("save_selection", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "selection",
        Description = "Save a named selection in the model (the same as Manage > Save Selection, synchronized to central " +
                      "with the model): the given element ids, or the current UI selection. With overwrite=true an " +
                      "existing saved selection of the same name gets the new elements. Returns its id and the element " +
                      "count read back. Undo with Ctrl+Z or delete_saved_selection. Supports dryRun.")]
    public sealed class SaveSelectionTool : McpTool<SaveSelectionArgs>
    {
        protected override void Validate(SaveSelectionArgs args)
        {
            args.Name = args.Name?.Trim();
            if (string.IsNullOrEmpty(args.Name))
                throw new ToolException("INVALID_ARGUMENTS", "name is required.");
            if ((args.ElementIds?.Count ?? 0) > 20000)
                throw new ToolException("INVALID_ARGUMENTS", "elementIds accepts at most 20000 entries.");
        }

        protected override object Run(ToolContext ctx, SaveSelectionArgs args)
        {
            var doc = ctx.Doc;
            ctx.Services.Names.Validate(args.Name, "saved selection name");
            var notFound = new List<object>();
            List<ElementId> ids;
            string source;
            if (args.ElementIds != null && args.ElementIds.Count > 0)
            {
                ids = SelectionSupport.ResolveIds(ctx, args.ElementIds, notFound);
                source = "elementIds";
            }
            else
            {
                var uiDoc = ctx.UiApp?.ActiveUIDocument;
                if (uiDoc?.Document == null || !uiDoc.Document.Equals(doc))
                    throw new ToolException(ToolErrorCodes.RequiresActiveDocument,
                        "Without elementIds the current selection is used, which needs the target to be the active document.",
                        "Pass elementIds explicitly.");
                ids = uiDoc.Selection.GetElementIds().ToList();
                source = "uiSelection";
            }
            if (ids.Count == 0)
                throw new ToolException("NOTHING_TO_SAVE", "There are no elements to save.", "Select elements or pass elementIds.");

            var existing = new FilteredElementCollector(doc).OfClass(typeof(SelectionFilterElement)).Cast<SelectionFilterElement>()
                .FirstOrDefault(s => string.Equals(s.Name, args.Name, StringComparison.CurrentCultureIgnoreCase));
            SelectionFilterElement target;
            bool created;
            if (existing != null)
            {
                if (!args.Overwrite)
                    throw new ToolException("NAME_IN_USE", $"A saved selection named '{existing.Name}' already exists.",
                        "Pass overwrite=true to replace its elements, or choose another name.");
                target = existing;
                created = false;
            }
            else
            {
                if (!FilterElement.IsNameUnique(doc, args.Name))
                    throw new ToolException("NAME_IN_USE", $"A view filter named '{args.Name}' already exists (filters and saved selections share names).",
                        "Choose another name.");
                target = SelectionFilterElement.Create(doc, args.Name);
                created = true;
            }
            target.SetElementIds(ids);

            var readBack = target.GetElementIds().Count;
            return new
            {
                id = target.Id.GetValue(),
                name = target.Name,
                created,
                source,
                count = readBack,
                notFound
            };
        }
    }

    public sealed class DeleteSavedSelectionArgs
    {
        [JsonProperty("names", Required = Required.Always)]
        [Description("Saved selections to delete, by name or id (1-100). The elements themselves are not touched.")]
        public List<string> Names { get; set; }
    }

    [McpTool("delete_saved_selection", Kind = ToolKind.Write, Risk = Risk.Medium, Toolset = "selection",
        Description = "Delete saved selections (selection filters) by name or id. Only the saved selection is removed, " +
                      "never its elements. Undo with Ctrl+Z right after. Supports dryRun.")]
    public sealed class DeleteSavedSelectionTool : McpTool<DeleteSavedSelectionArgs>
    {
        protected override void Validate(DeleteSavedSelectionArgs args)
        {
            if (args.Names == null || args.Names.Count == 0 || args.Names.Count > 100)
                throw new ToolException("INVALID_ARGUMENTS", "names must list 1 to 100 saved selections.");
        }

        protected override object Run(ToolContext ctx, DeleteSavedSelectionArgs args)
        {
            var targets = new List<SelectionFilterElement>();
            foreach (var n in args.Names.Distinct())
            {
                var s = SelectionSupport.SavedSelection(ctx, n);
                if (targets.All(t => t.Id != s.Id)) targets.Add(s);
            }
            var deleted = targets.Select(t => new { id = t.Id.GetValue(), name = t.Name }).ToList();
            ctx.Doc.Delete(targets.Select(t => t.Id).ToList());
            var stillThere = deleted.Where(d => ctx.Doc.GetElement(d.id.ToRevitElementId()) != null).ToList();
            if (stillThere.Count > 0)
                throw new ToolException("DELETE_FAILED", $"Revit kept {stillThere.Count} saved selection(s).");
            return new { deleted };
        }
    }
}
