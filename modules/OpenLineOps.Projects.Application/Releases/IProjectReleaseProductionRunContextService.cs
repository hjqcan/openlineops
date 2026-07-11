using OpenLineOps.Application.Abstractions.Results;

namespace OpenLineOps.Projects.Application.Releases;

public interface IProjectReleaseProductionRunContextService
{
    ValueTask<Result<ProjectReleaseProductionRunContext>> GetAsync(
        string projectId,
        string snapshotId,
        CancellationToken cancellationToken = default);
}

public sealed record ProjectReleaseProductionRunContext(
    string ProjectId,
    string ApplicationId,
    string SnapshotId,
    string TopologyId,
    string ProductionLineDefinitionId,
    string ProductModelId,
    string ProductModelIdentityInputKey,
    string EntryOperationId,
    string EntryStationSystemId,
    IReadOnlyCollection<string> StationSystemIds);
