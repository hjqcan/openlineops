namespace OpenLineOps.Engineering.Api.Models;

public sealed record EngineeringProjectResponse(
    string ProjectId,
    string WorkspaceId,
    string DisplayName,
    DateTimeOffset CreatedAtUtc,
    string? ActiveSnapshotId,
    IReadOnlyCollection<ConfigurationSnapshotResponse> Snapshots);
