namespace OpenLineOps.Topology.Domain.Layouts;

public sealed record LayoutTargetReference(LayoutTargetKind Kind, string TargetId)
{
    public string TargetId { get; } = string.IsNullOrWhiteSpace(TargetId)
        ? throw new ArgumentException("Target id cannot be empty.", nameof(TargetId))
        : TargetId.Trim();
}
