using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Events;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Application.Persistence;

public interface IProductionRunRepository
{
    ValueTask<bool> TryAddAsync(
        ProductionRun run,
        ProductionRunExecutionPlan executionPlan,
        ProductionRunAdmission admission,
        CancellationToken cancellationToken = default);

    ValueTask<long> SaveAsync(
        ProductionRun run,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    ValueTask<ProductionRunPersistenceEntry?> GetByIdAsync(
        ProductionRunId runId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<ProductionRunPersistenceEntry>> ListRecoverableAsync(
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<ProductionRunPersistenceEntry>> ListActiveAsync(
        string? productionLineDefinitionId = null,
        string? stationSystemId = null,
        string? slotId = null,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<ProductionRunCreatedOutboxItem>> ListPendingCreatedOutboxAsync(
        int maximumCount,
        CancellationToken cancellationToken = default);

    ValueTask MarkCreatedOutboxProcessedAsync(
        ProductionRunId runId,
        CancellationToken cancellationToken = default);

    ValueTask RecordCreatedOutboxFailureAsync(
        ProductionRunId runId,
        string failureDescription,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<ProductionRunTerminalOutboxItem>> ListPendingTerminalOutboxAsync(
        int maximumCount,
        CancellationToken cancellationToken = default);

    ValueTask MarkTerminalOutboxProcessedAsync(
        ProductionRunId runId,
        CancellationToken cancellationToken = default);

    ValueTask RecordTerminalOutboxFailureAsync(
        ProductionRunId runId,
        string failureDescription,
        CancellationToken cancellationToken = default);
}

public sealed record ProductionRunAdmission
{
    public ProductionRunAdmission(ProductionUnitSnapshot productionUnit, long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(productionUnit);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        ProductionUnit = productionUnit;
        ExpectedRevision = expectedRevision;
    }

    public ProductionUnitSnapshot ProductionUnit { get; }

    public long ExpectedRevision { get; }
}

public sealed record ProductionRunCreatedOutboxItem
{
    public static ProductionRunCreatedOutboxItem FromAdmission(ProductionRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var events = run.DomainEvents.ToArray();
        if (events.Length != 1
            || events[0] is not ProductionRunCreatedDomainEvent created
            || created.RunId != run.Id)
        {
            throw new ArgumentException(
                "Production Run admission requires exactly one matching Created domain event.",
                nameof(run));
        }

        return new ProductionRunCreatedOutboxItem(
            run.Id,
            created.EventId,
            created.OccurredAtUtc,
            0,
            null);
    }

    public ProductionRunCreatedOutboxItem(
        ProductionRunId runId,
        Guid eventId,
        DateTimeOffset occurredAtUtc,
        int attemptCount,
        string? lastError)
    {
        if (runId.Value == Guid.Empty)
        {
            throw new ArgumentException("Production Run id cannot be empty.", nameof(runId));
        }

        if (eventId == Guid.Empty)
        {
            throw new ArgumentException("Created-event id cannot be empty.", nameof(eventId));
        }

        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Created-event occurrence time must be UTC.", nameof(occurredAtUtc));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(attemptCount);
        if ((attemptCount == 0) != (lastError is null)
            || lastError is not null
                && (string.IsNullOrWhiteSpace(lastError)
                    || char.IsWhiteSpace(lastError[0])
                    || char.IsWhiteSpace(lastError[^1])))
        {
            throw new ArgumentException(
                "Created-event failure state requires a canonical error after every failed attempt.",
                nameof(lastError));
        }

        RunId = runId;
        EventId = eventId;
        OccurredAtUtc = occurredAtUtc;
        AttemptCount = attemptCount;
        LastError = lastError;
    }

    public ProductionRunId RunId { get; }

    public Guid EventId { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public int AttemptCount { get; }

    public string? LastError { get; }
}

public sealed record ProductionRunTerminalOutboxItem
{
    public ProductionRunTerminalOutboxItem(
        ProductionRunSnapshot run,
        int attemptCount,
        string? lastError)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentOutOfRangeException.ThrowIfNegative(attemptCount);
        if (run.ExecutionStatus is not ExecutionStatus.Completed
            and not ExecutionStatus.Failed
            and not ExecutionStatus.TimedOut
            and not ExecutionStatus.Canceled
            and not ExecutionStatus.Rejected
            || run.CompletedAtUtc is null)
        {
            throw new ArgumentException(
                "A terminal outbox item requires a terminal Production Run snapshot.",
                nameof(run));
        }

        Run = run;
        AttemptCount = attemptCount;
        LastError = lastError;
    }

    public ProductionRunSnapshot Run { get; }

    public int AttemptCount { get; }

    public string? LastError { get; }

    public ProductionRunId RunId => Run.RunId;
}

public sealed record ProductionRunPersistenceEntry
{
    public ProductionRunPersistenceEntry(ProductionRun run, long revision)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentOutOfRangeException.ThrowIfNegative(revision);

        Run = run;
        Revision = revision;
    }

    public ProductionRun Run { get; }

    public long Revision { get; }
}

public sealed class ProductionRunConcurrencyException : InvalidOperationException
{
    public ProductionRunConcurrencyException(ProductionRunId runId, long expectedRevision)
        : base(
            $"Production run {runId} was not stored at expected revision {expectedRevision}; "
            + "the caller must reload before applying another transition.")
    {
        RunId = runId;
        ExpectedRevision = expectedRevision;
    }

    public ProductionRunId RunId { get; }

    public long ExpectedRevision { get; }
}
