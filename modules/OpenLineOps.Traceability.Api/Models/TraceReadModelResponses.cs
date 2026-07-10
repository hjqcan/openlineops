namespace OpenLineOps.Traceability.Api.Models;

public sealed record StationTraceDashboardResponse(
    string StationId,
    DateTimeOffset? CompletedFromUtc,
    DateTimeOffset? CompletedToUtc,
    long TotalCount,
    long PassedCount,
    long FailedCount,
    long AbortedCount,
    long UnknownCount,
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
    string DutModelId,
    string DutIdentityInputKey,
    string DutIdentityValue,
    string? BatchId,
    string? FixtureId,
    string? DeviceId,
    string RunStatus,
    string Judgement,
    DateTimeOffset CompletedAtUtc,
    int StageCount,
    int CommandCount,
    int FailedCommandCount,
    int MeasurementCount,
    int FailedMeasurementCount,
    int ArtifactCount,
    int IncidentCount);

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
    string DutModelId,
    string DutIdentityInputKey,
    string DutIdentityValue,
    string? BatchId,
    string? FixtureId,
    string? DeviceId,
    string ActorId,
    string RunStatus,
    string Judgement,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    int StageCount,
    int FailedStageCount,
    int CommandCount,
    int FailedCommandCount,
    int MeasurementCount,
    int FailedMeasurementCount,
    int ArtifactCount,
    int IncidentCount);

public sealed record EngineeringTraceSearchFacetsResponse(
    IReadOnlyCollection<TraceFacetCountResponse> Judgements,
    IReadOnlyCollection<TraceFacetCountResponse> RunStatuses,
    IReadOnlyCollection<TraceFacetCountResponse> Stations,
    IReadOnlyCollection<TraceFacetCountResponse> Devices,
    IReadOnlyCollection<TraceFacetCountResponse> ProductionLines,
    IReadOnlyCollection<TraceFacetCountResponse> ProcessVersions,
    IReadOnlyCollection<TraceFacetCountResponse> ProjectSnapshots);

public sealed record TraceFacetCountResponse(string Value, long Count);
