namespace OpenLineOps.Engineering.Application.Configuration;

public sealed record ConfigurationSnapshotDetails(
    string SnapshotId,
    string ProjectId,
    string ProcessDefinitionId,
    string ProcessVersionId,
    string RecipeId,
    string RecipeVersionId,
    string StationProfileId,
    string Status,
    DateTimeOffset PublishedAtUtc,
    IReadOnlyCollection<DeviceBindingSnapshotDetails> DeviceBindings);
