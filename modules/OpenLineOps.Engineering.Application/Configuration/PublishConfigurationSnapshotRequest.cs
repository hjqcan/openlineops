namespace OpenLineOps.Engineering.Application.Configuration;

public sealed record PublishConfigurationSnapshotRequest(
    string SnapshotId,
    string ProcessDefinitionId,
    string ProcessVersionId,
    string RecipeId,
    string StationProfileId);
