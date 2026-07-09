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
    private int _saveCount;

    public int SaveCount => Volatile.Read(ref _saveCount);

    public ValueTask SaveAsync(TraceRecord traceRecord, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(traceRecord);
        cancellationToken.ThrowIfCancellationRequested();

        _records[traceRecord.Id] = traceRecord;
        Interlocked.Increment(ref _saveCount);

        return ValueTask.CompletedTask;
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
        return MatchesOptional(record.SerialNumber, query.SerialNumber)
            && MatchesOptional(record.BatchId, query.BatchId)
            && MatchesOptional(record.StationId.Value, query.StationId)
            && MatchesOptional(record.FixtureId, query.FixtureId)
            && MatchesOptional(record.ProcessDefinitionId.Value, query.ProcessDefinitionId)
            && MatchesOptional(record.ProcessVersionId.Value, query.ProcessVersionId)
            && MatchesOptional(record.ConfigurationSnapshotId.Value, query.ConfigurationSnapshotId)
            && MatchesOptional(record.RecipeSnapshotId.Value, query.RecipeSnapshotId)
            && MatchesOptional(record.DeviceId.Value, query.DeviceId)
            && MatchesOptional(record.Judgement.ToString(), query.Judgement)
            && (query.CompletedFromUtc is null || record.CompletedAtUtc >= query.CompletedFromUtc)
            && (query.CompletedToUtc is null || record.CompletedAtUtc <= query.CompletedToUtc);
    }

    private static bool MatchesOptional(string? actual, string? expected)
    {
        return expected is null
            || string.Equals(actual, expected, StringComparison.Ordinal);
    }
}
