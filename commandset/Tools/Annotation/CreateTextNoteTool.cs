using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;
using RevitMCPCommandSet.Tools.Annotation.Support;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Annotation
{
    public sealed class TextNoteItem
    {
        [JsonProperty("text", Required = Required.Always)]
        [Description("Text of the note (\\n for line breaks).")]
        public string Text { get; set; }

        [JsonProperty("position", Required = Required.Always)]
        [Description("Insertion point in the view plane (mm). Plans: model X/Y; sections/elevations: horizontal coordinate and elevation; sheets/drafting: sheet/view coordinates.")]
        public ViewPointMm Position { get; set; }

        [JsonProperty("textType")]
        [Description("Text type name or id (e.g. \"2.5mm Arial\"). Default: the document's default text type.")]
        public string TextType { get; set; }

        [JsonProperty("widthMm")]
        [Description("Wrap width on paper (as printed). Omit for a single line that grows with the text.")]
        [Unit("mm")]
        public double? WidthMm { get; set; }

        [JsonProperty("rotationDeg")]
        [Description("Rotation in the view plane, counter-clockwise (default 0).")]
        [Unit("deg")]
        [DefaultValue(0)]
        public double RotationDeg { get; set; }

        [JsonProperty("hAlign")]
        [Description("Horizontal alignment (default left).")]
        [EnumValues("left", "center", "right")]
        [DefaultValue("left")]
        public string HAlign { get; set; } = "left";

        [JsonProperty("vAlign")]
        [Description("Vertical alignment relative to the insertion point (default top).")]
        [EnumValues("top", "middle", "bottom")]
        [DefaultValue("top")]
        public string VAlign { get; set; } = "top";
    }

    public sealed class CreateTextNoteArgs
    {
        [JsonProperty("view", Required = Required.Always)]
        [Description("View or sheet to place the notes in (name or id).")]
        public string View { get; set; }

        [JsonProperty("notes", Required = Required.Always)]
        [Description("Notes to create, up to 200.")]
        public List<TextNoteItem> Notes { get; set; }
    }

    /// <summary>ANO-03.</summary>
    [McpTool("create_text_note", Kind = ToolKind.Write, Risk = Risk.Low, Toolset = "annotation",
        Description = "Create text notes in a view or sheet (batch): text, position in the view plane in mm (plans: " +
                      "model X/Y; sections/elevations: horizontal coordinate and elevation), text type by name, wrap " +
                      "width in paper mm, rotation in degrees and alignment. One transaction, all or nothing. Returns " +
                      "the note ids and positions read back. Undo: Ctrl+Z or delete the returned ids.")]
    public sealed class CreateTextNoteTool : McpTool<CreateTextNoteArgs>
    {
        protected override void Validate(CreateTextNoteArgs args)
        {
            if (args.Notes == null || args.Notes.Count == 0)
                throw new ToolException("INVALID_ARGUMENTS", "notes must list at least one note.");
            if (args.Notes.Count > 200)
                throw new ToolException("TOO_MANY_ITEMS", $"{args.Notes.Count} notes; the limit is 200 per call.");
            foreach (var n in args.Notes)
            {
                if (n == null || string.IsNullOrEmpty(n.Text))
                    throw new ToolException("INVALID_ARGUMENTS", "Every note needs text.");
                if (n.Position == null)
                    throw new ToolException("INVALID_ARGUMENTS", "Every note needs a position.");
                if (n.WidthMm.HasValue && (n.WidthMm.Value <= 0 || n.WidthMm.Value > 2000))
                    throw new ToolException("INVALID_ARGUMENTS", "widthMm must be between 0 and 2000.");
            }
        }

        protected override object Run(ToolContext ctx, CreateTextNoteArgs args)
        {
            var doc = ctx.Doc;
            var view = ViewPlane.RequireAnnotatable(ctx, args.View, true, "a text note");
            var types = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().ToList();
            var defaultType = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
            if ((defaultType == null || defaultType == ElementId.InvalidElementId) && types.Count > 0) defaultType = types[0].Id;

            var created = new List<object>();
            for (var i = 0; i < args.Notes.Count; i++)
            {
                var n = args.Notes[i];
                try
                {
                    var typeId = string.IsNullOrWhiteSpace(n.TextType)
                        ? defaultType
                        : ctx.Services.Lookup.ByNameOrId(n.TextType, types, "text type").Id;
                    if (typeId == null || typeId == ElementId.InvalidElementId)
                        throw new ToolException("NO_TEXT_TYPE", "The document has no text type.");

                    var options = new TextNoteOptions(typeId)
                    {
                        HorizontalAlignment = HAlign(n.HAlign),
                        VerticalAlignment = VAlign(n.VAlign),
                        Rotation = n.RotationDeg * Math.PI / 180.0
                    };
                    var position = ViewPlane.ToModel(view, n.Position.X, n.Position.Y);
                    TextNote note;
                    if (n.WidthMm.HasValue)
                    {
                        var width = UnitPolicy.ToInternal(n.WidthMm.Value, UnitSpec.Length);
                        var min = TextElement.GetMinimumAllowedWidth(doc, typeId);
                        var max = TextElement.GetMaximumAllowedWidth(doc, typeId);
                        if (width < min || width > max)
                            throw new ToolException("INVALID_ARGUMENTS",
                                $"widthMm must be between {UnitPolicy.FromInternal(min, UnitSpec.Length, out _):0.##} and {UnitPolicy.FromInternal(max, UnitSpec.Length, out _):0.##} for this text type.");
                        note = TextNote.Create(doc, view.Id, position, width, n.Text, options);
                    }
                    else
                    {
                        note = TextNote.Create(doc, view.Id, position, n.Text, options);
                    }

                    created.Add(new
                    {
                        index = i,
                        id = note.Id.GetValue(),
                        text = note.Text?.TrimEnd('\r', '\n'),
                        textType = doc.GetElement(note.GetTypeId())?.Name,
                        position = ViewPlane.FromModel(view, note.Coord),
                        widthMm = Math.Round(UnitPolicy.FromInternal(note.Width, UnitSpec.Length, out _), 2)
                    });
                }
                catch (ToolException ex)
                {
                    throw new ToolException(ex.Code, $"Note [{i}]: {ex.Message} No note was created.", ex.Hint, ex.RecommendedTool);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    throw new ToolException("TEXT_NOTE_FAILED", $"Note [{i}]: {ex.Message} No note was created.");
                }
            }

            return new { view = ViewPlane.DescribeView(view), created, createdCount = created.Count };
        }

        private static HorizontalTextAlignment HAlign(string s)
        {
            switch ((s ?? "left").Trim().ToLowerInvariant())
            {
                case "center": return HorizontalTextAlignment.Center;
                case "right": return HorizontalTextAlignment.Right;
                default: return HorizontalTextAlignment.Left;
            }
        }

        private static VerticalTextAlignment VAlign(string s)
        {
            switch ((s ?? "top").Trim().ToLowerInvariant())
            {
                case "middle": return VerticalTextAlignment.Middle;
                case "bottom": return VerticalTextAlignment.Bottom;
                default: return VerticalTextAlignment.Top;
            }
        }
    }
}
