namespace OpenLineOps.Projects.Domain.Snapshots;

public sealed record ProjectTargetReference(string Kind, string TargetId)
{
    public string Kind { get; } = string.IsNullOrWhiteSpace(Kind)
        ? throw new ArgumentException("Target kind cannot be empty.", nameof(Kind))
        : Kind.Trim();

    public string TargetId { get; } = string.IsNullOrWhiteSpace(TargetId)
        ? throw new ArgumentException("Target id cannot be empty.", nameof(TargetId))
        : TargetId.Trim();
}
