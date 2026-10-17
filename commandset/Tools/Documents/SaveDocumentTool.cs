using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using Autodesk.Revit.DB;
using Newtonsoft.Json;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Framework.Services;

namespace RevitMCPCommandSet.Tools.Documents
{
    public sealed class SaveDocumentArgs
    {
        [JsonProperty("saveAsPath")]
        [Description("Save a copy under a new name (Save As): absolute inside an allowed root, or relative to Documents\\RevitMCP\\models. The document then points to the new file. Not allowed for workshared models.")]
        public string SaveAsPath { get; set; }

        [JsonProperty("overwrite")]
        [Description("Save As only: replace an existing file (default false: fail with FILE_EXISTS).")]
        [DefaultValue(false)]
        public bool Overwrite { get; set; }

        [JsonProperty("compact")]
        [Description("Compact the file while saving (slower, smaller). Default false.")]
        [DefaultValue(false)]
        public bool Compact { get; set; }
    }

    /// <summary>
    /// INF-17: saves the target document (never the merely active one). Saving can't run inside a transaction, so
    /// the tool is self-transacted and handles dryRun itself. Workshared models are saved locally only: this tool
    /// never synchronizes with central.
    /// </summary>
    [McpTool("save_document", Kind = ToolKind.Write, Risk = Risk.Medium, Toolset = "documents", Batchable = false,
        TimeoutSeconds = 600,
        Description = "Save the target document to its own file, or Save As a new file inside the allowed folders " +
                      "(saveAsPath). Workshared models are saved locally only: it never synchronizes with central (ask " +
                      "the user to sync in Revit), and Save As is refused for them. A never-saved document needs " +
                      "saveAsPath. Saving can't be undone; dryRun reports what would be saved.")]
    public sealed class SaveDocumentTool : McpTool<SaveDocumentArgs>, ISelfTransactedTool
    {
        protected override object Run(ToolContext ctx, SaveDocumentArgs a)
        {
            var doc = ctx.Doc;
            if (doc.IsLinked)
                throw new ToolException("LINK_READ_ONLY", "The target is a linked document; it can't be saved from here.");
            if (doc.IsReadOnly)
                throw new ToolException(ToolErrorCodes.DocumentReadOnly, $"'{doc.Title}' is read-only.");

            bool saveAs = !string.IsNullOrWhiteSpace(a.SaveAsPath);
            string target;
            if (saveAs)
            {
                if (doc.IsWorkshared)
                    throw new ToolException("WORKSHARED_SAVE_AS_NOT_SUPPORTED",
                        "Save As of a workshared model would create a new central (or detach it); this tool doesn't do that.",
                        "Save without saveAsPath to save the local file, or ask the user to use Save As in Revit.");
                target = PathPolicy.Resolve(a.SaveAsPath, "models");
                var expected = doc.IsFamilyDocument ? ".rfa" : ".rvt";
                var ext = Path.GetExtension(target);
                if (string.IsNullOrEmpty(ext)) target += expected;
                else if (!ext.Equals(expected, StringComparison.OrdinalIgnoreCase) && !(ext.Equals(".rte", StringComparison.OrdinalIgnoreCase) && !doc.IsFamilyDocument))
                    throw new ToolException("INVALID_ARGUMENTS", $"A {(doc.IsFamilyDocument ? "family" : "project")} must be saved as {expected} (got '{ext}').");
                if (File.Exists(target) && !a.Overwrite)
                    throw new ToolException("FILE_EXISTS", $"'{target}' already exists.", "Pass overwrite: true or another saveAsPath.");
            }
            else
            {
                if (string.IsNullOrEmpty(doc.PathName))
                    throw new ToolException("NEVER_SAVED", $"'{doc.Title}' has never been saved, so it has no file to save to.",
                        "Pass saveAsPath (inside Documents\\RevitMCP or an allowed folder).");
                target = doc.PathName;
            }

            if (ctx.DryRun)
                return new
                {
                    wouldSave = true,
                    mode = saveAs ? "saveAs" : "save",
                    path = target,
                    isModified = doc.IsModified,
                    isWorkshared = doc.IsWorkshared,
                    synchronizeWithCentral = false
                };

            if (doc.IsModifiable)
                throw new ToolException(ToolErrorCodes.DocumentBusy, $"'{doc.Title}' has an open transaction.",
                    "Ask the user to finish the current command in Revit, then retry.");

            bool wasModified = doc.IsModified;
            try
            {
                if (saveAs)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    doc.SaveAs(target, new SaveAsOptions { OverwriteExistingFile = a.Overwrite, Compact = a.Compact });
                }
                else
                {
                    doc.Save(new SaveOptions { Compact = a.Compact });
                }
            }
            catch (Exception ex) when (!(ex is ToolException))
            {
                throw new ToolException("SAVE_FAILED", $"Revit could not save '{doc.Title}': {ex.Message}",
                    doc.IsWorkshared ? "Workshared: make sure this is a local copy (not the central file) and that you own the changes." : null);
            }

            var info = new FileInfo(doc.PathName ?? target);
            return new
            {
                mode = saveAs ? "saveAs" : "save",
                path = doc.PathName,
                savedAt = DateTime.Now.ToString("s", CultureInfo.InvariantCulture),
                bytes = info.Exists ? info.Length : (long?)null,
                hadUnsavedChanges = wasModified,
                isWorkshared = doc.IsWorkshared,
                synchronizedWithCentral = false
            };
        }
    }
}
