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
        if (!IsCanonical(query.StationId))
        {
            return Result.Failure<StationTraceDashboardReadModel>(ApplicationError.Validation(
                "Traceability.StationIdNotCanonical",
                "StationId must be a non-empty canonical string."));
        }

        var timeRangeError = ValidateTimeRange(query.CompletedFromUtc, query.CompletedToUtc);
        if (timeRangeError is not null)
        {
            return Result.Failure<StationTraceDashboardReadModel>(timeRangeError);
        }

        var result = await _repository.QueryAsync(
            new TraceRecordQuery(
                stationId: query.StationId,
                completedFromUtc: query.CompletedFromUtc,
                completedToUtc: query.CompletedToUtc,
                paging: new PagedRequest(1, TraceRecordQuery.MaxPageSize)),
            cancellationToken).ConfigureAwait(false);
        var records = result.Items.ToArray();
        var recent = records
            .OrderByDescending(record => record.CompletedAtUtc)
            .ThenByDescending(record => record.Id.Value)
            .Take(Math.Clamp(query.RecentLimit, 1, MaxRecentTraceLimit))
            .Select(record => ToStationRecentTrace(record, query.StationId!))
            .ToArray();

        return Result.Success(new StationTraceDashboardReadModel(
            query.StationId!,
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
        var pageQuery = ToTraceRecordQuery(query, query.Paging ?? new PagedRequest());
        var queryError = pageQuery.Validate();
        if (queryError is not null)
        {
            return Result.Failure<EngineeringTraceSearchReadModel>(queryError);
        }

        var pageResult = await _repository.QueryAsync(pageQuery, cancellationToken).ConfigureAwait(false);
        var facetResult = await _repository.QueryAsync(
            ToTraceRecordQuery(query, new PagedRequest(1, TraceRecordQuery.MaxPageSize)),
            cancellationToken).ConfigureAwait(false);
        return Result.Success(new EngineeringTraceSearchReadModel(
            new PagedResult<EngineeringTraceSearchRowReadModel>(
                pageResult.Items.Select(ToEngineeringSearchRow).ToArray(),
                pageResult.PageNumber,
                pageResult.PageSize,
                pageResult.TotalCount),
            CreateFacets(facetResult.Items),
            facetResult.TotalCount > facetResult.Items.Count));
    }

    private static TraceRecordQuery ToTraceRecordQuery(
        EngineeringTraceSearchQuery query,
        PagedRequest paging)
    {
        return new TraceRecordQuery(
            query.ProductionRunId,
            query.DutModelId,
            query.DutIdentityInputKey,
            query.DutIdentityValue,
            query.BatchId,
            query.FixtureId,
            query.DeviceId,
            query.ActorId,
            query.RunStatus,
            query.Judgement,
            query.ProjectId,
            query.ApplicationId,
            query.ProjectSnapshotId,
            query.TopologyId,
            query.ProductionLineDefinitionId,
            query.StageId,
            query.WorkstationId,
            query.StationId,
            query.ProcessDefinitionId,
            query.ProcessVersionId,
            query.ConfigurationSnapshotId,
            query.RecipeSnapshotId,
            query.CompletedFromUtc,
            query.CompletedToUtc,
            paging);
    }

    private static StationRecentTraceReadModel ToStationRecentTrace(TraceRecord record, string stationId)
    {
        var stages = record.Stages
            .Where(stage => string.Equals(stage.StationId.Value, stationId, StringComparison.Ordinal))
            .ToArray();
        return new StationRecentTraceReadModel(
            record.Id.Value,
            record.ProductionRunId.Value,
            record.ProjectId,
            record.ApplicationId,
            record.ProjectSnapshotId,
            record.TopologyId,
            record.ProductionLineDefinitionId,
            record.DutModelId,
            record.DutIdentityInputKey,
            record.DutIdentityValue,
            record.BatchId,
            record.FixtureId,
            record.DeviceId,
            record.RunStatus.ToString(),
            record.Judgement.ToString(),
            record.CompletedAtUtc,
            stages.Length,
            stages.Sum(stage => stage.Commands.Count),
            stages.Sum(stage => stage.Commands.Count(command => command.Status != TraceCommandStatus.Completed)),
            stages.Sum(stage => stage.Measurements.Count),
            stages.Sum(stage => stage.Measurements.Count(measurement => measurement.Passed == false)),
            stages.Sum(stage => stage.Artifacts.Count),
            stages.Sum(stage => stage.Incidents.Count));
    }

    private static EngineeringTraceSearchRowReadModel ToEngineeringSearchRow(TraceRecord record)
    {
        return new EngineeringTraceSearchRowReadModel(
            record.Id.Value,
            record.ProductionRunId.Value,
            record.ProjectId,
            record.ApplicationId,
            record.ProjectSnapshotId,
            record.TopologyId,
            record.ProductionLineDefinitionId,
            record.DutModelId,
            record.DutIdentityInputKey,
            record.DutIdentityValue,
            record.BatchId,
            record.FixtureId,
            record.DeviceId,
            record.ActorId.Value,
            record.RunStatus.ToString(),
            record.Judgement.ToString(),
            record.CreatedAtUtc,
            record.StartedAtUtc,
            record.CompletedAtUtc,
            record.Stages.Count,
            record.Stages.Count(stage => stage.Status is TraceStageStatus.Failed or TraceStageStatus.Canceled),
            record.Stages.Sum(stage => stage.Commands.Count),
            record.Stages.Sum(stage => stage.Commands.Count(command => command.Status != TraceCommandStatus.Completed)),
            record.Stages.Sum(stage => stage.Measurements.Count),
            record.Stages.Sum(stage => stage.Measurements.Count(measurement => measurement.Passed == false)),
            record.Stages.Sum(stage => stage.Artifacts.Count),
            record.Stages.Sum(stage => stage.Incidents.Count));
    }

    private static EngineeringTraceSearchFacetsReadModel CreateFacets(
        IReadOnlyCollection<TraceRecord> records)
    {
        return new EngineeringTraceSearchFacetsReadModel(
            CountBy(records.Select(record => record.Judgement.ToString())),
            CountBy(records.Select(record => record.RunStatus.ToString())),
            CountBy(records.SelectMany(record => record.Stages.Select(stage => stage.StationId.Value).Distinct(StringComparer.Ordinal))),
            CountBy(records.Select(record => record.DeviceId).Where(value => value is not null).Select(value => value!)),
            CountBy(records.Select(record => record.ProductionLineDefinitionId)),
            CountBy(records.SelectMany(record => record.Stages.Select(stage => stage.ProcessVersionId.Value).Distinct(StringComparer.Ordinal))),
            CountBy(records.Select(record => record.ProjectSnapshotId)));
    }

    private static TraceFacetCountReadModel[] CountBy(IEnumerable<string> values)
    {
        return values
            .GroupBy(value => value, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new TraceFacetCountReadModel(group.Key, group.Count()))
            .ToArray();
    }

    private static long CountJudgement(IEnumerable<TraceRecord> records, ResultJudgement judgement) =>
        records.LongCount(record => record.Judgement == judgement);

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

    private static bool IsCanonical(string? value)
    {
        return value is not null
            && !string.IsNullOrWhiteSpace(value)
            && !char.IsWhiteSpace(value[0])
            && !char.IsWhiteSpace(value[^1]);
    }
}
