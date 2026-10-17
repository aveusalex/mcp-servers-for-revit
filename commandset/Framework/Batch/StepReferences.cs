using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace RevitMCPCommandSet.Framework
{
    /// <summary>
    /// References between steps of execute_batch. A string value that is exactly <c>"$steps[i].path"</c> inside a
    /// step's args is replaced by the value at <c>path</c> in the envelope of step <c>i</c>, e.g.
    /// <c>$steps[0].changes.created[0]</c> or <c>$steps[1].data.viewId</c>. A path that doesn't start with an
    /// envelope field (ok, data, changes, warnings, dryRun, error) is looked up under <c>data</c> and then
    /// <c>changes</c>, so <c>$steps[0].created[0]</c> also works. <c>"$$steps..."</c> escapes a literal string.
    /// </summary>
    /// <remarks>No Revit dependency: covered by the framework tests.</remarks>
    public static class StepReferences
    {
        private static readonly Regex Pattern = new Regex(
            @"^\$steps\[(?<index>\d+)\](?<path>(?:\.[A-Za-z_][A-Za-z0-9_]*|\[\d+\])*)$",
            RegexOptions.CultureInvariant);

        private static readonly Regex Segment = new Regex(
            @"\.(?<name>[A-Za-z_][A-Za-z0-9_]*)|\[(?<index>\d+)\]",
            RegexOptions.CultureInvariant);

        private static readonly HashSet<string> EnvelopeFields =
            new HashSet<string> { "ok", "data", "changes", "warnings", "dryRun", "error" };

        /// <summary>A parsed reference.</summary>
        public sealed class Reference
        {
            public Reference(int stepIndex, string path, string raw)
            {
                StepIndex = stepIndex;
                Path = path;
                Raw = raw;
            }

            public int StepIndex { get; }

            /// <summary>Path after the step, e.g. ".changes.created[0]" (may be empty: the whole envelope).</summary>
            public string Path { get; }

            public string Raw { get; }
        }

        /// <summary>True when the string is meant as a reference (starts with "$steps[").</summary>
        public static bool LooksLikeReference(string value) =>
            value != null && value.StartsWith("$steps[", System.StringComparison.Ordinal);

        public static bool TryParse(string value, out Reference reference)
        {
            reference = null;
            if (!LooksLikeReference(value)) return false;
            var match = Pattern.Match(value);
            if (!match.Success) return false;
            if (!int.TryParse(match.Groups["index"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                return false;
            reference = new Reference(index, match.Groups["path"].Value, value);
            return true;
        }

        /// <summary>Every reference string inside <paramref name="args"/>, in document order.</summary>
        public static IEnumerable<string> FindAll(JToken args)
        {
            if (args == null) yield break;
            foreach (var value in Values(args))
            {
                if (value.Type == JTokenType.String && LooksLikeReference((string)value.Value))
                    yield return (string)value.Value;
            }
        }

        /// <summary>
        /// Checks the references of step <paramref name="stepIndex"/> before anything runs: each must be well
        /// formed and point to an earlier step. Throws BATCH_REFERENCE_INVALID.
        /// </summary>
        public static void Validate(JToken args, int stepIndex)
        {
            foreach (var raw in FindAll(args))
            {
                if (!TryParse(raw, out var reference))
                {
                    throw new ToolException(ToolErrorCodes.BatchReferenceInvalid,
                        $"Step {stepIndex}: '{raw}' is not a valid step reference.",
                        "Use \"$steps[i].path\" with a dotted path and [n] indexes, e.g. \"$steps[0].changes.created[0]\".");
                }
                if (reference.StepIndex >= stepIndex)
                {
                    throw new ToolException(ToolErrorCodes.BatchReferenceInvalid,
                        $"Step {stepIndex}: '{raw}' refers to step {reference.StepIndex}, which has not run yet.",
                        "A step can only refer to the results of earlier steps.");
                }
            }
        }

        /// <summary>
        /// Returns a copy of <paramref name="args"/> with every reference replaced by the value it points to in
        /// <paramref name="results"/> (the envelopes of the steps that already ran). Throws
        /// BATCH_REFERENCE_UNRESOLVED when a path has no value.
        /// </summary>
        public static JToken Resolve(JToken args, IReadOnlyList<JObject> results, int stepIndex)
        {
            if (args == null) return null;
            var copy = args.DeepClone();
            if (copy is JValue rootValue)
            {
                return ResolveValue(rootValue, results, stepIndex) ?? copy;
            }

            foreach (var value in Values(copy).ToList())
            {
                var replacement = ResolveValue(value, results, stepIndex);
                if (replacement != null)
                {
                    value.Replace(replacement);
                }
            }
            return copy;
        }

        /// <summary>Looks a path up in a step envelope. Returns null when nothing is there.</summary>
        public static JToken Lookup(JObject envelope, string path)
        {
            if (envelope == null) return null;
            var segments = Segment.Matches(path ?? string.Empty).Cast<Match>().ToList();
            if (segments.Count == 0) return envelope;

            var first = segments[0].Groups["name"];
            if (first.Success && !EnvelopeFields.Contains(first.Value))
            {
                // Shorthand: "$steps[0].viewId" -> data.viewId, "$steps[0].created[0]" -> changes.created[0].
                return Walk(envelope["data"], segments) ?? Walk(envelope["changes"], segments);
            }
            return Walk(envelope, segments);
        }

        private static JToken ResolveValue(JValue value, IReadOnlyList<JObject> results, int stepIndex)
        {
            if (value.Type != JTokenType.String) return null;
            var text = (string)value.Value;
            if (text != null && text.StartsWith("$$steps[", System.StringComparison.Ordinal))
            {
                return new JValue(text.Substring(1));
            }
            if (!LooksLikeReference(text)) return null;

            if (!TryParse(text, out var reference) || reference.StepIndex >= stepIndex)
            {
                // Validate() runs first, so this only happens when it was skipped.
                Validate(new JValue(text), stepIndex);
            }

            var envelope = reference.StepIndex < results.Count ? results[reference.StepIndex] : null;
            var found = Lookup(envelope, reference.Path);
            if (found == null || found.Type == JTokenType.Null || found.Type == JTokenType.Undefined)
            {
                throw new ToolException(ToolErrorCodes.BatchReferenceUnresolved,
                    $"Step {stepIndex}: '{text}' has no value in the result of step {reference.StepIndex}.",
                    "Check the shape of that step's result (e.g. changes.created is empty when nothing was created).");
            }
            return found.DeepClone();
        }

        private static IEnumerable<JValue> Values(JToken token)
        {
            if (token is JValue value) return new[] { value };
            if (token is JContainer container) return container.DescendantsAndSelf().OfType<JValue>();
            return Enumerable.Empty<JValue>();
        }

        private static JToken Walk(JToken current, IList<Match> segments)
        {
            foreach (var segment in segments)
            {
                if (current == null) return null;
                var name = segment.Groups["name"];
                if (name.Success)
                {
                    current = current is JObject obj ? obj[name.Value] : null;
                }
                else
                {
                    var index = int.Parse(segment.Groups["index"].Value, CultureInfo.InvariantCulture);
                    current = current is JArray array && index < array.Count ? array[index] : null;
                }
            }
            return current;
        }
    }
}
