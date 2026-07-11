using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Traceability.Domain.Identifiers;

namespace OpenLineOps.Traceability.Domain.Records;

public sealed class TraceRecord
{
    private readonly List<TraceOperationExecution> _operations;
    private readonly List<TraceRouteDecision> _routeDecisions;
    private readonly List<AuditEntry> _auditEntries;

    private TraceRecord(
        TraceRecordId id,
        ProductionRunId productionRunId,
        string projectId,
        string applicationId,
        string projectSnapshotId,
        string topologyId,
        string productionLineDefinitionId,
        string productModelId,
        string productionUnitIdentityInputKey,
        string productionUnitIdentityValue,
        string? lotId,
        string? carrierId,
        ActorId actorId,
        ExecutionStatus executionStatus,
        ResultJudgement judgement,
        ProductDisposition disposition,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset completedAtUtc,
        string? failureCode,
        string? failureReason,
        IEnumerable<TraceOperationExecution> operations,
        IEnumerable<TraceRouteDecision> routeDecisions,
        IEnumerable<AuditEntry> auditEntries)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(routeDecisions);
        ArgumentNullException.ThrowIfNull(auditEntries);
        if (id.Value != productionRunId.Value)
        {
            throw new ArgumentException("Trace Record id must equal its Production Run id.", nameof(id));
        }

        Id = id;
        ProductionRunId = productionRunId;
        ProjectId = TraceabilityIdGuard.NotBlank(projectId, nameof(projectId));
        ApplicationId = TraceabilityIdGuard.NotBlank(applicationId, nameof(applicationId));
        ProjectSnapshotId = TraceabilityIdGuard.NotBlank(projectSnapshotId, nameof(projectSnapshotId));
        TopologyId = TraceabilityIdGuard.NotBlank(topologyId, nameof(topologyId));
        ProductionLineDefinitionId = TraceabilityIdGuard.NotBlank(
            productionLineDefinitionId,
            nameof(productionLineDefinitionId));
        ProductModelId = TraceabilityIdGuard.NotBlank(productModelId, nameof(productModelId));
        ProductionUnitIdentityInputKey = TraceabilityIdGuard.NotBlank(
            productionUnitIdentityInputKey,
            nameof(productionUnitIdentityInputKey));
        ProductionUnitIdentityValue = TraceabilityIdGuard.NotBlank(
            productionUnitIdentityValue,
            nameof(productionUnitIdentityValue));
        LotId = TraceabilityIdGuard.OptionalText(lotId);
        CarrierId = TraceabilityIdGuard.OptionalText(carrierId);
        ActorId = actorId;
        ExecutionStatus = Enum.IsDefined(executionStatus)
            ? executionStatus
            : throw new ArgumentOutOfRangeException(nameof(executionStatus));
        Judgement = Enum.IsDefined(judgement)
            ? judgement
            : throw new ArgumentOutOfRangeException(nameof(judgement));
        Disposition = Enum.IsDefined(disposition)
            ? disposition
            : throw new ArgumentOutOfRangeException(nameof(disposition));
        CreatedAtUtc = RequiredTimestamp(createdAtUtc, nameof(createdAtUtc));
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = RequiredTimestamp(completedAtUtc, nameof(completedAtUtc));
        FailureCode = TraceabilityIdGuard.OptionalText(failureCode);
        FailureReason = TraceabilityIdGuard.OptionalText(failureReason);
        _operations = operations.OrderBy(operation => operation.StartedAtUtc).ThenBy(
                operation => operation.OperationRunId,
                StringComparer.Ordinal)
            .ToList();
        _routeDecisions = routeDecisions.OrderBy(decision => decision.DecidedAtUtc).ThenBy(
                decision => decision.TransitionId,
                StringComparer.Ordinal)
            .ToList();
        _auditEntries = auditEntries.OrderBy(entry => entry.OccurredAtUtc).ToList();

        if (StartedAtUtc < CreatedAtUtc || CompletedAtUtc < (StartedAtUtc ?? CreatedAtUtc))
        {
            throw new ArgumentException("Trace Record timestamps must be chronological.", nameof(completedAtUtc));
        }

