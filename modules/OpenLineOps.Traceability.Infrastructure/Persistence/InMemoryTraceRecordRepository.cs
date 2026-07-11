using System.Collections.Concurrent;
using OpenLineOps.Application.Abstractions.Paging;
using OpenLineOps.Traceability.Application.Persistence;
using OpenLineOps.Traceability.Application.Queries;
using OpenLineOps.Traceability.Domain.Identifiers;
using OpenLineOps.Traceability.Domain.Records;

namespace OpenLineOps.Traceability.Infrastructure.Persistence;

public sealed class InMemoryTraceRecordRepository : ITraceRecordRepository
{
    private readonly ConcurrentDictionary<TraceRecordId, TraceRecord> _records = [];
    private int _addCount;

    public int AddCount => Volatile.Read(ref _addCount);

    public ValueTask<bool> TryAddAsync(TraceRecord traceRecord, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(traceRecord);
        cancellationToken.ThrowIfCancellationRequested();

        var added = _records.TryAdd(traceRecord.Id, traceRecord);
        if (added)
        {
            Interlocked.Increment(ref _addCount);
        }

        return ValueTask.FromResult(added);
    }

    public ValueTask<TraceRecord?> GetByIdAsync(
        TraceRecordId traceRecordId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _records.TryGetValue(traceRecordId, out var traceRecord);

        return ValueTask.FromResult(traceRecord);
    }

    public ValueTask<PagedResult<TraceRecord>> QueryAsync(
        TraceRecordQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var paging = query.Paging.Normalize(TraceRecordQuery.MaxPageSize);
        var matches = _records.Values
            .Where(record => Matches(record, query))
            .OrderBy(record => record.CompletedAtUtc)
            .ThenBy(record => record.Id.Value)
            .ToArray();

        var page = matches
            .Skip(paging.Skip)
            .Take(paging.PageSize)
            .ToArray();

        var result = new PagedResult<TraceRecord>(
            page,
            paging.PageNumber,
            paging.PageSize,
            matches.Length);

        return ValueTask.FromResult(result);
    }

    private static bool Matches(TraceRecord record, TraceRecordQuery query)
    {
        return (query.ProductionRunId is null || record.ProductionRunId.Value == query.ProductionRunId)
               && (query.ProductionUnitId is null || record.ProductionUnitId.Value == query.ProductionUnitId)
            && MatchesOptional(record.ProductModelId, query.ProductModelId)
            && MatchesOptional(record.ProductionUnitIdentityInputKey, query.ProductionUnitIdentityInputKey)
            && MatchesOptional(record.ProductionUnitIdentityValue, query.ProductionUnitIdentityValue)
            && MatchesOptional(record.LotId, query.LotId)
            && MatchesOptional(record.CarrierId, query.CarrierId)
            && MatchesOptional(record.ActorId.Value, query.ActorId)
            && MatchesOptional(record.ExecutionStatus.ToString(), query.ExecutionStatus)
            && MatchesOptional(record.Judgement.ToString(), query.Judgement)
            && MatchesOptional(record.Disposition.ToString(), query.Disposition)
            && MatchesOptional(record.ProjectId, query.ProjectId)
            && MatchesOptional(record.ApplicationId, query.ApplicationId)
            && MatchesOptional(record.ProjectSnapshotId, query.ProjectSnapshotId)
            && MatchesOptional(record.TopologyId, query.TopologyId)
            && MatchesOptional(record.ProductionLineDefinitionId, query.ProductionLineDefinitionId)
            && MatchesOperation(record, query)
            && MatchesDevice(record, query.DeviceId)
            && (query.CompletedFromUtc is null || record.CompletedAtUtc >= query.CompletedFromUtc)
            && (query.CompletedToUtc is null || record.CompletedAtUtc <= query.CompletedToUtc);
    }

    private static bool MatchesOperation(TraceRecord record, TraceRecordQuery query)
    {
        var hasOperationFilter = query.OperationId is not null
            || query.StationSystemId is not null
            || query.StationId is not null
            || query.ProcessDefinitionId is not null
            || query.ProcessVersionId is not null
            || query.ConfigurationSnapshotId is not null
            || query.RecipeSnapshotId is not null
            || query.ResourceKind is not null
            || query.ResourceId is not null;
        return !hasOperationFilter || record.Operations.Any(operation =>
            MatchesOptional(operation.OperationId, query.OperationId)
            && MatchesOptional(operation.StationSystemId, query.StationSystemId)
            && MatchesOptional(operation.StationId.Value, query.StationId)
            && MatchesOptional(operation.ProcessDefinitionId.Value, query.ProcessDefinitionId)
            && MatchesOptional(operation.ProcessVersionId.Value, query.ProcessVersionId)
            && MatchesOptional(operation.ConfigurationSnapshotId.Value, query.ConfigurationSnapshotId)
            && MatchesOptional(operation.RecipeSnapshotId.Value, query.RecipeSnapshotId)
            && MatchesResource(operation, query.ResourceKind, query.ResourceId));
    }

    private static bool MatchesResource(
        TraceOperationExecution operation,
        string? resourceKind,
        string? resourceId)
    {
        return resourceKind is null && resourceId is null
            || operation.FencingTokens.Any(token =>
                MatchesOptional(token.ResourceKind, resourceKind)
                && MatchesOptional(token.ResourceId, resourceId));
    }

    private static bool MatchesDevice(TraceRecord record, string? deviceId)
    {
        return deviceId is null || record.Operations.Any(operation =>
            operation.Measurements.Any(measurement => MatchesOptional(measurement.DeviceId?.Value, deviceId))
            || operation.Artifacts.Any(artifact => MatchesOptional(artifact.DeviceId?.Value, deviceId)));
    }

    private static bool MatchesOptional(string? actual, string? expected)
    {
        return expected is null
            || string.Equals(actual, expected, StringComparison.Ordinal);
    }
}
