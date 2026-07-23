using OpenLineOps.Runtime.Application.Materials;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Events;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Materials;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Domain.Resources;
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

    ValueTask<ProductionRunTerminalPage> ListTerminalAsync(
        ProductionRunTerminalPageRequest request,
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

public sealed record ProductionRunTerminalPageRequest
{
    public const int MaximumPageSize = 1000;

    public ProductionRunTerminalPageRequest(
        int pageSize,
        ProductionRunTerminalCursor? after = null)
    {
        if (pageSize is < 1 or > MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageSize),
                pageSize,
                $"Terminal Production Run page size must be between 1 and {MaximumPageSize}.");
        }

        PageSize = pageSize;
        After = after;
    }

    public int PageSize { get; }

    public ProductionRunTerminalCursor? After { get; }
}

public sealed record ProductionRunTerminalCursor
{
    public ProductionRunTerminalCursor(
        DateTimeOffset lastTransitionAtUtc,
        ProductionRunId runId)
    {
        if (lastTransitionAtUtc == default || lastTransitionAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Terminal Production Run cursor timestamp must be a non-default UTC value.",
                nameof(lastTransitionAtUtc));
        }

        if (runId.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "Terminal Production Run cursor id cannot be empty.",
                nameof(runId));
        }

        LastTransitionAtUtc = lastTransitionAtUtc;
        RunId = runId;
    }

    public DateTimeOffset LastTransitionAtUtc { get; }

    public ProductionRunId RunId { get; }
}

public sealed record ProductionRunTerminalPage
{
    public ProductionRunTerminalPage(
        IReadOnlyList<ProductionRunTerminalEvidence> items,
        ProductionRunTerminalCursor? next)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Any(static item => item is null))
        {
            throw new ArgumentException(
                "A terminal Production Run page cannot contain null evidence.",
                nameof(items));
        }

        Items = items.ToArray();
        Next = next;
    }

    public IReadOnlyList<ProductionRunTerminalEvidence> Items { get; }

    public ProductionRunTerminalCursor? Next { get; }
}

public sealed record ProductionRunTerminalEvidence
{
    public ProductionRunTerminalEvidence(
        ProductionRunSnapshot run,
        IReadOnlyCollection<ProductionMaterialTimelineEntry> materialTimeline)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(materialTimeline);
        if (run.ExecutionStatus is not ExecutionStatus.Completed
            and not ExecutionStatus.Failed
            and not ExecutionStatus.TimedOut
            and not ExecutionStatus.Canceled
            and not ExecutionStatus.Rejected
            || run.CompletedAtUtc is null)
        {
            throw new ArgumentException(
                "Terminal evidence requires a terminal Production Run snapshot.",
                nameof(run));
        }

        var query = ProductionMaterialTimelineQuery.UnionScope(
            new ProductionUnitId(run.ProductionUnitId.Value),
            run.RunId,
            run.CarrierId is null ? null : new CarrierId(run.CarrierId),
            run.CompletedAtUtc.Value);
        var canonicalTimeline = materialTimeline
            .OrderBy(static entry => entry.OccurredAtUtc)
            .ThenBy(static entry => entry.EvidenceId)
            .ToArray();
        if (canonicalTimeline.Any(static entry => entry is null)
            || canonicalTimeline.Select(static entry => entry.EvidenceId).Distinct().Count()
                != canonicalTimeline.Length
            || canonicalTimeline.Any(entry => !query.Matches(entry)))
        {
            throw new ArgumentException(
                "Terminal material evidence must be unique and belong to the exact frozen Run scope.",
                nameof(materialTimeline));
        }

