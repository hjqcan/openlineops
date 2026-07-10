namespace OpenLineOps.Processes.Application.Runtime;

public sealed record RuntimeConfigurationSnapshotDetails(
    string ConfigurationSnapshotId,
    string ProcessDefinitionId,
    string ProcessVersionId,
    string RecipeSnapshotId,
    string StationSystemId);
