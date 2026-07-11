using OpenLineOps.Application.Abstractions.Paging;
using OpenLineOps.Application.Abstractions.Results;

namespace OpenLineOps.Traceability.Application.ReadModels;

public interface ITraceReadModelService
{
    Task<Result<StationTraceDashboardReadModel>> GetStationDashboardAsync(
        StationTraceDashboardQuery query,
        CancellationToken cancellationToken = default);

    Task<Result<EngineeringTraceSearchReadModel>> SearchForEngineeringAsync(
        EngineeringTraceSearchQuery query,
        CancellationToken cancellationToken = default);
}

public sealed record StationTraceDashboardQuery(
    string? StationSystemId,
    DateTimeOffset? CompletedFromUtc = null,
    DateTimeOffset? CompletedToUtc = null,
    int RecentLimit = 10);

public sealed record EngineeringTraceSearchQuery(
    Guid? ProductionRunId = null,
    string? ProductModelId = null,
    string? ProductionUnitIdentityInputKey = null,
    string? ProductionUnitIdentityValue = null,
    string? LotId = null,
    string? CarrierId = null,
    string? ActorId = null,
    string? ExecutionStatus = null,
    string? Judgement = null,
    string? Disposition = null,
    string? ProjectId = null,
    string? ApplicationId = null,
    string? ProjectSnapshotId = null,
    string? TopologyId = null,
    string? ProductionLineDefinitionId = null,
    string? OperationId = null,
    string? StationSystemId = null,
    string? StationId = null,
    string? ProcessDefinitionId = null,
    string? ProcessVersionId = null,
    string? ConfigurationSnapshotId = null,
    string? RecipeSnapshotId = null,
    string? ResourceKind = null,
    string? ResourceId = null,
    string? DeviceId = null,
    DateTimeOffset? CompletedFromUtc = null,
    DateTimeOffset? CompletedToUtc = null,
    PagedRequest? Paging = null);

public sealed record StationTraceDashboardReadModel(
    string StationSystemId,
    DateTimeOffset? CompletedFromUtc,
    DateTimeOffset? CompletedToUtc,
    long TotalCount,
    long PassedCount,
    long FailedCount,
    long AbortedCount,
    long UnknownCount,
    long NotApplicableCount,
    DateTimeOffset? FirstCompletedAtUtc,
    DateTimeOffset? LastCompletedAtUtc,
    bool IsWindowTruncated,
    IReadOnlyCollection<StationRecentTraceReadModel> RecentTraces);

public sealed record StationRecentTraceReadModel(
    Guid TraceRecordId,
    Guid ProductionRunId,
    string ProjectId,
    string ApplicationId,
    string ProjectSnapshotId,
    string TopologyId,
    string ProductionLineDefinitionId,
    string ProductModelId,
    string ProductionUnitIdentityInputKey,
    string ProductionUnitIdentityValue,
    string? LotId,
    string? CarrierId,
    string ExecutionStatus,
    string Judgement,
    string Disposition,
    DateTimeOffset CompletedAtUtc,
    int OperationCount,
    int CommandCount,
    int FailedCommandCount,
    int MeasurementCount,
    int FailedMeasurementCount,
    int ArtifactCount,
    int IncidentCount,
    int GenealogyCount,
    int MaterialLocationTransitionCount,
    int SlotOccupancyTransitionCount,
    int DispositionTransitionCount);

public sealed record EngineeringTraceSearchReadModel(
    PagedResult<EngineeringTraceSearchRowReadModel> Results,
    EngineeringTraceSearchFacetsReadModel Facets,
    bool AreFacetsTruncated);

public sealed record EngineeringTraceSearchRowReadModel(
    Guid TraceRecordId,
    Guid ProductionRunId,
    string ProjectId,
    string ApplicationId,
    string ProjectSnapshotId,
    string TopologyId,
    string ProductionLineDefinitionId,
    string ProductModelId,
    string ProductionUnitIdentityInputKey,
    string ProductionUnitIdentityValue,
    string? LotId,
    string? CarrierId,
    string ActorId,
    string ExecutionStatus,
    string Judgement,
    string Disposition,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    int OperationCount,
    int FailedOperationCount,
    int CommandCount,
    int FailedCommandCount,
    int MeasurementCount,
    int FailedMeasurementCount,
    int ArtifactCount,
    int IncidentCount,
    int RouteDecisionCount,
    int GenealogyCount,
    int MaterialLocationTransitionCount,
    int SlotOccupancyTransitionCount,
    int DispositionTransitionCount);

public sealed record EngineeringTraceSearchFacetsReadModel(
    IReadOnlyCollection<TraceFacetCountReadModel> Judgements,
    IReadOnlyCollection<TraceFacetCountReadModel> ExecutionStatuses,
    IReadOnlyCollection<TraceFacetCountReadModel> Dispositions,
    IReadOnlyCollection<TraceFacetCountReadModel> StationSystems,
    IReadOnlyCollection<TraceFacetCountReadModel> Devices,
    IReadOnlyCollection<TraceFacetCountReadModel> ProductionLines,
    IReadOnlyCollection<TraceFacetCountReadModel> ProcessVersions,
    IReadOnlyCollection<TraceFacetCountReadModel> ProjectSnapshots);

public sealed record TraceFacetCountReadModel(string Value, long Count);
