using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Application.Persistence;

public interface IProductionRunRepository
{
    ValueTask<bool> TryAddAsync(
        ProductionRun run,
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

public sealed record ProductionRunTerminalOutboxItem
{
    public ProductionRunTerminalOutboxItem(
        ProductionRunSnapshot run,
        int attemptCount,
        string? lastError)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentOutOfRangeException.ThrowIfNegative(attemptCount);
        if (run.Status is not ProductionRunStatus.Completed
            and not ProductionRunStatus.Failed
            and not ProductionRunStatus.Canceled
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