        ValidateCollections();
        ValidateTerminalState();
    }

    public TraceRecordId Id { get; }
    public ProductionRunId ProductionRunId { get; }
    public string ProjectId { get; }
    public string ApplicationId { get; }
    public string ProjectSnapshotId { get; }
    public string TopologyId { get; }
    public string ProductionLineDefinitionId { get; }
    public string ProductModelId { get; }
    public string ProductionUnitIdentityInputKey { get; }
    public string ProductionUnitIdentityValue { get; }
    public string? LotId { get; }
    public string? CarrierId { get; }
    public ActorId ActorId { get; }
    public ExecutionStatus ExecutionStatus { get; }
    public ResultJudgement Judgement { get; }
    public ProductDisposition Disposition { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset? StartedAtUtc { get; }
    public DateTimeOffset CompletedAtUtc { get; }
    public string? FailureCode { get; }
    public string? FailureReason { get; }
    public IReadOnlyList<TraceOperationExecution> Operations => _operations;
    public IReadOnlyList<TraceRouteDecision> RouteDecisions => _routeDecisions;
    public IReadOnlyList<AuditEntry> AuditEntries => _auditEntries;

    public static TraceRecord Create(
        TraceRecordId id,
        ProductionRunId productionRunId,
        string projectId,
        string applicationId,
        string projectSnapshotId,
        string topologyId,
        string productionLineDefinitionId,
        string productModelId,
        string productionUnitIdentityInputKey,
        string productionUnitIdentityValue,
        string? lotId,
        string? carrierId,
        ActorId actorId,
        ExecutionStatus executionStatus,
        ResultJudgement judgement,
        ProductDisposition disposition,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset completedAtUtc,
        string? failureCode,
        string? failureReason,
        IEnumerable<TraceOperationExecution> operations,
        IEnumerable<TraceRouteDecision> routeDecisions,
        IEnumerable<AuditEntry> auditEntries) => new(
        id,
        productionRunId,
        projectId,
        applicationId,
        projectSnapshotId,
        topologyId,
        productionLineDefinitionId,
        productModelId,
        productionUnitIdentityInputKey,
        productionUnitIdentityValue,
        lotId,
        carrierId,
        actorId,
        executionStatus,
        judgement,
        disposition,
        createdAtUtc,
        startedAtUtc,
        completedAtUtc,
        failureCode,
        failureReason,
        operations,
        routeDecisions,
        auditEntries);

    public static TraceRecord Restore(
        TraceRecordId id,
        ProductionRunId productionRunId,
        string projectId,
        string applicationId,
        string projectSnapshotId,
        string topologyId,
        string productionLineDefinitionId,
        string productModelId,
        string productionUnitIdentityInputKey,
        string productionUnitIdentityValue,
        string? lotId,
        string? carrierId,
        ActorId actorId,
        ExecutionStatus executionStatus,
        ResultJudgement judgement,
        ProductDisposition disposition,
        DateTimeOffset createdAtUtc,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset completedAtUtc,
        string? failureCode,
        string? failureReason,
        IEnumerable<TraceOperationExecution> operations,
        IEnumerable<TraceRouteDecision> routeDecisions,
        IEnumerable<AuditEntry> auditEntries) => Create(
        id,
        productionRunId,
        projectId,
        applicationId,
        projectSnapshotId,
        topologyId,
        productionLineDefinitionId,
        productModelId,
        productionUnitIdentityInputKey,
        productionUnitIdentityValue,
        lotId,
        carrierId,
        actorId,
        executionStatus,
        judgement,
        disposition,
        createdAtUtc,
        startedAtUtc,
        completedAtUtc,
        failureCode,
        failureReason,
        operations,
        routeDecisions,
        auditEntries);

    private void ValidateCollections()
    {
        if (_operations.Count == 0)
        {
            if (ExecutionStatus != ExecutionStatus.Canceled || StartedAtUtc is not null)
            {
                throw new ArgumentException(
                    "Only a Production Run canceled before execution may contain no Operations.");
            }

        }

        EnsureUnique(_operations.Select(operation => operation.OperationRunId), "Operation Run ids");
        EnsureUnique(
            _operations.Select(operation => $"{operation.OperationId}@{operation.Attempt}"),
            "Operation attempts");
        EnsureUnique(_auditEntries.Select(entry => entry.Id.Value.ToString("D")), "Audit Entry ids");
        EnsureUnique(
            _routeDecisions.Select(decision =>
                $"{decision.SourceOperationRunId}/{decision.TransitionId}/{decision.Traversal}"),
            "Route Decision identities");

        var operationsByRunId = _operations.ToDictionary(
            operation => operation.OperationRunId,
            StringComparer.Ordinal);
        foreach (var decision in _routeDecisions)
        {
            if (!operationsByRunId.TryGetValue(decision.SourceOperationRunId, out var source)
                || !_operations.Any(operation => string.Equals(
                    operation.OperationId,
                    decision.TargetOperationId,
                    StringComparison.Ordinal))
                || source.ExecutionStatus != ExecutionStatus.Completed
                || source.Judgement != decision.SourceJudgement
                || decision.DecidedAtUtc < source.CompletedAtUtc)
            {
                throw new ArgumentException(
                    $"Route Decision {decision.TransitionId} differs from frozen Operation evidence.");
            }
        }
    }

    private void ValidateTerminalState()
    {
        if (ExecutionStatus is not (ExecutionStatus.Completed
            or ExecutionStatus.Failed
            or ExecutionStatus.TimedOut
            or ExecutionStatus.Canceled
            or ExecutionStatus.Rejected))
        {
            throw new ArgumentException("Production Run trace status must be terminal.");
        }

        if (_operations.Any(operation => operation.ExecutionStatus is not (
                ExecutionStatus.Completed
                or ExecutionStatus.Failed
                or ExecutionStatus.TimedOut
                or ExecutionStatus.Canceled
                or ExecutionStatus.Rejected)))
        {
            throw new ArgumentException("Terminal Production Run trace contains an open Operation.");
        }

        switch (ExecutionStatus)
        {
            case ExecutionStatus.Completed:
                if (FailureCode is not null
                    || FailureReason is not null
                    || Judgement == ResultJudgement.Unknown
                    || !CompletedDispositionMatchesJudgement())
                {
                    throw new ArgumentException("Completed Production Run trace contains invalid result evidence.");
                }

                if (StartedAtUtc is null || Judgement != AggregateJudgement())
                {
                    throw new ArgumentException(
                        "Completed Production Run trace differs from its Operation judgements.");
                }

                break;
            case ExecutionStatus.Canceled:
                if (FailureCode is null
                    || FailureReason is null
                    || Judgement != ResultJudgement.Aborted
                    || Disposition != ProductDisposition.Held)
                {
                    throw new ArgumentException("Canceled Production Run trace contains invalid result evidence.");
                }

                break;
            default:
                if (FailureCode is null
                    || FailureReason is null
                    || Judgement != ResultJudgement.Unknown
                    || Disposition != ProductDisposition.Held
                    || StartedAtUtc is null)
                {
                    throw new ArgumentException("Failed Production Run trace contains invalid result evidence.");
                }

                break;
        }
    }

    private bool CompletedDispositionMatchesJudgement()
    {
        return Judgement switch
        {
            ResultJudgement.Passed or ResultJudgement.NotApplicable =>
                Disposition == ProductDisposition.Completed,
            ResultJudgement.Failed => Disposition is ProductDisposition.Nonconforming
                or ProductDisposition.Scrapped,
            ResultJudgement.Aborted => Disposition == ProductDisposition.Held,
            _ => false
        };
    }

    private ResultJudgement AggregateJudgement()
    {
        var judgements = _operations.Select(operation => operation.Judgement).ToArray();
        if (judgements.Contains(ResultJudgement.Aborted))
        {
            return ResultJudgement.Aborted;
        }

        if (judgements.Contains(ResultJudgement.Unknown))
        {
            return ResultJudgement.Unknown;
        }

        if (judgements.Contains(ResultJudgement.Failed))
        {
            return ResultJudgement.Failed;
        }

        return judgements.Contains(ResultJudgement.Passed)
            ? ResultJudgement.Passed
            : ResultJudgement.NotApplicable;
    }

    private static DateTimeOffset RequiredTimestamp(DateTimeOffset value, string parameterName) =>
        value == default
            ? throw new ArgumentException("Timestamp is required.", parameterName)
            : value;

    private static void EnsureUnique(IEnumerable<string> values, string description)
    {
        var materialized = values.ToArray();
        if (materialized.Distinct(StringComparer.Ordinal).Count() != materialized.Length)
        {
            throw new ArgumentException($"Trace Record {description} must be unique.");
        }
    }
}
