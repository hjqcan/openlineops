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
    string? SerialNumber = null,
    string? BatchId = null,
    string? StationId = null,
    string? FixtureId = null,
    string? ProcessDefinitionId = null,
    string? ProcessVersionId = null,
    string? ConfigurationSnapshotId = null,
    string? RecipeSnapshotId = null,
    string? DeviceId = null,
    string? Judgement = null,
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
    Guid RuntimeSessionId,
    string SerialNumber,
    string? BatchId,
    string? FixtureId,
    string ProcessVersionId,
    string DeviceId,
    string Judgement,
    DateTimeOffset CompletedAtUtc,
    int MeasurementCount,
    int FailedMeasurementCount,
    int ArtifactCount);

public sealed record EngineeringTraceSearchReadModel(
    PagedResult<EngineeringTraceSearchRowReadModel> Results,
    EngineeringTraceSearchFacetsReadModel Facets,
    bool AreFacetsTruncated);

public sealed record EngineeringTraceSearchRowReadModel(
    Guid TraceRecordId,
    Guid RuntimeSessionId,
    string SerialNumber,
    string? BatchId,
    string StationId,
    string? FixtureId,
    string ProcessDefinitionId,
    string ProcessVersionId,
    string ConfigurationSnapshotId,
    string RecipeSnapshotId,
    string DeviceId,
    string Judgement,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    int MeasurementCount,
    int FailedMeasurementCount,
    int ArtifactCount);

public sealed record EngineeringTraceSearchFacetsReadModel(
    IReadOnlyCollection<TraceFacetCountReadModel> Judgements,
    IReadOnlyCollection<TraceFacetCountReadModel> Stations,
    IReadOnlyCollection<TraceFacetCountReadModel> Devices,
    IReadOnlyCollection<TraceFacetCountReadModel> ProcessVersions);

public sealed record TraceFacetCountReadModel(string Value, long Count);
