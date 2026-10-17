using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Annotation.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Annotation
{
    // ---- tag_elements (ANO-01) ----------------------------------------------------------------------------------

    public sealed class TagItem
    {
        [JsonProperty("element", Required = Required.Always)]
        [Description("Element to tag: id, UniqueId, or \"linkInstanceId:elementId\" for an element inside a Revit link.")]
        public string Element { get; set; }

        [JsonProperty("tagType")]
        [Description("Tag type as \"Family: Type\", type name or id. Omit for the category's default tag.")]
        public string TagType { get; set; }

        [JsonProperty("leader")]
        [Description("Draw a leader (default false).")]
        [DefaultValue(false)]
        public bool Leader { get; set; }

        [JsonProperty("orientation")]
        [Description("horizontal (default), vertical or model (follows the element).")]
        [EnumValues("horizontal", "vertical", "model")]
        [DefaultValue("horizontal")]
        public string Orientation { get; set; } = "horizontal";

        [JsonProperty("point")]
        [Description("Where to put the tag, in the view plane (mm). Default: the element's location point, curve middle or bounding-box centre.")]
        public ViewPointMm Point { get; set; }

        [JsonProperty("offsetX")]
        [Description("Shift from the default point along the view's horizontal direction (model mm).")]
        [Unit("mm")]
        [DefaultValue(0)]
        public double OffsetX { get; set; }

        [JsonProperty("offsetY")]
        [Description("Shift from the default point along the view's vertical direction (model mm).")]
        [Unit("mm")]
        [DefaultValue(0)]
        public double OffsetY { get; set; }
    }

    public sealed class TagElementsArgs
    {
        [JsonProperty("view", Required = Required.Always)]
        [Description("View to tag in (name or id): plan, ceiling plan, section, elevation or locked 3D view.")]
        public string View { get; set; }

        [JsonProperty("items", Required = Required.Always)]
        [Description("Elements to tag, up to 500.")]
        public List<TagItem> Items { get; set; }

        [JsonProperty("skipTagged")]
        [Description("Skip elements that already have a tag in this view (default true).")]
        [DefaultValue(true)]
        public bool SkipTagged { get; set; } = true;

        [JsonProperty("continueOnError")]
        [Description("false (default): any failure rolls back every tag of the call. true: failed items are listed in failed[] and the rest is kept.")]
        [DefaultValue(false)]
        public bool ContinueOnError { get; set; }
    }

    /// <summary>ANO-01.</summary>
    [McpTool("tag_elements", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "annotation",
        Description = "Tag specific elements in a view (any category; rooms, areas and spaces get room/area/space " +
                      "tags; elements of Revit links via \"linkInstanceId:elementId\"). Optional tag type " +
                      "(\"Family: Type\"), leader, orientation and placement in the view plane (mm) or offset. Skips " +
                      "already-tagged elements by default. One transaction; all or nothing unless continueOnError. " +
                      "Returns created tag ids. Undo: Ctrl+Z or delete the returned ids.")]
    public sealed class TagElementsTool : McpTool<TagElementsArgs>
    {
        protected override void Validate(TagElementsArgs args)
        {
            if (args.Items == null || args.Items.Count == 0)
                throw new ToolException("INVALID_ARGUMENTS", "items must list at least one element.");
            if (args.Items.Count > 500)
                throw new ToolException("TOO_MANY_ITEMS", $"{args.Items.Count} items; the limit is 500 per call.", "Use tag_all_in_view for bulk tagging.", "tag_all_in_view");
            if (args.Items.Any(i => i == null || string.IsNullOrWhiteSpace(i.Element)))
                throw new ToolException("INVALID_ARGUMENTS", "Every item needs an element.");
        }

        protected override object Run(ToolContext ctx, TagElementsArgs args)
        {
            var doc = ctx.Doc;
            var view = ViewPlane.RequireAnnotatable(ctx, args.View, false, "a tag");
            var tagged = args.SkipTagged ? TagSupport.TaggedKeys(doc, view) : new HashSet<string>();
            var created = new List<object>();
            var skipped = new List<object>();
            var failed = new List<object>();

            for (var index = 0; index < args.Items.Count; index++)
            {
                var item = args.Items[index];
                if (!args.ContinueOnError)
                {
                    try
                    {
                        var r = TagOne(ctx, view, item, tagged);
                        if (r.skipped) skipped.Add(new { index, element = item.Element, reason = "already tagged" });
                        else created.Add(r.result);
                    }
                    catch (ToolException ex)
                    {
                        throw new ToolException(ex.Code, $"Item [{index}] '{item.Element}': {ex.Message} No tag was created.",
                            ex.Hint ?? "Fix this item, or pass continueOnError:true.", ex.RecommendedTool);
                    }
                    continue;
                }

                using (var sub = new SubTransaction(doc))
                {
                    sub.Start();
                    try
                    {
                        var r = TagOne(ctx, view, item, tagged);
                        sub.Commit();
                        if (r.skipped) skipped.Add(new { index, element = item.Element, reason = "already tagged" });
                        else created.Add(r.result);
                    }
                    catch (Exception ex) when (ex is ToolException || ex is Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack();
                        failed.Add(new { index, element = item.Element, code = (ex as ToolException)?.Code ?? "REVIT_ERROR", message = ex.Message });
                    }
                }
            }

            return new
            {
                view = ViewPlane.DescribeView(view),
                created,
                skipped,
                failed,
                createdCount = created.Count,
                skippedCount = skipped.Count,
                failedCount = failed.Count
            };
        }

        private static (bool skipped, object result) TagOne(ToolContext ctx, View view, TagItem item, HashSet<string> tagged)
        {
            var resolved = ctx.Services.Lookup.Resolve(item.Element);
            var element = resolved.Element;
            var link = resolved.Link;
            var key = TagSupport.Key(element.Id.GetValue(), link?.Id.GetValue());
            if (tagged.Contains(key)) return (true, null);
            if (element is ElementType)
                throw new ToolException("INVALID_TARGET", $"'{item.Element}' is a type; tag an instance.");
            if (element.Category == null)
                throw new ToolException("INVALID_TARGET", $"'{item.Element}' has no category and cannot be tagged.");
            if (element is Room room && room.Area <= 0)
                throw new ToolException("ROOM_NOT_PLACED", $"Room '{room.Name}' is not placed or not enclosed.");

            var tagType = string.IsNullOrWhiteSpace(item.TagType) ? null : TagSupport.ResolveTagType(ctx, item.TagType, element.Category);
            XYZ point;
            if (item.Point != null)
            {
                point = ViewPlane.ToModel(view, item.Point.X, item.Point.Y);
            }
            else
            {
                point = TagSupport.AnchorPoint(element, view, link?.GetTotalTransform());
                if (point == null)
                    throw new ToolException("NO_LOCATION", $"'{item.Element}' has no location or bounding box to tag at.", "Pass point.");
            }
            point += ViewPlane.Offset(view, item.OffsetX, item.OffsetY);

            var tag = TagSupport.CreateTag(ctx.Doc, view, element, link, tagType, item.Leader, TagSupport.Orientation(item.Orientation), point);
            tagged.Add(key);
            var typeName = (ctx.Doc.GetElement(tag.GetTypeId()) as ElementType);
            return (false, new
            {
                element = item.Element,
                tagId = tag.Id.GetValue(),
                tagType = typeName == null ? null : LookupService.FullName(typeName),
                point = ViewPlane.FromModel(view, point)
            });
        }
    }

    // ---- tag_all_in_view (ANO-02) -------------------------------------------------------------------------------

    public sealed class TagAllInViewArgs
    {
        [JsonProperty("view", Required = Required.Always)]
        [Description("View to tag in (name or id): plan, ceiling plan, area plan, section, elevation or locked 3D view.")]
        public string View { get; set; }

        [JsonProperty("categories", Required = Required.Always)]
        [Description("Categories to tag, in any language (OST_Doors, Doors, Portas, OST_Rooms...).")]
        public List<string> Categories { get; set; }

        [JsonProperty("tagTypes")]
        [Description("Tag type per category, e.g. {\"OST_Doors\": \"M_Door Tag: Standard\"}. Categories not listed use their default tag.")]
        public Dictionary<string, string> TagTypes { get; set; }

        [JsonProperty("skipTagged")]
        [Description("Skip elements that already have a tag in this view (default true).")]
        [DefaultValue(true)]
        public bool SkipTagged { get; set; } = true;

        [JsonProperty("leader")]
        [Description("Draw leaders (default false).")]
        [DefaultValue(false)]
        public bool Leader { get; set; }

        [JsonProperty("orientation")]
        [Description("horizontal (default), vertical or model.")]
        [EnumValues("horizontal", "vertical", "model")]
        [DefaultValue("horizontal")]
        public string Orientation { get; set; } = "horizontal";

        [JsonProperty("elementIds")]
        [Description("Only these elements (ids) among those visible in the view.")]
        public List<long> ElementIds { get; set; }

        [JsonProperty("limit")]
        [Description("Maximum tags to create in this call, 1-5000 (default 1000). Run again to continue.")]
        [DefaultValue(1000)]
        public int Limit { get; set; } = 1000;
    }

    /// <summary>ANO-02: generalizes the legacy tag_all_rooms / tag_all_walls (kept as they are).</summary>
    [McpTool("tag_all_in_view", Kind = ToolKind.Write, Risk = Risk.Medium, DryRunDefault = true, Toolset = "annotation", TimeoutSeconds = 120,
        Description = "Tag every element of the given categories (any language) visible in a view, skipping the " +
                      "ones already tagged (local and linked tags detected). Rooms, areas and spaces get their own " +
                      "tags. Optional tag type per category, leader, orientation, elementIds and a limit per call. " +
                      "Elements that cannot be tagged are reported, not fatal. Use dryRun to see the counts first. " +
                      "Returns counts per category and the created tag ids. Undo: Ctrl+Z.")]
    public sealed class TagAllInViewTool : McpTool<TagAllInViewArgs>
    {
        public const int MaxListed = 200;

        protected override void Validate(TagAllInViewArgs args)
        {
            if (args.Categories == null || args.Categories.Count(c => !string.IsNullOrWhiteSpace(c)) == 0)
                throw new ToolException("INVALID_ARGUMENTS", "categories must list at least one category.");
            if (args.Limit < 1 || args.Limit > 5000)
                throw new ToolException("INVALID_ARGUMENTS", "limit must be between 1 and 5000.");
        }

        protected override object Run(ToolContext ctx, TagAllInViewArgs args)
        {
            var doc = ctx.Doc;
            var view = ViewPlane.RequireAnnotatable(ctx, args.View, false, "a tag");
            var categories = ctx.Services.Categories.ResolveAll(args.Categories);

            // Tag types per category, resolved before anything is created.
            var tagTypes = new Dictionary<ElementId, FamilySymbol>();
            foreach (var kv in args.TagTypes ?? new Dictionary<string, string>())
            {
                var cat = ctx.Services.Categories.Resolve(kv.Key);
                if (!categories.Any(c => c.Id == cat.Id))
                    throw new ToolException("INVALID_ARGUMENTS", $"tagTypes names '{kv.Key}', which is not in categories.");
                if (cat.Category == null)
                    throw new ToolException("CATEGORY_NOT_FOUND", $"Category '{kv.Key}' does not exist in this document.");
                tagTypes[cat.Id] = TagSupport.ResolveTagType(ctx, kv.Value, cat.Category);
            }

            var only = args.ElementIds == null ? null : new HashSet<long>(args.ElementIds);
            var tagged = args.SkipTagged ? TagSupport.TaggedKeys(doc, view) : new HashSet<string>();
            var orientation = TagSupport.Orientation(args.Orientation);
            var perCategory = new List<object>();
            var createdIds = new List<long>();
            var failures = new List<object>();
            var remaining = args.Limit;
            var limitReached = false;

            foreach (var cat in categories)
            {
                int candidates = 0, done = 0, alreadyTagged = 0, failed = 0, notPlaced = 0;
                var elements = new FilteredElementCollector(doc, view.Id).OfCategoryId(cat.Id).WhereElementIsNotElementType()
                    .ToElements().OrderBy(e => e.Id.GetValue()).ToList();
                tagTypes.TryGetValue(cat.Id, out var tagType);

                foreach (var e in elements)
                {
                    if (only != null && !only.Contains(e.Id.GetValue())) continue;
                    if (e is Room r && r.Area <= 0) { notPlaced++; continue; }
                    if (e is SpatialElement se && !(e is Room) && se.Area <= 0) { notPlaced++; continue; }
                    candidates++;
                    var key = TagSupport.Key(e.Id.GetValue(), null);
                    if (tagged.Contains(key)) { alreadyTagged++; continue; }
                    if (remaining <= 0) { limitReached = true; continue; }

                    try
                    {
                        var point = TagSupport.AnchorPoint(e, view, null);
                        if (point == null) throw new ToolException("NO_LOCATION", "No location or bounding box.");
                        var tag = TagSupport.CreateTag(doc, view, e, null, tagType, args.Leader, orientation, point);
                        tagged.Add(key);
                        createdIds.Add(tag.Id.GetValue());
                        done++;
                        remaining--;
                    }
                    catch (ToolException ex)
                    {
                        failed++;
                        if (failures.Count < MaxListed)
                            failures.Add(new { elementId = e.Id.GetValue(), category = cat.Name, code = ex.Code, message = ex.Message });
                        // A missing tag family fails every element of the category: stop early.
                        if (done == 0 && failed >= 3 && ex.Code == "TAG_FAILED") break;
                    }
                }

                perCategory.Add(new
                {
                    category = cat.Name,
                    categoryBuiltIn = cat.BuiltInName,
                    tagType = tagType == null ? "(default)" : LookupService.FullName(tagType),
                    candidates,
                    tagged = done,
                    alreadyTagged,
                    notPlaced,
                    failed
                });
            }

            if (createdIds.Count == 0 && failures.Count > 0)
                ctx.Warnings.Add(new ToolWarning("No tag was created; see failures (is a tag family loaded for the category?)."));

            return new
            {
                view = ViewPlane.DescribeView(view),
                categories = perCategory,
                createdCount = createdIds.Count,
                createdIds = createdIds.Take(1000).ToList(),
                createdIdsTruncated = createdIds.Count > 1000,
                failures,
                limitReached,
                hint = limitReached ? "The limit was reached; call again to tag the rest (already-tagged elements are skipped)." : null
            };
        }
    }
}
