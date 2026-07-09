using OpenLineOps.Application.Abstractions.Paging;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Traceability.Application.Persistence;
using OpenLineOps.Traceability.Application.Queries;
using OpenLineOps.Traceability.Domain.Records;

namespace OpenLineOps.Traceability.Application.ReadModels;

public sealed class TraceReadModelService : ITraceReadModelService
{
    private const int MaxRecentTraceLimit = 50;

    private readonly ITraceRecordRepository _repository;

    public TraceReadModelService(ITraceRecordRepository repository)
    {
        _repository = repository;
    }

    public async Task<Result<StationTraceDashboardReadModel>> GetStationDashboardAsync(
        StationTraceDashboardQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (string.IsNullOrWhiteSpace(query.StationId))
        {
            return Result.Failure<StationTraceDashboardReadModel>(ApplicationError.Validation(
                "Traceability.StationIdRequired",
                "StationId is required."));
        }

        var timeRangeValidation = ValidateTimeRange(query.CompletedFromUtc, query.CompletedToUtc);
        if (timeRangeValidation is not null)
        {
            return Result.Failure<StationTraceDashboardReadModel>(timeRangeValidation);
        }

        var normalizedStationId = query.StationId.Trim();
        var recentLimit = Math.Clamp(query.RecentLimit, 1, MaxRecentTraceLimit);
        var result = await _repository
            .QueryAsync(
                new TraceRecordQuery(
                    stationId: normalizedStationId,
                    completedFromUtc: query.CompletedFromUtc,
                    completedToUtc: query.CompletedToUtc,
                    paging: new PagedRequest(1, TraceRecordQuery.MaxPageSize)),
                cancellationToken)
            .ConfigureAwait(false);
        var records = result.Items.ToArray();
        var recent = records
            .OrderByDescending(record => record.CompletedAtUtc)
            .ThenByDescending(record => record.Id.Value)
            .Take(recentLimit)
            .Select(ToStationRecentTrace)
            .ToArray();

        return Result.Success(new StationTraceDashboardReadModel(
            normalizedStationId,
            query.CompletedFromUtc,
            query.CompletedToUtc,
            result.TotalCount,
            CountJudgement(records, ResultJudgement.Passed),
            CountJudgement(records, ResultJudgement.Failed),
            CountJudgement(records, ResultJudgement.Aborted),
            CountJudgement(records, ResultJudgement.Unknown),
            records.Length == 0 ? null : records.Min(record => record.CompletedAtUtc),
            records.Length == 0 ? null : records.Max(record => record.CompletedAtUtc),
            result.TotalCount > records.Length,
            recent));
    }

    public async Task<Result<EngineeringTraceSearchReadModel>> SearchForEngineeringAsync(
        EngineeringTraceSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var timeRangeValidation = ValidateTimeRange(query.CompletedFromUtc, query.CompletedToUtc);
        if (timeRangeValidation is not null)
        {
            return Result.Failure<EngineeringTraceSearchReadModel>(timeRangeValidation);
        }

        var pageResult = await _repository
            .QueryAsync(ToTraceRecordQuery(query, query.Paging ?? new PagedRequest()), cancellationToken)
            .ConfigureAwait(false);
        var facetResult = await _repository
            .QueryAsync(ToTraceRecordQuery(query, new PagedRequest(1, TraceRecordQuery.MaxPageSize)), cancellationToken)
            .ConfigureAwait(false);

        var rows = pageResult.Items
            .Select(ToEngineeringSearchRow)
            .ToArray();
        var facets = CreateFacets(facetResult.Items);

        return Result.Success(new EngineeringTraceSearchReadModel(
            new PagedResult<EngineeringTraceSearchRowReadModel>(
                rows,
                pageResult.PageNumber,
                pageResult.PageSize,
                pageResult.TotalCount),
            facets,
            facetResult.TotalCount > facetResult.Items.Count));
    }

    private static TraceRecordQuery ToTraceRecordQuery(
        EngineeringTraceSearchQuery query,
        PagedRequest paging)
    {
        return new TraceRecordQuery(
            serialNumber: query.SerialNumber,
            batchId: query.BatchId,
            stationId: query.StationId,
            fixtureId: query.FixtureId,
            completedFromUtc: query.CompletedFromUtc,
            completedToUtc: query.CompletedToUtc,
            paging: paging,
            processDefinitionId: query.ProcessDefinitionId,
            processVersionId: query.ProcessVersionId,
            configurationSnapshotId: query.ConfigurationSnapshotId,
            recipeSnapshotId: query.RecipeSnapshotId,
            deviceId: query.DeviceId,
            judgement: query.Judgement,
            projectId: query.ProjectId,
            applicationId: query.ApplicationId,
            projectSnapshotId: query.ProjectSnapshotId,
            topologyId: query.TopologyId);
    }

