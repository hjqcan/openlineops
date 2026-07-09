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
    Guid RuntimeSessionId,
    string? ProjectId,
    string? ApplicationId,
    string? ProjectSnapshotId,
    string? TopologyId,
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
    Guid RuntimeSessionId,
    string? ProjectId,
    string? ApplicationId,
    string? ProjectSnapshotId,
    string? TopologyId,
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

public sealed record EngineeringTraceSearchFacetsResponse(
    IReadOnlyCollection<TraceFacetCountResponse> Judgements,
    IReadOnlyCollection<TraceFacetCountResponse> Stations,
    IReadOnlyCollection<TraceFacetCountResponse> Devices,
    IReadOnlyCollection<TraceFacetCountResponse> ProcessVersions,
    IReadOnlyCollection<TraceFacetCountResponse> ProjectSnapshots);

public sealed record TraceFacetCountResponse(string Value, long Count);
