using System;

// Stand-ins for the real Contract A types. ManifestGen matches by NAME, so these only need the same shape.
namespace RevitMCPCommandSet.Framework
{
    public enum ToolKind { Read, Write, UI }

    public enum Risk { None, Low, Medium, High }

    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class McpToolAttribute : Attribute
    {
        public McpToolAttribute(string name) { Name = name; }
        public string Name { get; }
        public ToolKind Kind { get; set; } = ToolKind.Read;
        public Risk Risk { get; set; } = Risk.None;
        public string Toolset { get; set; } = "core";
        public bool Batchable { get; set; } = true;
        public bool Idempotent { get; set; }
        public int TimeoutSeconds { get; set; } = 50;
        public string Description { get; set; }
    }

    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class UnitAttribute : Attribute
    {
        public UnitAttribute(string unit) { Unit = unit; }
        public string Unit { get; }
    }

    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class EnumValuesAttribute : Attribute
    {
        public EnumValuesAttribute(params string[] values) { Values = values; }
        public string[] Values { get; }
    }

    public abstract class McpTool<TArgs> where TArgs : class, new()
    {
        protected abstract object Run(object ctx, TArgs args);
    }
}
