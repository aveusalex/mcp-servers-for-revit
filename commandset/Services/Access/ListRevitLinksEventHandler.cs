using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Links;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Access
{
    /// <summary>
    /// Builds a read-only link-instance tree for the broker-selected host
    /// document. A linked document may be queried through the Revit API, but it
    /// must not be modified, saved, closed, or exposed as a normal MCP target.
    /// </summary>
    public class ListRevitLinksEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private const int MaxTraversalDepth = 16;
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public RevitLinksResult ResultInfo { get; private set; }

        public void Prepare()
        {
            ResultInfo = null;
            _resetEvent.Reset();
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public void Execute(UIApplication app)
        {
            try
            {
                Document host = RevitDocumentContext.ResolveDocument(app);
                if (host == null)
                    throw new InvalidOperationException("No target Revit document is available.");

                var links = new List<RevitLinkInfo>();
                foreach (var instance in new FilteredElementCollector(host)
                    .OfClass(typeof(RevitLinkInstance))
                    .Cast<RevitLinkInstance>())
                {
                    links.Add(BuildLinkInfo(host, instance, Transform.Identity,
                        new List<string>(), 0, new HashSet<string>(StringComparer.Ordinal)));
                }

                ResultInfo = new RevitLinksResult
                {
                    Success = true,
                    HostDocumentTitle = host.Title,
                    HostDocumentId = DocumentIdOf(host),
                    LinkCount = links.Count,
                    Links = links,
                    Message = $"Found {links.Count} top-level Revit link instance(s). Linked models are read-only; open a source model separately to edit it."
                };
            }
            catch (Exception ex)
            {
                ResultInfo = new RevitLinksResult
                {
                    Success = false,
                    Message = $"Error listing Revit links: {ex.Message}"
                };
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        private static RevitLinkInfo BuildLinkInfo(
            Document parentDocument,
            RevitLinkInstance instance,
            Transform parentTransform,
            List<string> parentPath,
            int depth,
            HashSet<string> ancestors)
        {
            RevitLinkType linkType = parentDocument.GetElement(instance.GetTypeId()) as RevitLinkType;
            Document linkedDocument = null;
            try { linkedDocument = instance.GetLinkDocument(); } catch { }

            Transform localTransform = SafeTransform(instance);
            Transform hostTransform = parentTransform.Multiply(localTransform);
            var instancePath = new List<string>(parentPath) { instance.UniqueId };
            string traversalKey = $"{DocumentIdOf(parentDocument)}:{instance.UniqueId}";

            var info = new RevitLinkInfo
            {
                LinkInstanceUniqueId = instance.UniqueId,
                LinkInstanceElementId = instance.Id.IntegerValue,
                LinkTypeUniqueId = linkType?.UniqueId,
                Name = instance.Name,
                SourcePath = SourcePathOf(linkType),
                LoadStatus = LoadStatusOf(linkType),
                IsLoaded = linkedDocument != null,
                IsNested = linkType?.IsNestedLink ?? false,
                AttachmentType = AttachmentTypeOf(linkType),
                LinkedDocumentTitle = linkedDocument?.Title,
                LinkedDocumentId = DocumentIdOf(linkedDocument),
                InstancePath = instancePath,
                HostTransform = SerializeTransform(hostTransform)
            };

            // A malformed/repeated link graph must never recurse indefinitely.
            if (linkedDocument == null || depth >= MaxTraversalDepth || ancestors.Contains(traversalKey))
                return info;

            var nextAncestors = new HashSet<string>(ancestors, StringComparer.Ordinal)
            {
                traversalKey
            };
            foreach (var child in new FilteredElementCollector(linkedDocument)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>())
            {
                info.Children.Add(BuildLinkInfo(linkedDocument, child, hostTransform,
                    instancePath, depth + 1, nextAncestors));
            }

            return info;
        }

        private static Transform SafeTransform(RevitLinkInstance instance)
        {
            try { return instance.GetTotalTransform() ?? Transform.Identity; }
            catch { return Transform.Identity; }
        }

        private static RevitLinkTransform SerializeTransform(Transform transform)
        {
            return new RevitLinkTransform
            {
                Origin = Vector(transform.Origin),
                BasisX = Vector(transform.BasisX),
                BasisY = Vector(transform.BasisY),
                BasisZ = Vector(transform.BasisZ)
            };
        }

        private static double[] Vector(XYZ value) => new[] { value.X, value.Y, value.Z };

        private static string DocumentIdOf(Document document)
        {
            if (document == null) return null;
            try
            {
                var creationGuid = document.GetType().GetProperty("CreationGUID")?.GetValue(document);
                if (creationGuid is Guid guid && guid != Guid.Empty)
                    return guid.ToString("D");
                return document.ProjectInformation?.UniqueId;
            }
            catch { return null; }
        }

        private static string SourcePathOf(RevitLinkType linkType)
        {
            try
            {
                ExternalFileReference reference = linkType?.GetExternalFileReference();
                return reference == null
                    ? null
                    : ModelPathUtils.ConvertModelPathToUserVisiblePath(reference.GetAbsolutePath());
            }
            catch { return null; }
        }

        private static string LoadStatusOf(RevitLinkType linkType)
        {
            try { return linkType?.GetLinkedFileStatus().ToString() ?? "Unknown"; }
            catch { return "Unknown"; }
        }

        private static string AttachmentTypeOf(RevitLinkType linkType)
        {
            try { return linkType?.AttachmentType.ToString() ?? "Unknown"; }
            catch { return "Unknown"; }
        }

        public string GetName() => "List Revit Links";
    }
}
