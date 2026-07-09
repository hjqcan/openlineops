namespace OpenLineOps.Engineering.Api.Models;

public sealed record PublishConfigurationSnapshotRequest(
    string? SnapshotId,
    string? ProcessDefinitionId,
    string? ProcessVersionId,
    string? RecipeId,
    string? StationProfileId);
