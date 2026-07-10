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
            && MatchesOptional(record.DutModelId, query.DutModelId)
            && MatchesOptional(record.DutIdentityInputKey, query.DutIdentityInputKey)
            && MatchesOptional(record.DutIdentityValue, query.DutIdentityValue)
            && MatchesOptional(record.BatchId, query.BatchId)
            && MatchesOptional(record.FixtureId, query.FixtureId)
            && MatchesOptional(record.DeviceId, query.DeviceId)
            && MatchesOptional(record.ActorId.Value, query.ActorId)
            && MatchesOptional(record.RunStatus.ToString(), query.RunStatus)
            && MatchesOptional(record.Judgement.ToString(), query.Judgement)
            && MatchesOptional(record.ProjectId, query.ProjectId)
            && MatchesOptional(record.ApplicationId, query.ApplicationId)
            && MatchesOptional(record.ProjectSnapshotId, query.ProjectSnapshotId)
            && MatchesOptional(record.TopologyId, query.TopologyId)
            && MatchesOptional(record.ProductionLineDefinitionId, query.ProductionLineDefinitionId)
            && MatchesStage(record, query)
            && (query.CompletedFromUtc is null || record.CompletedAtUtc >= query.CompletedFromUtc)
            && (query.CompletedToUtc is null || record.CompletedAtUtc <= query.CompletedToUtc);
    }

    private static bool MatchesStage(TraceRecord record, TraceRecordQuery query)
    {
        var hasStageFilter = query.StageId is not null
            || query.WorkstationId is not null
            || query.StationId is not null
            || query.ProcessDefinitionId is not null
            || query.ProcessVersionId is not null
            || query.ConfigurationSnapshotId is not null
            || query.RecipeSnapshotId is not null;
        return !hasStageFilter || record.Stages.Any(stage =>
            MatchesOptional(stage.StageId, query.StageId)
            && MatchesOptional(stage.WorkstationId, query.WorkstationId)
            && MatchesOptional(stage.StationId.Value, query.StationId)
            && MatchesOptional(stage.ProcessDefinitionId.Value, query.ProcessDefinitionId)
            && MatchesOptional(stage.ProcessVersionId.Value, query.ProcessVersionId)
            && MatchesOptional(stage.ConfigurationSnapshotId.Value, query.ConfigurationSnapshotId)
            && MatchesOptional(stage.RecipeSnapshotId.Value, query.RecipeSnapshotId));
    }

    private static bool MatchesOptional(string? actual, string? expected)
    {
        return expected is null
            || string.Equals(actual, expected, StringComparison.Ordinal);
    }
}
