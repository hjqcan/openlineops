using OpenLineOps.Application.Abstractions.Paging;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Runtime.Contracts;
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
        if (!IsCanonical(query.StationSystemId))
        {
            return Result.Failure<StationTraceDashboardReadModel>(ApplicationError.Validation(
                "Traceability.StationSystemIdNotCanonical",
                "StationSystemId must be a non-empty canonical string."));
        }

        var timeRangeError = ValidateTimeRange(query.CompletedFromUtc, query.CompletedToUtc);
        if (timeRangeError is not null)
        {
            return Result.Failure<StationTraceDashboardReadModel>(timeRangeError);
        }

        var result = await _repository.QueryAsync(
            new TraceRecordQuery(
                stationSystemId: query.StationSystemId,
                completedFromUtc: query.CompletedFromUtc,
                completedToUtc: query.CompletedToUtc,
                paging: new PagedRequest(1, TraceRecordQuery.MaxPageSize)),
            cancellationToken).ConfigureAwait(false);
        var records = result.Items.ToArray();
        var recent = records
            .OrderByDescending(record => record.CompletedAtUtc)
            .ThenByDescending(record => record.Id.Value)
            .Take(Math.Clamp(query.RecentLimit, 1, MaxRecentTraceLimit))
            .Select(record => ToStationRecentTrace(record, query.StationSystemId!))
            .ToArray();

        return Result.Success(new StationTraceDashboardReadModel(
            query.StationSystemId!,
            query.CompletedFromUtc,
            query.CompletedToUtc,
            result.TotalCount,
            CountJudgement(records, ResultJudgement.Passed),
            CountJudgement(records, ResultJudgement.Failed),
            CountJudgement(records, ResultJudgement.Aborted),
            CountJudgement(records, ResultJudgement.Unknown),
            CountJudgement(records, ResultJudgement.NotApplicable),
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
            null,
            query.ProductModelId,
            query.ProductionUnitIdentityInputKey,
            query.ProductionUnitIdentityValue,
            query.LotId,
            query.CarrierId,
            query.ActorId,
            query.ExecutionStatus,
            query.Judgement,
            query.Disposition,
            query.ProjectId,
            query.ApplicationId,
            query.ProjectSnapshotId,
            query.TopologyId,
            query.ProductionLineDefinitionId,
            query.OperationId,
            query.StationSystemId,
            query.StationId,
            query.ProcessDefinitionId,
            query.ProcessVersionId,
            query.ConfigurationSnapshotId,
            query.RecipeSnapshotId,
            query.ResourceKind,
            query.ResourceId,
            query.DeviceId,
            query.CompletedFromUtc,
            query.CompletedToUtc,
            paging);
    }

    private static StationRecentTraceReadModel ToStationRecentTrace(TraceRecord record, string stationSystemId)
    {
        var operations = record.Operations
            .Where(operation => string.Equals(
                operation.StationSystemId,
                stationSystemId,
                StringComparison.Ordinal))
            .ToArray();
        return new StationRecentTraceReadModel(
            record.Id.Value,
            record.ProductionRunId.Value,
            record.ProjectId,
            record.ApplicationId,
            record.ProjectSnapshotId,
            record.TopologyId,
            record.ProductionLineDefinitionId,
            record.ProductModelId,
            record.ProductionUnitIdentityInputKey,
            record.ProductionUnitIdentityValue,
            record.LotId,
            record.CarrierId,
            record.ExecutionStatus.ToString(),
            record.Judgement.ToString(),
            record.Disposition.ToString(),
            record.CompletedAtUtc,
            operations.Length,
            operations.Sum(operation => operation.Commands.Count),
            operations.Sum(operation => operation.Commands.Count(IsExecutionFailure)),
            operations.Sum(operation => operation.Measurements.Count),
            operations.Sum(operation => operation.Measurements.Count(measurement => measurement.Passed == false)),
            operations.Sum(operation => operation.Artifacts.Count),
            operations.Sum(operation => operation.Incidents.Count),
            record.Genealogy.Count,
            record.MaterialLocationTransitions.Count(transition =>
                string.Equals(
                    transition.Destination.StationSystemId,
                    stationSystemId,
                    StringComparison.Ordinal)
                || string.Equals(
                    transition.Source?.StationSystemId,
                    stationSystemId,
                    StringComparison.Ordinal)),
            record.SlotOccupancyTransitions.Count(transition =>
                string.Equals(
                    transition.StationSystemId,
                    stationSystemId,
                    StringComparison.Ordinal)),
            record.DispositionTransitions.Count);
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
            record.ProductModelId,
            record.ProductionUnitIdentityInputKey,
            record.ProductionUnitIdentityValue,
            record.LotId,
            record.CarrierId,
            record.ActorId.Value,
            record.ExecutionStatus.ToString(),
            record.Judgement.ToString(),
            record.Disposition.ToString(),
            record.CreatedAtUtc,
            record.StartedAtUtc,
            record.CompletedAtUtc,
            record.Operations.Count,
            record.Operations.Count(operation => operation.ExecutionStatus != ExecutionStatus.Completed),
            record.Operations.Sum(operation => operation.Commands.Count),
            record.Operations.Sum(operation => operation.Commands.Count(IsExecutionFailure)),
            record.Operations.Sum(operation => operation.Measurements.Count),
            record.Operations.Sum(operation => operation.Measurements.Count(measurement => measurement.Passed == false)),
            record.Operations.Sum(operation => operation.Artifacts.Count),
            record.Operations.Sum(operation => operation.Incidents.Count),
            record.RouteDecisions.Count,
            record.Genealogy.Count,
            record.MaterialLocationTransitions.Count,
            record.SlotOccupancyTransitions.Count,
            record.DispositionTransitions.Count);
    }

    private static EngineeringTraceSearchFacetsReadModel CreateFacets(
        IReadOnlyCollection<TraceRecord> records)
    {
        return new EngineeringTraceSearchFacetsReadModel(
            CountBy(records.Select(record => record.Judgement.ToString())),
            CountBy(records.Select(record => record.ExecutionStatus.ToString())),
            CountBy(records.Select(record => record.Disposition.ToString())),
            CountBy(records.SelectMany(record => record.Operations
                .Select(operation => operation.StationSystemId)
                .Distinct(StringComparer.Ordinal))),
            CountBy(records.SelectMany(DeviceIds)),
            CountBy(records.Select(record => record.ProductionLineDefinitionId)),
            CountBy(records.SelectMany(record => record.Operations
                .Select(operation => operation.ProcessVersionId.Value)
                .Distinct(StringComparer.Ordinal))),
            CountBy(records.Select(record => record.ProjectSnapshotId)));
    }

    private static IEnumerable<string> DeviceIds(TraceRecord record)
    {
        return record.Operations
            .SelectMany(operation => operation.Measurements.Select(measurement => measurement.DeviceId?.Value)
                .Concat(operation.Artifacts.Select(artifact => artifact.DeviceId?.Value)))
            .Where(value => value is not null)
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal);
    }

    private static bool IsExecutionFailure(TraceCommandRecord command) =>
        command.Status != TraceCommandStatus.Completed;

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
