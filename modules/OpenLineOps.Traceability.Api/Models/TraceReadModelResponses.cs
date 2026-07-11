namespace OpenLineOps.Traceability.Api.Models;

public sealed record StationTraceDashboardResponse(
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
    IReadOnlyCollection<StationRecentTraceResponse> RecentTraces);

public sealed record StationRecentTraceResponse(
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

public sealed record EngineeringTraceSearchResponse(
    PagedEngineeringTraceSearchRowsResponse Results,
    EngineeringTraceSearchFacetsResponse Facets,
    bool AreFacetsTruncated);

public sealed record PagedEngineeringTraceSearchRowsResponse(
    IReadOnlyCollection<EngineeringTraceSearchRowResponse> Items,
    int PageNumber,
    int PageSize,
    long TotalCount,
    long TotalPages);

public sealed record EngineeringTraceSearchRowResponse(
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

public sealed record EngineeringTraceSearchFacetsResponse(
    IReadOnlyCollection<TraceFacetCountResponse> Judgements,
    IReadOnlyCollection<TraceFacetCountResponse> ExecutionStatuses,
    IReadOnlyCollection<TraceFacetCountResponse> Dispositions,
    IReadOnlyCollection<TraceFacetCountResponse> StationSystems,
    IReadOnlyCollection<TraceFacetCountResponse> Devices,
    IReadOnlyCollection<TraceFacetCountResponse> ProductionLines,
    IReadOnlyCollection<TraceFacetCountResponse> ProcessVersions,
    IReadOnlyCollection<TraceFacetCountResponse> ProjectSnapshots);

public sealed record TraceFacetCountResponse(string Value, long Count);
