namespace OpenLineOps.Engineering.Api.Models;

public sealed record ConfigurationSnapshotResponse(
    string SnapshotId,
    string ProjectId,
    string ProcessDefinitionId,
    string ProcessVersionId,
    string RecipeId,
    string RecipeVersionId,
    string StationProfileId,
    string Status,
    DateTimeOffset PublishedAtUtc,
    IReadOnlyCollection<DeviceBindingSnapshotResponse> DeviceBindings);
