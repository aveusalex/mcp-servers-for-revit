using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace RevitMCPCommandSet.Framework.Services
{
    /// <summary>One page of a result set.</summary>
    public sealed class PageResult<T>
    {
        [JsonProperty("items")] public IList<T> Items { get; set; }
        [JsonProperty("total")] public int Total { get; set; }
        [JsonProperty("limit")] public int Limit { get; set; }
        [JsonProperty("offset")] public int Offset { get; set; }

        /// <summary>True when more items exist after this page (call again with offset = offset + limit).</summary>
        [JsonProperty("truncated")] public bool Truncated { get; set; }
    }

    /// <summary>Uniform limit/offset paging. Pure.</summary>
    public sealed class Paging
    {
        public const int DefaultLimit = 100;

        public static PageResult<T> Page<T>(IEnumerable<T> source, int limit, int offset, int maxLimit = 5000)
        {
            if (limit <= 0)
                throw new ToolException("INVALID_ARGUMENTS", $"limit must be at least 1 (got {limit}).");
            if (limit > maxLimit)
                throw new ToolException("INVALID_ARGUMENTS", $"limit must be at most {maxLimit} (got {limit}).",
                    "Page through the results with offset.");
            if (offset < 0)
                throw new ToolException("INVALID_ARGUMENTS", $"offset must be 0 or more (got {offset}).");

            var all = source as IList<T> ?? (source ?? Enumerable.Empty<T>()).ToList();
            var items = all.Skip(offset).Take(limit).ToList();
            return new PageResult<T>
            {
                Items = items,
                Total = all.Count,
                Limit = limit,
                Offset = offset,
                Truncated = offset + items.Count < all.Count
            };
        }

        /// <summary>Instance shortcut so tools can write ctx.Services.Paging.Of(...).</summary>
        public PageResult<T> Of<T>(IEnumerable<T> source, int limit, int offset, int maxLimit = 5000) =>
            Page(source, limit, offset, maxLimit);
    }
}
