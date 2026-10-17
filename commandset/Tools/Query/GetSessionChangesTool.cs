using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Query.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Query
{
    public sealed class GetSessionChangesArgs
    {
        [JsonProperty("operation")]
        [Description("get (default): report changes; reset: forget what was recorded for this document and start a new baseline.")]
        [EnumValues("get", "reset")]
        [DefaultValue("get")]
        public string Operation { get; set; } = "get";

        [JsonProperty("sinceSeq")]
        [Description("Only events after this sequence number (lastSeq of a previous call). Default: since tracking started.")]
        public long? SinceSeq { get; set; }

        [JsonProperty("since")]
        [Description("Only events after this ISO-8601 time (e.g. 2026-10-17T14:00:00Z).")]
        public string Since { get; set; }

        [JsonProperty("categories")]
        [Description("Only elements of these categories (any language).")]
        public List<string> Categories { get; set; }

        [JsonProperty("onlyByMcp")]
        [Description("Only changes made by this MCP's framework tools (transactions named \"MCP: ...\"), default false.")]
        [DefaultValue(false)]
        public bool OnlyByMcp { get; set; }

        [JsonProperty("includeModified")]
        [Description("Include modified elements (default true; modifications are often numerous).")]
        [DefaultValue(true)]
        public bool IncludeModified { get; set; } = true;

        [JsonProperty("limit")]
        [Description("Page size, 1-2000 (default 200).")]
        [DefaultValue(200)]
        public int Limit { get; set; } = 200;

        [JsonProperty("offset")]
        [Description("Items to skip, for paging (default 0).")]
        [DefaultValue(0)]
        public int Offset { get; set; }
    }

    /// <summary>UND-09: what changed in the target document during this Revit session.</summary>
    [McpTool("get_session_changes", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "query", Idempotent = false,
        Description = "What changed in the target document during this session: net created / modified / deleted " +
                      "elements (id, UniqueId, name, category, transaction names, by MCP or by the user), the last " +
                      "created element still in the model, and lastSeq to ask again for only newer changes (sinceSeq). " +
                      "Tracking starts at the first call of this tool in the Revit session (earlier changes are " +
                      "unknown: the first call returns trackingStarted=true). Undo/redo is included. Filter by time, " +
                      "categories or onlyByMcp; paged. operation=reset starts a new baseline. Does not change the model.")]
    public sealed class GetSessionChangesTool : McpTool<GetSessionChangesArgs>
    {
        protected override void Validate(GetSessionChangesArgs args)
        {
            args.Operation = (args.Operation ?? "get").Trim().ToLowerInvariant();
            QueryHelpers.CheckPaging(args.Limit, args.Offset, 2000);
            if (!string.IsNullOrWhiteSpace(args.Since) &&
                !DateTime.TryParse(args.Since, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out _))
                throw new ToolException("INVALID_ARGUMENTS", $"since '{args.Since}' is not an ISO-8601 date/time.");
        }

        protected override object Run(ToolContext ctx, GetSessionChangesArgs args)
        {
            var doc = ctx.Doc;
            var started = !SessionChangeLog.IsSubscribed;
            SessionChangeLog.EnsureSubscribed(ctx.UiApp?.Application);
            if (!SessionChangeLog.IsSubscribed)
                throw new ToolException("NOT_AVAILABLE", "Change tracking could not start (no Revit application).");

            if (args.Operation == "reset")
            {
                SessionChangeLog.Reset(doc);
                return new { reset = true, trackingSince = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) };
            }

            var log = SessionChangeLog.For(doc, true);
            // DocumentChanged and tool calls both run on Revit's thread: a plain copy is consistent.
            var events = log.Events.ToList();
            if (args.SinceSeq.HasValue) events = events.Where(ev => ev.Seq > args.SinceSeq.Value).ToList();
            if (!string.IsNullOrWhiteSpace(args.Since))
            {
                var since = DateTime.Parse(args.Since, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
                events = events.Where(ev => ev.Utc > since).ToList();
            }
            if (args.OnlyByMcp) events = events.Where(ev => ev.ByMcp).ToList();

            // Net membership across events; remember the last event that touched each id.
            var acc = new ChangeAccumulator(int.MaxValue);
            var lastEvent = new Dictionary<long, ChangeEvent>();
            var byMcp = new Dictionary<long, bool>();
            foreach (var ev in events)
            {
                foreach (var id in ev.Added) { acc.Created(id); Touch(id, ev); }
                foreach (var id in ev.Modified) { acc.Modified(id); Touch(id, ev); }
                foreach (var id in ev.Deleted) { acc.Deleted(id); Touch(id, ev); }
            }

            void Touch(long id, ChangeEvent ev)
            {
                lastEvent[id] = ev;
                byMcp[id] = (byMcp.TryGetValue(id, out var b) && b) || ev.ByMcp;
            }

            var set = acc.ToChangeSet();
            var cats = ctx.Services.Categories.ResolveAll(args.Categories);
            var catIds = cats.Count > 0 ? new HashSet<long>(cats.Select(c => c.Id.GetValue())) : null;

            var rows = new List<(long id, string change, Element el)>();
            void AddRows(IEnumerable<long> ids, string change, bool exists)
            {
                foreach (var id in ids)
                {
                    var el = exists ? doc.GetElement(id.ToRevitElementId()) : null;
                    if (catIds != null)
                    {
                        long? cat = el?.Category?.Id.GetValue();
                        if (cat == null && log.CategoryOf.TryGetValue(id, out var cached)) cat = cached;
                        if (cat == null || !catIds.Contains(cat.Value)) continue;
                    }
                    else if (exists && el != null && el.Category == null && !(el is ElementType))
                    {
                        // internal bookkeeping elements (sketch planes, analytical data...) are noise
                        continue;
                    }
                    rows.Add((id, change, el));
                }
            }
            AddRows(set.Created, "created", true);
            if (args.IncludeModified) AddRows(set.Modified, "modified", true);
            AddRows(set.Deleted, "deleted", false);

            var counts = rows.GroupBy(r => r.change).ToDictionary(g => g.Key, g => g.Count());
            var ordered = rows.OrderByDescending(r => lastEvent[r.id].Seq).ThenBy(r => r.id).ToList();
            var page = Paging.Page(ordered, args.Limit, args.Offset, 2000);

            object lastCreated = null;
            var lc = rows.Where(r => r.change == "created" && r.el != null && r.el.Category != null)
                .OrderByDescending(r => lastEvent[r.id].Seq).ThenByDescending(r => r.id).FirstOrDefault();
            if (lc.el != null) lastCreated = Row(lc, lastEvent, byMcp);

            return new
            {
                trackingStarted = started,
                trackingSince = log.StartedUtc.ToString("o", CultureInfo.InvariantCulture),
                historyTrimmed = log.Trimmed,
                eventCount = events.Count,
                lastSeq = log.Events.Count > 0 ? log.Events[log.Events.Count - 1].Seq : (args.SinceSeq ?? 0),
                counts,
                lastCreated,
                items = page.Items.Select(r => Row(r, lastEvent, byMcp)).ToList(),
                total = page.Total,
                limit = page.Limit,
                offset = page.Offset,
                truncated = page.Truncated
            };
        }

        private static object Row((long id, string change, Element el) r, Dictionary<long, ChangeEvent> lastEvent,
            Dictionary<long, bool> byMcp)
        {
            var ev = lastEvent[r.id];
            return new
            {
                id = r.id,
                change = r.change,
                uniqueId = r.el?.UniqueId,
                name = QueryHelpers.SafeName(r.el),
                category = r.el?.Category?.Name,
                byMcp = byMcp.TryGetValue(r.id, out var b) && b,
                lastSeq = ev.Seq,
                lastChangedUtc = ev.Utc.ToString("o", CultureInfo.InvariantCulture),
                transactions = ev.Transactions
            };
        }
    }
}