    private static StationRecentTraceReadModel ToStationRecentTrace(TraceRecord traceRecord)
    {
        return new StationRecentTraceReadModel(
            traceRecord.Id.Value,
            traceRecord.RuntimeSessionId.Value,
            traceRecord.ProjectId,
            traceRecord.ApplicationId,
            traceRecord.ProjectSnapshotId,
            traceRecord.TopologyId,
            traceRecord.SerialNumber,
            traceRecord.BatchId,
            traceRecord.FixtureId,
            traceRecord.ProcessVersionId.Value,
            traceRecord.DeviceId.Value,
            traceRecord.Judgement.ToString(),
            traceRecord.CompletedAtUtc,
            traceRecord.Measurements.Count,
            traceRecord.Measurements.Count(measurement => measurement.Passed == false),
            traceRecord.Artifacts.Count);
    }

    private static EngineeringTraceSearchRowReadModel ToEngineeringSearchRow(TraceRecord traceRecord)
    {
        return new EngineeringTraceSearchRowReadModel(
            traceRecord.Id.Value,
            traceRecord.RuntimeSessionId.Value,
            traceRecord.ProjectId,
            traceRecord.ApplicationId,
            traceRecord.ProjectSnapshotId,
            traceRecord.TopologyId,
            traceRecord.SerialNumber,
            traceRecord.BatchId,
            traceRecord.StationId.Value,
            traceRecord.FixtureId,
            traceRecord.ProcessDefinitionId.Value,
            traceRecord.ProcessVersionId.Value,
            traceRecord.ConfigurationSnapshotId.Value,
            traceRecord.RecipeSnapshotId.Value,
            traceRecord.DeviceId.Value,
            traceRecord.Judgement.ToString(),
            traceRecord.StartedAtUtc,
            traceRecord.CompletedAtUtc,
            traceRecord.Measurements.Count,
            traceRecord.Measurements.Count(measurement => measurement.Passed == false),
            traceRecord.Artifacts.Count);
    }

    private static EngineeringTraceSearchFacetsReadModel CreateFacets(
        IReadOnlyCollection<TraceRecord> traceRecords)
    {
        return new EngineeringTraceSearchFacetsReadModel(
            CountBy(traceRecords, record => record.Judgement.ToString()),
            CountBy(traceRecords, record => record.StationId.Value),
            CountBy(traceRecords, record => record.DeviceId.Value),
            CountBy(traceRecords, record => record.ProcessVersionId.Value),
            CountByOptional(traceRecords, record => record.ProjectSnapshotId));
    }

    private static TraceFacetCountReadModel[] CountBy(
        IEnumerable<TraceRecord> traceRecords,
        Func<TraceRecord, string> selector)
    {
        return traceRecords
            .GroupBy(selector, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new TraceFacetCountReadModel(group.Key, group.Count()))
            .ToArray();
    }

    private static TraceFacetCountReadModel[] CountByOptional(
        IEnumerable<TraceRecord> traceRecords,
        Func<TraceRecord, string?> selector)
    {
        return traceRecords
            .Select(selector)
            .Where(value => value is not null)
            .Select(value => value!)
            .GroupBy(value => value, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new TraceFacetCountReadModel(group.Key, group.Count()))
            .ToArray();
    }

    private static long CountJudgement(
        IEnumerable<TraceRecord> traceRecords,
        ResultJudgement judgement)
    {
        return traceRecords.LongCount(traceRecord => traceRecord.Judgement == judgement);
    }

    private static ApplicationError? ValidateTimeRange(
        DateTimeOffset? completedFromUtc,
        DateTimeOffset? completedToUtc)
    {
        return completedFromUtc is not null
            && completedToUtc is not null
            && completedToUtc < completedFromUtc
            ? ApplicationError.Validation(
                "Traceability.InvalidTimeRange",
                "CompletedToUtc cannot be earlier than CompletedFromUtc.")
            : null;
    }
}
