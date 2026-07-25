using System.Collections.Generic;

namespace RevitMCPCommandSet.Models.Links
{
    /// <summary>
    /// Read-only representation of one Revit link instance. A linked model can
    /// appear more than once in a host, so LinkInstanceUniqueId -- not just its
    /// source path -- identifies an entry in this tree.
    /// </summary>
    public class RevitLinkInfo
    {
        public string LinkInstanceUniqueId { get; set; }
        public int LinkInstanceElementId { get; set; }
        public string LinkTypeUniqueId { get; set; }
        public string Name { get; set; }
        public string SourcePath { get; set; }
        public string LoadStatus { get; set; }
        public bool IsLoaded { get; set; }
        public bool IsNested { get; set; }
        public string AttachmentType { get; set; }
        public string LinkedDocumentTitle { get; set; }
        public string LinkedDocumentId { get; set; }
        public List<string> InstancePath { get; set; } = new List<string>();
        public RevitLinkTransform HostTransform { get; set; }
        public List<RevitLinkInfo> Children { get; set; } = new List<RevitLinkInfo>();
    }

    /// <summary>
    /// Transform from the linked model's internal coordinates to the host
    /// document's internal coordinates. Values are in Revit internal feet.
    /// </summary>
    public class RevitLinkTransform
    {
        public string Units { get; set; } = "internal-feet";
        public double[] Origin { get; set; }
        public double[] BasisX { get; set; }
        public double[] BasisY { get; set; }
        public double[] BasisZ { get; set; }
    }

    public class RevitLinksResult
    {
        public bool Success { get; set; }
        public string HostDocumentTitle { get; set; }
        public string HostDocumentId { get; set; }
        public int LinkCount { get; set; }
        public List<RevitLinkInfo> Links { get; set; } = new List<RevitLinkInfo>();
        public string Message { get; set; }
    }
}
