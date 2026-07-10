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
    string? StationId,
    DateTimeOffset? CompletedFromUtc = null,
    DateTimeOffset? CompletedToUtc = null,
    int RecentLimit = 10);

public sealed record EngineeringTraceSearchQuery(
    Guid? ProductionRunId = null,
    string? DutModelId = null,
    string? DutIdentityInputKey = null,
    string? DutIdentityValue = null,
    string? BatchId = null,
    string? FixtureId = null,
    string? DeviceId = null,
    string? ActorId = null,
    string? RunStatus = null,
    string? Judgement = null,
    string? ProjectId = null,
    string? ApplicationId = null,
    string? ProjectSnapshotId = null,
    string? TopologyId = null,
    string? ProductionLineDefinitionId = null,
    string? StageId = null,
    string? WorkstationId = null,
    string? StationId = null,
    string? ProcessDefinitionId = null,
    string? ProcessVersionId = null,
    string? ConfigurationSnapshotId = null,
    string? RecipeSnapshotId = null,
    DateTimeOffset? CompletedFromUtc = null,
    DateTimeOffset? CompletedToUtc = null,
    PagedRequest? Paging = null);

public sealed record StationTraceDashboardReadModel(
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
    IReadOnlyCollection<StationRecentTraceReadModel> RecentTraces);

public sealed record StationRecentTraceReadModel(
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

public sealed record EngineeringTraceSearchFacetsReadModel(
    IReadOnlyCollection<TraceFacetCountReadModel> Judgements,
    IReadOnlyCollection<TraceFacetCountReadModel> RunStatuses,
    IReadOnlyCollection<TraceFacetCountReadModel> Stations,
    IReadOnlyCollection<TraceFacetCountReadModel> Devices,
    IReadOnlyCollection<TraceFacetCountReadModel> ProductionLines,
    IReadOnlyCollection<TraceFacetCountReadModel> ProcessVersions,
    IReadOnlyCollection<TraceFacetCountReadModel> ProjectSnapshots);

public sealed record TraceFacetCountReadModel(string Value, long Count);
