using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Views.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Views
{
    public sealed class DuplicateViewArgs
    {
        [JsonProperty("views", Required = Required.Always)]
        [Description("Views or schedules to duplicate (names or ids), up to 50.")]
        [MinLength(1)]
        [MaxLength(50)]
        public List<string> Views { get; set; }

        [JsonProperty("option")]
        [Description("duplicate (model only, default), with_detailing (also annotations/detail items) or as_dependent (a dependent view sharing the primary's annotations).")]
        [EnumValues("duplicate", "with_detailing", "as_dependent")]
        [DefaultValue("duplicate")]
        public string Option { get; set; } = "duplicate";

        [JsonProperty("count")]
        [Description("Copies per view, 1-20 (default 1).")]
        [Range(1, 20)]
        [DefaultValue(1)]
        public int Count { get; set; } = 1;

        [JsonProperty("newName")]
        [Description("Exact name of the copy; only for one view with count 1. Must be free.")]
        public string NewName { get; set; }

        [JsonProperty("prefix")]
        [Description("Text put before the source name for the copies' names.")]
        public string Prefix { get; set; }

        [JsonProperty("suffix")]
        [Description("Text put after the source name for the copies' names. With neither prefix, suffix nor newName Revit's default names (\"... Copy 1\") are kept.")]
        public string Suffix { get; set; }

        [JsonProperty("continueOnError")]
        [Description("false (default): any failure rolls back every copy. true: failed views are listed in failed[].")]
        [DefaultValue(false)]
        public bool ContinueOnError { get; set; }
    }

    /// <summary>VIS-08: duplicate views and schedules (simple, with detailing, dependent).</summary>
    [McpTool("duplicate_view", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "views",
        Description = "Duplicate views or schedules: option duplicate, with_detailing or as_dependent (checked with " +
                      "CanViewBeDuplicated first), count copies each, named newName (single copy) or prefix + source " +
                      "name + suffix (collisions get \" (2)\"). Returns the new view ids with their names read back. " +
                      "Undo: Ctrl+Z or delete the returned ids.")]
    public sealed class DuplicateViewTool : McpTool<DuplicateViewArgs>
    {
        protected override void Validate(DuplicateViewArgs args)
        {
            args.Option = (args.Option ?? "duplicate").ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(args.NewName) && (args.Views.Count != 1 || args.Count != 1))
                throw new ToolException("INVALID_ARGUMENTS", "newName works only with one view and count 1; use prefix/suffix for more.");
            if (!string.IsNullOrWhiteSpace(args.NewName) && (!string.IsNullOrEmpty(args.Prefix) || !string.IsNullOrEmpty(args.Suffix)))
                throw new ToolException("INVALID_ARGUMENTS", "Pass newName or prefix/suffix, not both.");
        }

        public static ViewDuplicateOption ParseOption(string option)
        {
            switch (option)
            {
                case "with_detailing": return ViewDuplicateOption.WithDetailing;
                case "as_dependent": return ViewDuplicateOption.AsDependent;
                default: return ViewDuplicateOption.Duplicate;
            }
        }

        protected override object Run(ToolContext ctx, DuplicateViewArgs args)
        {
            var option = ParseOption(args.Option);
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var r = ItemLoop.Run(ctx, args.Views.Distinct().ToList(), args.ContinueOnError, (reference, index) =>
            {
                var source = ctx.Services.Lookup.View(reference);
                if (source is ViewSheet)
                    throw new ToolException("NOT_SUPPORTED", $"'{source.Name}' is a sheet.", "Use duplicate_sheet for sheets.", "duplicate_sheet");
                if (!source.CanViewBeDuplicated(option))
                    throw new ToolException("CANNOT_DUPLICATE", $"'{source.Name}' ({source.ViewType}) cannot be duplicated as {args.Option}.",
                        args.Option == "duplicate" ? null : "Try option duplicate.");
                var copies = new List<object>();
                for (var n = 0; n < args.Count; n++)
                {
                    var newId = source.Duplicate(option);
                    var copy = ctx.Doc.GetElement(newId) as View
                               ?? throw new ToolException("VIEW_NOT_CREATED", $"Revit did not duplicate '{source.Name}'.");
                    if (!string.IsNullOrWhiteSpace(args.NewName))
                    {
                        ctx.Services.Names.EnsureViewNameFree(args.NewName, copy.ViewType, false, copy.Id);
                        ctx.Services.Names.ApplyName(copy, args.NewName, "view name");
                    }
                    else if (!string.IsNullOrEmpty(args.Prefix) || !string.IsNullOrEmpty(args.Suffix))
                    {
                        var name = ViewSupport.FreeViewName(ctx.Doc, (args.Prefix ?? string.Empty) + source.Name + (args.Suffix ?? string.Empty),
                            copy.ViewType, reserved);
                        ctx.Services.Names.ApplyName(copy, name, "view name");
                    }
                    copies.Add(ViewSupport.Describe(copy));
                }
                return new { index, source = source.Name, sourceId = source.Id.GetValue(), copies };
            }, v => v, "View");

            return new { duplicated = r.Done, failed = r.Failed, option = args.Option, dryRun = ctx.DryRun };
        }
    }
}