        foreach (var operation in run.Operations)
        {
            var executionEvidence = operation.ExecutionEvidence;
            if (operation.RecoveryDecisionId is { } recoveryDecisionId
                    && run.RecoveryDecisions.Any(decision =>
                        decision.DecisionId == recoveryDecisionId)
                || operation.ExecutionStatus == ExecutionStatus.Canceled
                    && operation.RuntimeSessionId is null
                    && executionEvidence is null)
            {
                continue;
            }

            if (executionEvidence is null
                || executionEvidence.ProductionRunId != run.RunId.Value
                || executionEvidence.ProductionUnitId != run.ProductionUnitId.Value
                || executionEvidence.RuntimeSessionId != operation.RuntimeSessionId?.Value
                || !string.Equals(
                    executionEvidence.ProductionLineDefinitionId,
                    run.ProductionLineDefinitionId,
                    StringComparison.Ordinal)
                || !string.Equals(executionEvidence.OperationId, operation.Definition.OperationId, StringComparison.Ordinal)
                || !string.Equals(executionEvidence.OperationRunId, operation.OperationRunId, StringComparison.Ordinal)
                || executionEvidence.OperationAttempt != operation.Attempt
                || !string.Equals(executionEvidence.StationSystemId, operation.Definition.StationSystemId, StringComparison.Ordinal)
                || !string.Equals(executionEvidence.StationId, operation.Definition.StationId.Value, StringComparison.Ordinal)
                || !string.Equals(executionEvidence.ProcessDefinitionId, operation.Definition.ProcessDefinitionId.Value, StringComparison.Ordinal)
                || !string.Equals(executionEvidence.ProcessVersionId, operation.Definition.ProcessVersionId.Value, StringComparison.Ordinal)
                || !string.Equals(executionEvidence.ConfigurationSnapshotId, operation.Definition.ConfigurationSnapshotId.Value, StringComparison.Ordinal)
                || !string.Equals(executionEvidence.RecipeSnapshotId, operation.Definition.RecipeSnapshotId.Value, StringComparison.Ordinal)
                || !string.Equals(executionEvidence.ProductModelId, run.ProductionUnitIdentity.ModelId, StringComparison.Ordinal)
                || !string.Equals(executionEvidence.IdentityInputKey, run.ProductionUnitIdentity.InputKey, StringComparison.Ordinal)
                || !string.Equals(executionEvidence.IdentityValue, run.ProductionUnitIdentity.Value, StringComparison.Ordinal)
                || !string.Equals(executionEvidence.LotId, run.LotId, StringComparison.Ordinal)
                || !string.Equals(executionEvidence.CarrierId, run.CarrierId, StringComparison.Ordinal)
                || !string.Equals(executionEvidence.ActorId, run.ActorId, StringComparison.Ordinal)
                || !string.Equals(executionEvidence.ProjectId, run.ProjectId, StringComparison.Ordinal)
                || !string.Equals(executionEvidence.ApplicationId, run.ApplicationId, StringComparison.Ordinal)
                || !string.Equals(executionEvidence.ProjectSnapshotId, run.ProjectSnapshotId, StringComparison.Ordinal)
                || !string.Equals(executionEvidence.TopologyId, run.TopologyId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Operation Run {operation.OperationRunId} has no exact frozen execution evidence.",
                    nameof(run));
            }


            var expectedRuntimeStatus = operation.ExecutionStatus switch
            {
                ExecutionStatus.Completed => "Completed",
                ExecutionStatus.Canceled => executionEvidence.RuntimeSessionStatus is "Canceled" or "Stopped"
                    ? executionEvidence.RuntimeSessionStatus
                    : null,
                ExecutionStatus.Failed or ExecutionStatus.TimedOut or ExecutionStatus.Rejected => "Failed",
                _ => null
            };
            var exactFences = operation.FencingTokens
                .OrderBy(
                    static pair => $"{pair.Key.Kind}:{pair.Key.ResourceId}",
                    StringComparer.Ordinal)
                .Select(static pair => (pair.Key.Kind.ToString(), pair.Key.ResourceId, pair.Value))
                .ToArray();
            var evidenceFences = executionEvidence.ResourceFences
                .OrderBy(static fence => $"{fence.ResourceKind}:{fence.ResourceId}", StringComparer.Ordinal)
                .Select(static fence => (fence.ResourceKind, fence.ResourceId, fence.FencingToken))
                .ToArray();
            if (expectedRuntimeStatus is null
                || !string.Equals(
                    executionEvidence.RuntimeSessionStatus,
                    expectedRuntimeStatus,
                    StringComparison.Ordinal)
                || !exactFences.SequenceEqual(evidenceFences)
                || !string.Equals(
                    executionEvidence.FixtureId,
                    FindResource(operation, ResourceKind.Fixture),
                    StringComparison.Ordinal)
                || !string.Equals(
                    executionEvidence.DeviceId,
                    FindResource(operation, ResourceKind.Device),
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Operation Run {operation.OperationRunId} frozen execution evidence is not canonical.",
                    nameof(run));
            }
        }

        Run = run;
        MaterialTimeline = canonicalTimeline;
    }

    public ProductionRunSnapshot Run { get; }

    public IReadOnlyList<ProductionMaterialTimelineEntry> MaterialTimeline { get; }

    public ProductionRunId RunId => Run.RunId;

    private static string? FindResource(OperationRunSnapshot operation, ResourceKind kind) =>
        operation.Definition.ResourceRequirements
            .FirstOrDefault(requirement => requirement.Kind == kind)?.ResourceId;
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
        ProductionRunTerminalEvidence evidence,
        int attemptCount,
        string? lastError)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentOutOfRangeException.ThrowIfNegative(attemptCount);
        if ((attemptCount == 0) != (lastError is null)
            || lastError is not null
                && (string.IsNullOrWhiteSpace(lastError)
                    || char.IsWhiteSpace(lastError[0])
                    || char.IsWhiteSpace(lastError[^1])))
        {
            throw new ArgumentException(
                "Terminal outbox failure state requires canonical evidence after every failed attempt.",
                nameof(lastError));
        }

        Evidence = evidence;
        AttemptCount = attemptCount;
        LastError = lastError;
    }

    public ProductionRunTerminalEvidence Evidence { get; }

    public int AttemptCount { get; }

    public string? LastError { get; }

    public ProductionRunId RunId => Evidence.RunId;
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
