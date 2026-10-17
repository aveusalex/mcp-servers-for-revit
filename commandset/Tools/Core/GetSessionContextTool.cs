using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Utils;

namespace RevitMCPCommandSet.Tools.Core
{
    /// <summary>get_session_context has no arguments (the server adds broker/approval info around it).</summary>
    public sealed class GetSessionContextArgs
    {
    }

    /// <summary>INF-01, Revit side: add-in version and capability hash, Revit build, open documents, active/target.</summary>
    [McpTool("get_session_context", Kind = ToolKind.Read, Risk = Risk.None, Toolset = "core", Idempotent = true,
        TimeoutSeconds = 20,
        Description = "Call first. Returns this Revit session's state: add-in version and capabilityHash (compare with " +
                      "the server's manifest to detect an outdated add-in), Revit version/build/language, every open " +
                      "document (docId, title, isFamilyDocument, isWorkshared, isActive, isReadOnly, isModified), the " +
                      "active document, the targeted document, the active view and the number of queued tool calls.")]
    [NoDocumentRequired]
    public sealed class GetSessionContextTool : McpTool<GetSessionContextArgs>
    {
        protected override object Run(ToolContext ctx, GetSessionContextArgs args)
        {
            var app = ctx.UiApp?.Application;
            var activeDoc = ctx.UiApp?.ActiveUIDocument?.Document;
            var activeDocId = RevitDocumentContext.GetDocId(activeDoc);
            var targetDocId = RevitDocumentContext.GetDocId(ctx.Doc);

            var documents = new List<object>();
            if (app != null)
            {
                foreach (Document doc in app.Documents)
                {
                    if (doc.IsLinked) continue;
                    var docId = RevitDocumentContext.GetDocId(doc);
                    documents.Add(new
                    {
                        docId,
                        title = doc.Title,
                        pathName = doc.PathName ?? string.Empty,
                        isFamilyDocument = doc.IsFamilyDocument,
                        isWorkshared = doc.IsWorkshared,
                        isActive = activeDoc != null && doc.Equals(activeDoc),
                        isTarget = ctx.Doc != null && doc.Equals(ctx.Doc),
                        isReadOnly = doc.IsReadOnly,
                        isModified = doc.IsModified,
                        routable = docId != null
                    });
                }
            }

            object activeView = null;
            var view = ctx.UiApp?.ActiveUIDocument?.ActiveView;
            if (view != null)
            {
                activeView = new { id = view.Id.GetValue(), name = view.Name, viewType = view.ViewType.ToString() };
            }

            var assembly = typeof(ToolRegistry).Assembly;
            var problems = ToolRegistry.ContractProblems;

            return new
            {
                addin = new
                {
                    assembly = assembly.GetName().Name,
                    version = assembly.GetName().Version?.ToString(),
                    informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                    capabilityHash = ToolRegistry.CapabilityHash,
                    toolCount = ToolRegistry.All.Count(),
                    contractProblems = problems.Count > 0 ? problems : null
                },
                revit = app == null
                    ? null
                    : new
                    {
                        versionNumber = app.VersionNumber,
                        versionName = app.VersionName,
                        versionBuild = app.VersionBuild,
                        language = app.Language.ToString(),
                        username = app.Username
                    },
                documents,
                activeDocId,
                targetDocId,
                activeView,
                queue = new { pending = ToolDispatcher.Instance?.PendingCount ?? 0 }
            };
        }
    }
}
