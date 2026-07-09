namespace OpenLineOps.Engineering.Application.Configuration;

public sealed record EngineeringProjectDetails(
    string ProjectId,
    string WorkspaceId,
    string DisplayName,
    DateTimeOffset CreatedAtUtc,
    string? ActiveSnapshotId,
    IReadOnlyCollection<ConfigurationSnapshotDetails> Snapshots);
