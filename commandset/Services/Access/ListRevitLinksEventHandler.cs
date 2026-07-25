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

                var hostInstances = new FilteredElementCollector(host)
                    .OfClass(typeof(RevitLinkInstance))
                    .Cast<RevitLinkInstance>()
                    .ToList();
                var consumedHostNestedInstances = new HashSet<string>(StringComparer.Ordinal);
                var links = new List<RevitLinkInfo>();

                // Revit can surface a nested link both as a child of its parent
                // document and as a direct instance in the host. Only true roots
                // belong at the top level; nested host instances are matched below
                // to provide the child's effective loaded state and transform.
                foreach (var instance in hostInstances.Where(i => !IsNestedInstance(host, i)))
                {
                    links.Add(BuildLinkInfo(host, instance, Transform.Identity,
                        new List<string>(), 0, new HashSet<string>(StringComparer.Ordinal),
                        host, hostInstances, consumedHostNestedInstances));
                }

                int totalLinkInstanceCount = CountTree(links);

                ResultInfo = new RevitLinksResult
                {
                    Success = true,
                    HostDocumentTitle = host.Title,
                    HostDocumentId = DocumentIdOf(host),
                    LinkCount = links.Count,
                    TopLevelLinkCount = links.Count,
                    TotalLinkInstanceCount = totalLinkInstanceCount,
                    Links = links,
                    Message = $"Found {links.Count} top-level and {totalLinkInstanceCount} total Revit link instance(s). Linked models are read-only; open a source model separately to edit it."
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
            HashSet<string> ancestors,
            Document hostDocument,
            List<RevitLinkInstance> hostInstances,
            HashSet<string> consumedHostNestedInstances)
        {
            RevitLinkType linkType = parentDocument.GetElement(instance.GetTypeId()) as RevitLinkType;
            Transform localTransform = SafeTransform(instance);
            Transform hostTransform = parentTransform.Multiply(localTransform);
            RevitLinkInstance effectiveInstance = instance;
            Document linkedDocument = SafeLinkedDocument(instance);

            // A nested link can be loaded in the host even when the link instance
            // exposed by its parent document reports no loaded document. Match the
            // host representation by source and composed transform so the tree has
            // one node with the effective host identity/state rather than a
            // duplicate root plus an apparently unloaded child.
            if (linkedDocument == null && depth > 0)
            {
                RevitLinkInstance hostRepresentation = FindLoadedHostRepresentation(
                    hostDocument, hostInstances, linkType, hostTransform,
                    consumedHostNestedInstances);
                if (hostRepresentation != null)
                {
                    effectiveInstance = hostRepresentation;
                    linkedDocument = SafeLinkedDocument(hostRepresentation);
                    hostTransform = SafeTransform(hostRepresentation);
                    consumedHostNestedInstances.Add(hostRepresentation.UniqueId);
                }
            }

            RevitLinkType effectiveType = effectiveInstance == instance
                ? linkType
                : hostDocument.GetElement(effectiveInstance.GetTypeId()) as RevitLinkType ?? linkType;
            var instancePath = new List<string>(parentPath) { effectiveInstance.UniqueId };
            string traversalKey = $"{DocumentIdOf(parentDocument)}:{instance.UniqueId}";

            var info = new RevitLinkInfo
            {
                LinkInstanceUniqueId = effectiveInstance.UniqueId,
                LinkInstanceElementId = effectiveInstance.Id.IntegerValue,
                LinkTypeUniqueId = effectiveType?.UniqueId,
                Name = effectiveInstance.Name,
                SourcePath = SourcePathOf(effectiveType),
                LoadStatus = linkedDocument != null ? "Loaded" : LoadStatusOf(effectiveType),
                IsLoaded = linkedDocument != null,
                IsNested = linkType?.IsNestedLink ?? false,
                AttachmentType = AttachmentTypeOf(effectiveType),
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
                    instancePath, depth + 1, nextAncestors, hostDocument,
                    hostInstances, consumedHostNestedInstances));
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

        private static Document SafeLinkedDocument(RevitLinkInstance instance)
        {
            try { return instance.GetLinkDocument(); }
            catch { return null; }
        }

        private static bool IsNestedInstance(Document document, RevitLinkInstance instance)
        {
            try
            {
                return (document.GetElement(instance.GetTypeId()) as RevitLinkType)?.IsNestedLink ?? false;
            }
            catch { return false; }
        }

        private static RevitLinkInstance FindLoadedHostRepresentation(
            Document hostDocument,
            IEnumerable<RevitLinkInstance> hostInstances,
            RevitLinkType nestedType,
            Transform expectedHostTransform,
            ISet<string> consumed)
        {
            string sourcePath = SourcePathOf(nestedType);
            if (string.IsNullOrEmpty(sourcePath)) return null;

            return hostInstances
                .Where(candidate => !consumed.Contains(candidate.UniqueId))
                .Where(candidate => IsNestedInstance(hostDocument, candidate))
                .Where(candidate => SafeLinkedDocument(candidate) != null)
                .Where(candidate => string.Equals(
                    SourcePathOf(hostDocument.GetElement(candidate.GetTypeId()) as RevitLinkType),
                    sourcePath,
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(candidate => TransformDistance(SafeTransform(candidate), expectedHostTransform))
                .FirstOrDefault();
        }

        private static double TransformDistance(Transform left, Transform right)
        {
            return VectorDistance(left.Origin, right.Origin)
                + VectorDistance(left.BasisX, right.BasisX)
                + VectorDistance(left.BasisY, right.BasisY)
                + VectorDistance(left.BasisZ, right.BasisZ);
        }

        private static double VectorDistance(XYZ left, XYZ right)
        {
            return Math.Abs(left.X - right.X)
                + Math.Abs(left.Y - right.Y)
                + Math.Abs(left.Z - right.Z);
        }

        private static int CountTree(IEnumerable<RevitLinkInfo> links)
        {
            return links.Sum(link => 1 + CountTree(link.Children));
        }

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
