using System.Collections.Concurrent;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

public sealed class InMemoryProductionRunRepository : IProductionRunRepository
{
    private readonly ConcurrentDictionary<ProductionRunId, StoredProductionRun> _runs = [];
    private readonly ConcurrentDictionary<ProductionRunId, StoredTerminalOutboxItem> _terminalOutbox = [];
    private int _saveCount;

    public int SaveCount => Volatile.Read(ref _saveCount);

    public ValueTask<bool> TryAddAsync(
        ProductionRun run,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        cancellationToken.ThrowIfCancellationRequested();
        if (run.Status != ProductionRunStatus.Created)
        {
            throw new ArgumentException(
                "A new production run must be persisted in Created status.",
                nameof(run));
        }

        var added = _runs.TryAdd(
            run.Id,
            new StoredProductionRun(ProductionRunSnapshotMapper.ToSnapshot(run), 0));
        if (added)
        {
            Interlocked.Increment(ref _saveCount);
        }

        return ValueTask.FromResult(added);
    }

    public ValueTask<long> SaveAsync(
        ProductionRun run,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_runs.TryGetValue(run.Id, out var stored))
        {
            throw new InvalidOperationException(
                $"Production run {run.Id} must be added before it can be updated.");
        }

        if (stored.Revision != expectedRevision)
        {
            throw new ProductionRunConcurrencyException(run.Id, expectedRevision);
        }

        var nextRevision = checked(expectedRevision + 1);
        var updated = new StoredProductionRun(
            ProductionRunSnapshotMapper.ToSnapshot(run),
            nextRevision);
        if (!_runs.TryUpdate(run.Id, updated, stored))
        {
            throw new ProductionRunConcurrencyException(run.Id, expectedRevision);
        }

        if (run.IsTerminal)
        {
            _terminalOutbox.TryAdd(
                run.Id,
                new StoredTerminalOutboxItem(run.ToSnapshot(), 0, null));
        }

        Interlocked.Increment(ref _saveCount);
        return ValueTask.FromResult(nextRevision);
    }

    public ValueTask<ProductionRunPersistenceEntry?> GetByIdAsync(
        ProductionRunId runId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var run = _runs.TryGetValue(runId, out var stored)
            ? new ProductionRunPersistenceEntry(
                ProductionRunSnapshotMapper.ToAggregate(stored.Snapshot),
                stored.Revision)
            : null;
        return ValueTask.FromResult(run);
    }

    public ValueTask<IReadOnlyCollection<ProductionRunPersistenceEntry>> ListRecoverableAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var runs = _runs.Values
            .Select(stored => new ProductionRunPersistenceEntry(
                ProductionRunSnapshotMapper.ToAggregate(stored.Snapshot),
                stored.Revision))
            .Where(entry => !entry.Run.IsTerminal)
            .OrderBy(entry => entry.Run.LastTransitionAtUtc)
            .ThenBy(entry => entry.Run.Id.Value)
            .ToArray();
        return ValueTask.FromResult<IReadOnlyCollection<ProductionRunPersistenceEntry>>(runs);
    }

    public ValueTask<IReadOnlyCollection<ProductionRunTerminalOutboxItem>>
        ListPendingTerminalOutboxAsync(
            int maximumCount,
            CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);
        cancellationToken.ThrowIfCancellationRequested();
        var items = _terminalOutbox.Values
            .OrderBy(item => item.Run.CompletedAtUtc)
            .ThenBy(item => item.Run.RunId.Value)
            .Take(maximumCount)
            .Select(item => new ProductionRunTerminalOutboxItem(
                item.Run,
                item.AttemptCount,
                item.LastError))
            .ToArray();
        return ValueTask.FromResult<IReadOnlyCollection<ProductionRunTerminalOutboxItem>>(items);
    }

    public ValueTask MarkTerminalOutboxProcessedAsync(
        ProductionRunId runId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_terminalOutbox.TryRemove(runId, out _))
        {
            throw new InvalidOperationException(
                $"Production Run terminal outbox item {runId} does not exist.");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask RecordTerminalOutboxFailureAsync(
        ProductionRunId runId,
        string failureDescription,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(failureDescription);
        while (_terminalOutbox.TryGetValue(runId, out var stored))
        {
            var updated = stored with
            {
                AttemptCount = checked(stored.AttemptCount + 1),
                LastError = failureDescription
            };
            if (_terminalOutbox.TryUpdate(runId, updated, stored))
            {
                return ValueTask.CompletedTask;
            }
        }

        throw new InvalidOperationException(
            $"Production Run terminal outbox item {runId} does not exist.");
    }

    private sealed record StoredProductionRun(PersistedProductionRun Snapshot, long Revision);
    private sealed record StoredTerminalOutboxItem(
        ProductionRunSnapshot Run,
        int AttemptCount,
        string? LastError);
}
