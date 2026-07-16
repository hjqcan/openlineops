using System.Collections.Concurrent;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

public sealed class InMemoryProductionRunRepository :
    IProductionRunRepository,
    IProductionRunExecutionPlanRepository
{
    private readonly InMemoryProductionMaterialRepository _materials;
    private readonly ConcurrentDictionary<ProductionRunId, StoredProductionRun> _runs = [];
    private readonly ConcurrentDictionary<ProductionRunId, StoredCreatedOutboxItem> _createdOutbox = [];
    private readonly ConcurrentDictionary<ProductionRunId, StoredTerminalOutboxItem> _terminalOutbox = [];
    private int _saveCount;

    public InMemoryProductionRunRepository(InMemoryProductionMaterialRepository materials)
    {
        _materials = materials ?? throw new ArgumentNullException(nameof(materials));
    }

    public int SaveCount => Volatile.Read(ref _saveCount);

    internal object CoordinationGate => _materials.CoordinationGate;

    public ValueTask<bool> TryAddAsync(
        ProductionRun run,
        ProductionRunExecutionPlan executionPlan,
        ProductionRunAdmission admission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(executionPlan);
        ArgumentNullException.ThrowIfNull(admission);
        cancellationToken.ThrowIfCancellationRequested();
        if (run.ExecutionStatus != ExecutionStatus.Pending || executionPlan.RunId != run.Id)
        {
            throw new ArgumentException(
                "A new Production Run must be Pending and own its execution plan.",
                nameof(run));
        }

        var createdOutboxItem = ProductionRunCreatedOutboxItem.FromAdmission(run);

        bool added;
        lock (_materials.CoordinationGate)
        {
            if (_runs.ContainsKey(run.Id))
            {
                return ValueTask.FromResult(false);
            }

            if (!_materials.TryReserveProductionRun(run, admission))
            {
                return ValueTask.FromResult(false);
            }

            if (!_createdOutbox.TryAdd(
                    run.Id,
                    new StoredCreatedOutboxItem(
                        createdOutboxItem.EventId,
                        createdOutboxItem.OccurredAtUtc,
                        0,
                        null)))
            {
                throw new InvalidDataException(
                    $"Production Run {run.Id} has orphaned Created-event outbox state.");
            }

            added = _runs.TryAdd(
                run.Id,
                new StoredProductionRun(
                    ProductionRunSnapshotMapper.ToSnapshot(run),
                    executionPlan,
                    0));
            if (!added)
            {
                _createdOutbox.TryRemove(run.Id, out _);
                throw new InvalidOperationException(
                    $"Production Run {run.Id} admission lost atomic ownership.");
            }
        }
        if (added)
        {
            run.ClearDomainEvents();
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
        long nextRevision;
        lock (_materials.CoordinationGate)
        {
            if (!_runs.TryGetValue(run.Id, out var stored))
            {
                throw new InvalidOperationException(
                    $"Production run {run.Id} must be added before it can be updated.");
            }

            if (stored.Revision != expectedRevision)
            {
                throw new ProductionRunConcurrencyException(run.Id, expectedRevision);
            }

            if (ProductionRunSnapshotMapper.ToAggregate(stored.Snapshot).IsTerminal)
            {
                var candidate = ProductionRunSnapshotMapper.ToSnapshot(run);
                if (!CanonicalJsonEquals(stored.Snapshot, candidate))
                {
                    throw new InvalidOperationException(
                        $"Terminal Production Run {run.Id} is immutable.");
                }

                return ValueTask.FromResult(stored.Revision);
            }

            nextRevision = checked(expectedRevision + 1);
            var materialSnapshot = _materials.CaptureCoordinationSnapshot();
            var hadTerminalOutbox = _terminalOutbox.TryGetValue(run.Id, out var terminalOutbox);
            StoredProductionRun? updated = null;
            try
            {
                _materials.SynchronizeProductionRun(run, nextRevision);
                updated = new StoredProductionRun(
                    ProductionRunSnapshotMapper.ToSnapshot(run),
                    stored.ExecutionPlan,
                    nextRevision,
                    run.IsTerminal
                        ? new ProductionRunTerminalEvidence(
                            run.ToSnapshot(),
                            _materials.CaptureTerminalTimeline(run))
                        : null);
                if (!_runs.TryUpdate(run.Id, updated, stored))
                {
                    throw new InvalidOperationException(
                        $"Production Run {run.Id} lost atomic ownership while synchronizing its Unit.");
                }

                if (run.IsTerminal
                    && !_terminalOutbox.TryAdd(run.Id, new StoredTerminalOutboxItem(0, null)))
                {
                    throw new InvalidDataException(
                        $"Production Run {run.Id} has orphaned terminal outbox state.");
                }
            }
            catch
            {
                _materials.RestoreCoordinationSnapshot(materialSnapshot);
                if (updated is not null)
                {
                    _runs.TryUpdate(run.Id, stored, updated);
                }

                if (hadTerminalOutbox)
                {
                    _terminalOutbox[run.Id] = terminalOutbox!;
                }
                else
                {
                    _terminalOutbox.TryRemove(run.Id, out _);
                }

                throw;
            }
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

    public ValueTask<IReadOnlyCollection<ProductionRunPersistenceEntry>> ListActiveAsync(
        string? productionLineDefinitionId = null,
        string? stationSystemId = null,
        string? slotId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = _runs.Values
            .Select(stored => new ProductionRunPersistenceEntry(
                ProductionRunSnapshotMapper.ToAggregate(stored.Snapshot),
                stored.Revision))
            .Where(entry => !entry.Run.IsTerminal)
            .Where(entry => productionLineDefinitionId is null || string.Equals(
                entry.Run.ProductionLineDefinitionId,
                productionLineDefinitionId,
                StringComparison.Ordinal))
            .Where(entry => stationSystemId is null || entry.Run.OperationDefinitions.Any(definition =>
                string.Equals(definition.StationSystemId, stationSystemId, StringComparison.Ordinal)))
            .Where(entry => slotId is null || entry.Run.OperationDefinitions.Any(definition =>
                definition.ResourceRequirements.Any(requirement =>
                    requirement.Kind == ResourceKind.Slot
                    && string.Equals(requirement.ResourceId, slotId, StringComparison.Ordinal))))
            .OrderBy(entry => entry.Run.CreatedAtUtc)
            .ThenBy(entry => entry.Run.Id.Value)
            .ToArray();
        return ValueTask.FromResult<IReadOnlyCollection<ProductionRunPersistenceEntry>>(entries);
    }

    public ValueTask<ProductionRunTerminalPage> ListTerminalAsync(
        ProductionRunTerminalPageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var entries = _runs.Values
            .Select(static stored => stored.TerminalEvidence)
            .Where(static evidence => evidence is not null)
            .Cast<ProductionRunTerminalEvidence>()
            .Where(evidence => request.After is null || IsAfter(evidence.Run, request.After))
            .OrderBy(static evidence => evidence.Run.LastTransitionAtUtc)
            .ThenBy(static evidence => evidence.Run.RunId.Value)
            .Take(request.PageSize + 1)
            .ToArray();
        var hasMore = entries.Length > request.PageSize;
        var items = hasMore ? entries[..request.PageSize] : entries;
        var next = hasMore
            ? new ProductionRunTerminalCursor(
                items[^1].Run.LastTransitionAtUtc,
                items[^1].Run.RunId)
            : null;
        return ValueTask.FromResult(new ProductionRunTerminalPage(items, next));
    }

    public ValueTask<ProductionRunExecutionPlan?> GetByRunIdAsync(
        ProductionRunId runId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = _runs.TryGetValue(runId, out var stored)
            ? stored.ExecutionPlan
            : null;
        return ValueTask.FromResult(plan);
    }

    public ValueTask<IReadOnlyCollection<ProductionRunCreatedOutboxItem>>
        ListPendingCreatedOutboxAsync(
            int maximumCount,
            CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);
        cancellationToken.ThrowIfCancellationRequested();
        var items = _createdOutbox
            .OrderBy(static pair => pair.Value.OccurredAtUtc)
            .ThenBy(static pair => pair.Key.Value)
            .Take(maximumCount)
            .Select(static pair => new ProductionRunCreatedOutboxItem(
                pair.Key,
                pair.Value.EventId,
                pair.Value.OccurredAtUtc,
                pair.Value.AttemptCount,
                pair.Value.LastError))
            .ToArray();
        return ValueTask.FromResult<IReadOnlyCollection<ProductionRunCreatedOutboxItem>>(items);
    }

    public ValueTask MarkCreatedOutboxProcessedAsync(
        ProductionRunId runId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_createdOutbox.TryRemove(runId, out _))
        {
            throw new InvalidOperationException(
                $"Production Run Created-event outbox item {runId} does not exist.");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask RecordCreatedOutboxFailureAsync(
        ProductionRunId runId,
        string failureDescription,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(failureDescription);
        if (char.IsWhiteSpace(failureDescription[0])
            || char.IsWhiteSpace(failureDescription[^1]))
        {
            throw new ArgumentException(
                "Created-event failure description must be canonical.",
                nameof(failureDescription));
        }

        var boundedFailure = failureDescription.Length <= 4096
            ? failureDescription
            : failureDescription[..4096];
        while (_createdOutbox.TryGetValue(runId, out var stored))
        {
            var updated = stored with
            {
                AttemptCount = checked(stored.AttemptCount + 1),
                LastError = boundedFailure
            };
            if (_createdOutbox.TryUpdate(runId, updated, stored))
            {
                return ValueTask.CompletedTask;
            }
        }

        throw new InvalidOperationException(
            $"Production Run Created-event outbox item {runId} does not exist.");
    }

    public ValueTask<IReadOnlyCollection<ProductionRunTerminalOutboxItem>>
        ListPendingTerminalOutboxAsync(
            int maximumCount,
            CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);
        cancellationToken.ThrowIfCancellationRequested();
        var items = _terminalOutbox
            .Select(pair => new
            {
                pair.Key,
                Outbox = pair.Value,
                Evidence = _runs.TryGetValue(pair.Key, out var stored)
                    ? stored.TerminalEvidence
                    : null
            })
            .Select(item => item.Evidence is null
                ? throw new InvalidDataException(
                    $"Production Run {item.Key} terminal outbox has no immutable evidence.")
                : new { item.Key, item.Outbox, Evidence = item.Evidence })
            .OrderBy(item => item.Evidence.Run.CompletedAtUtc)
            .ThenBy(item => item.Key.Value)
            .Take(maximumCount)
            .Select(item => new ProductionRunTerminalOutboxItem(
                item.Evidence,
                item.Outbox.AttemptCount,
                item.Outbox.LastError))
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

    private sealed record StoredProductionRun(
        PersistedProductionRun Snapshot,
        ProductionRunExecutionPlan ExecutionPlan,
        long Revision,
        ProductionRunTerminalEvidence? TerminalEvidence = null);
    private sealed record StoredCreatedOutboxItem(
        Guid EventId,
        DateTimeOffset OccurredAtUtc,
        int AttemptCount,
        string? LastError);
    private sealed record StoredTerminalOutboxItem(
        int AttemptCount,
        string? LastError);

    private static bool IsAfter(
        ProductionRunSnapshot run,
        ProductionRunTerminalCursor cursor)
    {
        var timestampComparison = run.LastTransitionAtUtc.CompareTo(cursor.LastTransitionAtUtc);
        return timestampComparison > 0
            || timestampComparison == 0 && run.RunId.Value.CompareTo(cursor.RunId.Value) > 0;
    }

    private static bool CanonicalJsonEquals(
        PersistedProductionRun left,
        PersistedProductionRun right)
    {
        var leftJson = System.Text.Json.JsonSerializer.Serialize(left);
        var rightJson = System.Text.Json.JsonSerializer.Serialize(right);
        using var leftDocument = System.Text.Json.JsonDocument.Parse(leftJson);
        using var rightDocument = System.Text.Json.JsonDocument.Parse(rightJson);
        return System.Text.Json.JsonElement.DeepEquals(
            leftDocument.RootElement,
            rightDocument.RootElement);
    }
}
