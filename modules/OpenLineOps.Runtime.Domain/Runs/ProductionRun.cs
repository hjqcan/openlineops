using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Events;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Operations;
using OpenLineOps.Runtime.Domain.ProductionUnits;
using OpenLineOps.Runtime.Domain.Resources;

namespace OpenLineOps.Runtime.Domain.Runs;

public sealed class ProductionRun : AggregateRoot<ProductionRunId>
{
    private readonly List<OperationRunDefinition> _operationDefinitions;
    private readonly List<RouteTransitionDefinition> _routeTransitions;
    private readonly List<OperationRun> _operations = [];
    private readonly List<RouteDecisionSnapshot> _routeDecisions = [];
    private readonly List<ProductionRecoveryDecision> _recoveryDecisions = [];
    private readonly Dictionary<string, int> _transitionTraversals = new(StringComparer.Ordinal);

    private ProductionRun(
        ProductionRunId id,
        string projectId,
        string applicationId,
        string projectSnapshotId,
        string topologyId,
        string productionLineDefinitionId,
        ProductionUnitId productionUnitId,
        ProductionUnitIdentity productionUnitIdentity,
        string? lotId,
        string? carrierId,
        string actorId,
        string entryOperationId,
        IEnumerable<OperationRunDefinition> operationDefinitions,
        IEnumerable<RouteTransitionDefinition> routeTransitions,
        DateTimeOffset createdAtUtc)
        : base(id)
    {
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException("Production Run id cannot be empty.", nameof(id));
        }

        ProjectId = ProductionRunText.Required(projectId, nameof(projectId));
        ApplicationId = ProductionRunText.Required(applicationId, nameof(applicationId));
        ProjectSnapshotId = ProductionRunText.Required(projectSnapshotId, nameof(projectSnapshotId));
        TopologyId = ProductionRunText.Required(topologyId, nameof(topologyId));
        ProductionLineDefinitionId = ProductionRunText.Required(
            productionLineDefinitionId,
            nameof(productionLineDefinitionId));
        ProductionUnitId = productionUnitId.Value == Guid.Empty
            ? throw new ArgumentException("Production Unit id cannot be empty.", nameof(productionUnitId))
            : productionUnitId;
        ProductionUnitIdentity = productionUnitIdentity
            ?? throw new ArgumentNullException(nameof(productionUnitIdentity));
        LotId = ProductionRunText.Optional(lotId, nameof(lotId));
        CarrierId = ProductionRunText.Optional(carrierId, nameof(carrierId));
        ActorId = ProductionRunText.Required(actorId, nameof(actorId));
        EntryOperationId = ProductionRunText.Required(entryOperationId, nameof(entryOperationId));
        _operationDefinitions = operationDefinitions?.ToList()
            ?? throw new ArgumentNullException(nameof(operationDefinitions));
        _routeTransitions = routeTransitions?.ToList()
            ?? throw new ArgumentNullException(nameof(routeTransitions));
        ValidateDefinitionGraph();
        RequireUtc(createdAtUtc, nameof(createdAtUtc));
        CreatedAtUtc = createdAtUtc;
        LastTransitionAtUtc = createdAtUtc;
        ExecutionStatus = ExecutionStatus.Pending;
        Judgement = ResultJudgement.Unknown;
        Disposition = ProductDisposition.InProcess;
        ControlState = ProductionRunControlState.Active;
    }

    public string ProjectId { get; }

    public string ApplicationId { get; }

    public string ProjectSnapshotId { get; }

    public string TopologyId { get; }

    public string ProductionLineDefinitionId { get; }

    public ProductionUnitId ProductionUnitId { get; }

    public ProductionUnitIdentity ProductionUnitIdentity { get; }

    public string? LotId { get; }

    public string? CarrierId { get; }

    public string ActorId { get; }

    public string EntryOperationId { get; }

    public ExecutionStatus ExecutionStatus { get; private set; }

    public ResultJudgement Judgement { get; private set; }

    public ProductDisposition Disposition { get; private set; }

    public ProductionRunControlState ControlState { get; private set; }

    public string? SafeStopRequestedBy { get; private set; }

    public string? SafeStopReason { get; private set; }

    public DateTimeOffset? SafeStopRequestedAtUtc { get; private set; }

    public DateTimeOffset? SafeStopAcknowledgedAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset LastTransitionAtUtc { get; private set; }

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public string? FailureCode { get; private set; }

    public string? FailureReason { get; private set; }

    public IReadOnlyList<OperationRunDefinition> OperationDefinitions =>
        _operationDefinitions.AsReadOnly();

    public IReadOnlyList<RouteTransitionDefinition> RouteTransitions =>
        _routeTransitions.AsReadOnly();

    public IReadOnlyList<OperationRun> Operations => _operations.AsReadOnly();

    public IReadOnlyList<RouteDecisionSnapshot> RouteDecisions => _routeDecisions.AsReadOnly();

    public IReadOnlyList<ProductionRecoveryDecision> RecoveryDecisions =>
        _recoveryDecisions.AsReadOnly();

    public bool IsTerminal => ExecutionStatus is ExecutionStatus.Completed
        or ExecutionStatus.Failed
        or ExecutionStatus.TimedOut
        or ExecutionStatus.Canceled
        or ExecutionStatus.Rejected;

    public static ProductionRun Create(
        ProductionRunId id,
        string projectId,
        string applicationId,
        string projectSnapshotId,
        string topologyId,
        string productionLineDefinitionId,
        ProductionUnitId productionUnitId,
        ProductionUnitIdentity productionUnitIdentity,
        string? lotId,
        string? carrierId,
        string actorId,
        string entryOperationId,
        DateTimeOffset createdAtUtc,
        IEnumerable<OperationRunDefinition> operationDefinitions,
        IEnumerable<RouteTransitionDefinition> routeTransitions)
    {
        var run = new ProductionRun(
            id,
            projectId,
            applicationId,
            projectSnapshotId,
            topologyId,
            productionLineDefinitionId,
            productionUnitId,
            productionUnitIdentity,
            lotId,
            carrierId,
            actorId,
            entryOperationId,
            operationDefinitions,
            routeTransitions,
            createdAtUtc);
        run.RaiseDomainEvent(new ProductionRunCreatedDomainEvent(run.Id)
        {
            OccurredAtUtc = createdAtUtc
        });
        return run;
    }

    public static ProductionRun Restore(ProductionRunSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var run = new ProductionRun(
            snapshot.RunId,
            snapshot.ProjectId,
            snapshot.ApplicationId,
            snapshot.ProjectSnapshotId,
            snapshot.TopologyId,
            snapshot.ProductionLineDefinitionId,
            snapshot.ProductionUnitId,
            snapshot.ProductionUnitIdentity,
            snapshot.LotId,
            snapshot.CarrierId,
            snapshot.ActorId,
            snapshot.EntryOperationId,
            snapshot.OperationDefinitions,
            snapshot.RouteTransitions,
            snapshot.CreatedAtUtc)
        {
            ExecutionStatus = snapshot.ExecutionStatus,
            Judgement = snapshot.Judgement,
            Disposition = snapshot.Disposition,
            ControlState = snapshot.ControlState,
            SafeStopRequestedBy = ProductionRunText.Optional(
                snapshot.SafeStopRequestedBy,
                nameof(snapshot.SafeStopRequestedBy)),
            SafeStopReason = ProductionRunText.Optional(
                snapshot.SafeStopReason,
                nameof(snapshot.SafeStopReason)),
            SafeStopRequestedAtUtc = snapshot.SafeStopRequestedAtUtc,
            SafeStopAcknowledgedAtUtc = snapshot.SafeStopAcknowledgedAtUtc,
            LastTransitionAtUtc = snapshot.LastTransitionAtUtc,
            StartedAtUtc = snapshot.StartedAtUtc,
            CompletedAtUtc = snapshot.CompletedAtUtc,
            FailureCode = ProductionRunText.Optional(snapshot.FailureCode, nameof(snapshot.FailureCode)),
            FailureReason = ProductionRunText.Optional(snapshot.FailureReason, nameof(snapshot.FailureReason))
        };
        run._operations.AddRange(snapshot.Operations.Select(OperationRun.Restore));
        run._routeDecisions.AddRange(snapshot.RouteDecisions);
        run._recoveryDecisions.AddRange(snapshot.RecoveryDecisions);
        foreach (var traversal in snapshot.TransitionTraversals)
        {
            run._transitionTraversals.Add(traversal.Key, traversal.Value);
        }

        run.ValidateState();
        run.ClearDomainEvents();
        return run;
    }

    public RuntimeOperationResult Start(DateTimeOffset startedAtUtc)
    {
        if (ExecutionStatus != ExecutionStatus.Pending)
        {
            return Reject("Runtime.ProductionRunStartRejected", "start");
        }

        RequireUtc(startedAtUtc, nameof(startedAtUtc));
        var from = ExecutionStatus;
        ExecutionStatus = ExecutionStatus.Running;
        StartedAtUtc = startedAtUtc;
        LastTransitionAtUtc = startedAtUtc;
        ActivateOperation(EntryOperationId);
        RaiseStatusChanged(from, "Production Run started.");
        return RuntimeOperationResult.Accepted();
    }

    public RuntimeOperationResult StartOperation(
        string operationRunId,
        RuntimeSessionId runtimeSessionId,
        IReadOnlyCollection<ResourceLease> leases,
        DateTimeOffset startedAtUtc)
    {
        if (ExecutionStatus != ExecutionStatus.Running
            || ControlState != ProductionRunControlState.Active)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.ProductionRunNotDispatchable",
                $"Production Run {Id} cannot dispatch operations while {ExecutionStatus}/{ControlState}.");
        }

        var operation = FindOperationRun(operationRunId);
        if (operation is null)
        {
            return OperationNotFound(operationRunId);
        }

        if (leases.Any(lease => lease.ProductionRunId != Id
            || !string.Equals(lease.OperationRunId, operationRunId, StringComparison.Ordinal)))
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.OperationResourceLeaseOwnerMismatch",
                $"Every resource lease must belong to Operation Run {operationRunId}.");
        }

        var from = operation.ExecutionStatus;
        var result = operation.Start(runtimeSessionId, leases, startedAtUtc);
        if (result.Succeeded)
        {
            LastTransitionAtUtc = startedAtUtc;
            RaiseOperationStatusChanged(operation, from, "Operation execution started.");
        }

        return result;
    }

    public RuntimeOperationResult CompleteOperation(
        string operationRunId,
        ResultJudgement judgement,
        IReadOnlyDictionary<string, ProductionContextValue>? outputs,
        int completedStepCount,
        int commandCount,
        int incidentCount,
        DateTimeOffset completedAtUtc)
    {
        var operation = FindOperationRun(operationRunId);
        if (operation is null)
        {
            return OperationNotFound(operationRunId);
        }

        var from = operation.ExecutionStatus;
        var result = operation.Complete(
            judgement,
            outputs,
            completedStepCount,
            commandCount,
            incidentCount,
            completedAtUtc);
        if (!result.Succeeded)
        {
            return result;
        }

        LastTransitionAtUtc = completedAtUtc;
        RaiseOperationStatusChanged(operation, from, "Operation execution completed.");
        if (ControlState == ProductionRunControlState.StopRequested)
        {
            TryFinishRequestedStop(completedAtUtc);
            return result;
        }

        ApplyRoute(operation, completedAtUtc);
        TryCompleteRun(completedAtUtc);
        return result;
    }

    public RuntimeOperationResult FailOperation(
        string operationRunId,
        ExecutionStatus terminalStatus,
        string code,
        string reason,
        int completedStepCount,
        int commandCount,
        int incidentCount,
        DateTimeOffset failedAtUtc)
    {
        var operation = FindOperationRun(operationRunId);
        if (operation is null)
        {
            return OperationNotFound(operationRunId);
        }

        var from = operation.ExecutionStatus;
        var result = operation.Fail(
            terminalStatus,
            code,
            reason,
            completedStepCount,
            commandCount,
            incidentCount,
            failedAtUtc);
        if (!result.Succeeded)
        {
            return result;
        }

        LastTransitionAtUtc = failedAtUtc;
        RaiseOperationStatusChanged(operation, from, reason);
        CancelOpenOperations(reason, failedAtUtc);
        TransitionToTerminal(
            terminalStatus,
            ResultJudgement.Unknown,
            ProductDisposition.Held,
            failedAtUtc,
            code,
            reason);
        return result;
    }

    public RuntimeOperationResult CancelOperation(
        string operationRunId,
        string code,
        string reason,
        int completedStepCount,
        int commandCount,
        int incidentCount,
        DateTimeOffset canceledAtUtc)
    {
        if (IsTerminal
            || ControlState is not (ProductionRunControlState.Active
                or ProductionRunControlState.StopRequested))
        {
            return Reject("Runtime.OperationCancelRejected", "capture operation cancellation");
        }

        RequireUtc(canceledAtUtc, nameof(canceledAtUtc));
        var operation = FindOperationRun(operationRunId);
        if (operation is null)
        {
            return OperationNotFound(operationRunId);
        }

        var from = operation.ExecutionStatus;
        var result = operation.CancelAfterExecution(
            code,
            reason,
            completedStepCount,
            commandCount,
            incidentCount,
            canceledAtUtc);
        if (!result.Succeeded)
        {
            return result;
        }

        LastTransitionAtUtc = canceledAtUtc > LastTransitionAtUtc
            ? canceledAtUtc
            : LastTransitionAtUtc;
        RaiseOperationStatusChanged(operation, from, reason);
        var safeStopIsInFlight = ControlState == ProductionRunControlState.StopRequested
            && SafeStopRequestedAtUtc is not null
            && string.Equals(
                FailureCode,
                "Runtime.ProductionRunSafeStopRequested",
                StringComparison.Ordinal);
        if (!safeStopIsInFlight)
        {
            FailureCode = "Runtime.ProductionRunCancelRequested";
            FailureReason = ProductionRunText.Required(reason, nameof(reason));
            ControlState = ProductionRunControlState.StopRequested;
        }

        Disposition = ProductDisposition.Held;
        foreach (var pending in _operations.Where(candidate =>
                     candidate.ExecutionStatus == ExecutionStatus.Pending))
        {
            var pendingFrom = pending.ExecutionStatus;
            pending.Cancel(reason, LastTransitionAtUtc);
            RaiseOperationStatusChanged(pending, pendingFrom, reason);
        }

        TryFinishRequestedStop(LastTransitionAtUtc);
        return result;
    }

    public RuntimeOperationResult Pause(DateTimeOffset pausedAtUtc)
    {
        if (ExecutionStatus != ExecutionStatus.Running
            || ControlState != ProductionRunControlState.Active)
        {
            return Reject("Runtime.ProductionRunPauseRejected", "pause");
        }

        ControlState = ProductionRunControlState.Paused;
        LastTransitionAtUtc = pausedAtUtc;
        return RuntimeOperationResult.Accepted();
    }

    public RuntimeOperationResult Continue(DateTimeOffset continuedAtUtc)
    {
        if (ExecutionStatus != ExecutionStatus.Running
            || ControlState != ProductionRunControlState.Paused)
        {
            return Reject("Runtime.ProductionRunContinueRejected", "continue");
        }

        ControlState = ProductionRunControlState.Active;
        LastTransitionAtUtc = continuedAtUtc;
        return RuntimeOperationResult.Accepted();
    }

    public RuntimeOperationResult Hold(string reason, DateTimeOffset heldAtUtc)
    {
        if (ExecutionStatus != ExecutionStatus.Running || ControlState == ProductionRunControlState.Held)
        {
            return Reject("Runtime.ProductionRunHoldRejected", "hold");
        }

        _ = ProductionRunText.Required(reason, nameof(reason));
        ControlState = ProductionRunControlState.Held;
        Disposition = ProductDisposition.Held;
        LastTransitionAtUtc = heldAtUtc;
        return RuntimeOperationResult.Accepted();
    }

    public RuntimeOperationResult Release(DateTimeOffset releasedAtUtc)
    {
        if (ExecutionStatus != ExecutionStatus.Running
            || ControlState != ProductionRunControlState.Held)
        {
            return Reject("Runtime.ProductionRunReleaseRejected", "release");
        }

        ControlState = ProductionRunControlState.Active;
        Disposition = Judgement == ResultJudgement.Failed
            ? ProductDisposition.Nonconforming
            : ProductDisposition.InProcess;
        LastTransitionAtUtc = releasedAtUtc;
        return RuntimeOperationResult.Accepted();
    }

    public RuntimeOperationResult Rework(string operationId, DateTimeOffset requestedAtUtc)
    {
        if (ExecutionStatus != ExecutionStatus.Running
            || ControlState != ProductionRunControlState.Held
            || _operations.Any(operation => operation.ExecutionStatus == ExecutionStatus.Running))
        {
            return Reject("Runtime.ProductionRunReworkRejected", "rework");
        }

        if (_operationDefinitions.All(definition =>
                !string.Equals(definition.OperationId, operationId, StringComparison.Ordinal)))
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.OperationNotFound",
                $"Operation {operationId} is not part of Production Run {Id}.");
        }

        ActivateOperation(operationId);
        ControlState = ProductionRunControlState.Active;
        Disposition = ProductDisposition.InProcess;
        LastTransitionAtUtc = requestedAtUtc;
        return RuntimeOperationResult.Accepted();
    }

    public RuntimeOperationResult MarkRecoveryRequired(string reason, DateTimeOffset detectedAtUtc)
    {
        if (detectedAtUtc == default || detectedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Recovery detection timestamp must be non-default UTC.",
                nameof(detectedAtUtc));
        }

        if (ExecutionStatus != ExecutionStatus.Running)
        {
            return Reject("Runtime.ProductionRunRecoveryRejected", "enter recovery");
        }

        if (detectedAtUtc < LastTransitionAtUtc)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.RecoveryDetectionPredatesRun",
                "Recovery detection cannot precede the Production Run's latest transition.");
        }

        FailureCode = "Runtime.RecoveryRequired";
        FailureReason = ProductionRunText.Required(reason, nameof(reason));
        ControlState = ProductionRunControlState.RecoveryRequired;
        Disposition = ProductDisposition.Held;
        LastTransitionAtUtc = detectedAtUtc;
        return RuntimeOperationResult.Accepted();
    }

    public RuntimeOperationResult ReconcileRecovery(ProductionRecoveryDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var duplicate = FindRecoveryDecision(decision);
        if (duplicate is not null)
        {
            return duplicate;
        }

        if (ExecutionStatus != ExecutionStatus.Running
            || ControlState != ProductionRunControlState.RecoveryRequired
            || decision.Kind != ProductionRecoveryDecisionKind.Reconcile)
        {
            return Reject("Runtime.ProductionRunReconcileRejected", "reconcile recovery");
        }

        var timestamp = ValidateRecoveryDecisionTimestamp(decision);
        if (timestamp is not null)
        {
            return timestamp;
        }

        var operation = FindOperationRun(decision.OperationRunId!);
        if (operation is null)
        {
            return OperationNotFound(decision.OperationRunId!);
        }

        if (operation.ExecutionStatus != ExecutionStatus.Running)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.RecoveryOperationNotRunning",
                $"Operation Run {operation.OperationRunId} is {operation.ExecutionStatus}, not an interrupted running execution.");
        }

        var from = operation.ExecutionStatus;
        var result = operation.Complete(
            decision.ObservedJudgement!.Value,
            decision.ObservedOutputs,
            operation.CompletedStepCount,
            operation.CommandCount,
            operation.IncidentCount,
            decision.DecidedAtUtc);
        if (!result.Succeeded)
        {
            return result;
        }

        _recoveryDecisions.Add(decision);
        LastTransitionAtUtc = decision.DecidedAtUtc;
        RaiseOperationStatusChanged(
            operation,
            from,
            $"Operator reconciled interrupted execution from evidence {decision.EvidenceReference}.");
        ApplyRoute(operation, decision.DecidedAtUtc);
        if (_operations.All(candidate => candidate.ExecutionStatus != ExecutionStatus.Running))
        {
            FailureCode = null;
            FailureReason = null;
            ControlState = ProductionRunControlState.Active;
            Disposition = ProductDisposition.InProcess;
        }

        RaiseDomainEvent(new ProductionRecoveryDecisionRecordedDomainEvent(Id, decision));
        TryCompleteRun(decision.DecidedAtUtc);
        return result;
    }

    public RuntimeOperationResult RetryRecovery(ProductionRecoveryDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var duplicate = FindRecoveryDecision(decision);
        if (duplicate is not null)
        {
            return duplicate;
        }

        if (ExecutionStatus != ExecutionStatus.Running
            || ControlState != ProductionRunControlState.RecoveryRequired
            || decision.Kind != ProductionRecoveryDecisionKind.Retry)
        {
            return Reject("Runtime.ProductionRunRetryRejected", "retry recovery");
        }

        var timestamp = ValidateRecoveryDecisionTimestamp(decision);
        if (timestamp is not null)
        {
            return timestamp;
        }

        if (_operationDefinitions.All(definition =>
                !string.Equals(definition.OperationId, decision.OperationId, StringComparison.Ordinal)))
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.OperationNotFound",
                $"Operation {decision.OperationId} is not part of Production Run {Id}.");
        }

        if (_operations.All(operation => operation.IsTerminal
            || !string.Equals(operation.OperationId, decision.OperationId, StringComparison.Ordinal)))
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.RecoveryOperationNotOpen",
                $"Operation {decision.OperationId} is not an interrupted open Operation in Production Run {Id}.");
        }

        CancelOpenOperations(
            $"Operator explicitly closed interrupted execution before retry: {decision.Reason}",
            decision.DecidedAtUtc);
        ActivateOperation(decision.OperationId!);
        _recoveryDecisions.Add(decision);
        FailureCode = null;
        FailureReason = null;
        ControlState = ProductionRunControlState.Active;
        Disposition = ProductDisposition.InProcess;
        LastTransitionAtUtc = decision.DecidedAtUtc;
        RaiseDomainEvent(new ProductionRecoveryDecisionRecordedDomainEvent(Id, decision));
        return RuntimeOperationResult.Accepted();
    }

    public RuntimeOperationResult AbortRecovery(ProductionRecoveryDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var duplicate = FindRecoveryDecision(decision);
        if (duplicate is not null)
        {
            return duplicate;
        }

        if (ExecutionStatus != ExecutionStatus.Running
            || ControlState != ProductionRunControlState.RecoveryRequired
            || decision.Kind != ProductionRecoveryDecisionKind.Abort)
        {
            return Reject("Runtime.ProductionRunAbortRejected", "abort recovery");
        }

        var timestamp = ValidateRecoveryDecisionTimestamp(decision);
        if (timestamp is not null)
        {
            return timestamp;
        }

        _recoveryDecisions.Add(decision);
        RaiseDomainEvent(new ProductionRecoveryDecisionRecordedDomainEvent(Id, decision));
        return Cancel($"Recovery aborted by {decision.ActorId}: {decision.Reason}", decision.DecidedAtUtc);
    }

    public RuntimeOperationResult ScrapRecovery(ProductionRecoveryDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var duplicate = FindRecoveryDecision(decision);
        if (duplicate is not null)
        {
            return duplicate;
        }

        if (ExecutionStatus != ExecutionStatus.Running
            || ControlState != ProductionRunControlState.RecoveryRequired
            || decision.Kind != ProductionRecoveryDecisionKind.Scrap)
        {
            return Reject("Runtime.ProductionRunRecoveryScrapRejected", "scrap recovery");
        }

        var timestamp = ValidateRecoveryDecisionTimestamp(decision);
        if (timestamp is not null)
        {
            return timestamp;
        }

        _recoveryDecisions.Add(decision);
        RaiseDomainEvent(new ProductionRecoveryDecisionRecordedDomainEvent(Id, decision));
        return Scrap(decision.Reason, decision.DecidedAtUtc);
    }

    public RuntimeOperationResult RequestStop(string reason, DateTimeOffset requestedAtUtc)
    {
        if (IsTerminal)
        {
            return Reject("Runtime.ProductionRunStopRejected", "request stop");
        }

        if (ControlState == ProductionRunControlState.RecoveryRequired)
        {
            return Reject("Runtime.ProductionRunStopRejected", "request stop during recovery");
        }

        _ = ProductionRunText.Required(reason, nameof(reason));
        RequireUtc(requestedAtUtc, nameof(requestedAtUtc));
        if (ControlState == ProductionRunControlState.StopRequested)
        {
            return string.Equals(FailureReason, reason, StringComparison.Ordinal)
                ? RuntimeOperationResult.Accepted()
                : RuntimeOperationResult.Rejected(
                    "Runtime.ProductionRunStopEvidenceMismatch",
                    $"Production Run {Id} already has a different Stop request.");
        }

        FailureCode = "Runtime.ProductionRunStopRequested";
        FailureReason = reason;
        ControlState = ProductionRunControlState.StopRequested;
        Disposition = ProductDisposition.Held;
        LastTransitionAtUtc = requestedAtUtc;
        foreach (var operation in _operations.Where(operation =>
                     operation.ExecutionStatus == ExecutionStatus.Pending))
        {
            var from = operation.ExecutionStatus;
            operation.Cancel(reason, requestedAtUtc);
            RaiseOperationStatusChanged(operation, from, reason);
        }

        TryFinishRequestedStop(requestedAtUtc);
        return RuntimeOperationResult.Accepted();
    }

    public RuntimeOperationResult RequestSafeStop(
        string actorId,
        string reason,
        DateTimeOffset requestedAtUtc)
    {
        if (IsTerminal)
        {
            return Reject("Runtime.ProductionRunSafeStopRejected", "request safe-stop");
        }

        if (ControlState == ProductionRunControlState.RecoveryRequired)
        {
            return Reject(
                "Runtime.ProductionRunSafeStopRejected",
                "request safe-stop during recovery");
        }

        actorId = ProductionRunText.Required(actorId, nameof(actorId));
        _ = ProductionRunText.Required(reason, nameof(reason));
        RequireUtc(requestedAtUtc, nameof(requestedAtUtc));
        if (SafeStopRequestedAtUtc is not null)
        {
            return ControlState == ProductionRunControlState.StopRequested
                && string.Equals(
                    FailureCode,
                    "Runtime.ProductionRunSafeStopRequested",
                    StringComparison.Ordinal)
                && string.Equals(SafeStopRequestedBy, actorId, StringComparison.Ordinal)
                && string.Equals(SafeStopReason, reason, StringComparison.Ordinal)
                    ? RuntimeOperationResult.Accepted()
                    : RuntimeOperationResult.Rejected(
                        "Runtime.ProductionRunSafeStopEvidenceMismatch",
                        $"Production Run {Id} already has different Safe Stop evidence.");
        }

        FailureCode = "Runtime.ProductionRunSafeStopRequested";
        FailureReason = reason;
        ControlState = ProductionRunControlState.StopRequested;
        Disposition = ProductDisposition.Held;
        SafeStopRequestedBy = actorId;
        SafeStopReason = reason;
        SafeStopRequestedAtUtc = requestedAtUtc;
        LastTransitionAtUtc = requestedAtUtc;
        foreach (var operation in _operations.Where(operation =>
                     operation.ExecutionStatus == ExecutionStatus.Pending))
        {
            var from = operation.ExecutionStatus;
            operation.Cancel(reason, requestedAtUtc);
            RaiseOperationStatusChanged(operation, from, reason);
        }

        if (_operations.All(operation => operation.StartedAtUtc is null))
        {
            // No hardware command has crossed the dispatch boundary. The durable barrier is
            // itself a complete no-op Safe Stop and must not invoke a physical actuator.
            SafeStopAcknowledgedAtUtc = requestedAtUtc;
        }

        TryFinishRequestedStop(requestedAtUtc);
        return RuntimeOperationResult.Accepted();
    }

    public RuntimeOperationResult Cancel(string reason, DateTimeOffset canceledAtUtc)
    {
        if (IsTerminal)
        {
            return Reject("Runtime.ProductionRunCancelRejected", "cancel");
        }

        CancelOpenOperations(reason, canceledAtUtc);
        TransitionToTerminal(
            ExecutionStatus.Canceled,
            ResultJudgement.Aborted,
            ProductDisposition.Held,
            canceledAtUtc,
            "Runtime.ProductionRunCanceled",
            reason);
        return RuntimeOperationResult.Accepted();
    }

    public RuntimeOperationResult AcknowledgeSafeStop(DateTimeOffset acknowledgedAtUtc)
    {
        if (IsTerminal
            || ControlState != ProductionRunControlState.StopRequested
            || SafeStopRequestedAtUtc is null
            || !string.Equals(
                FailureCode,
                "Runtime.ProductionRunSafeStopRequested",
                StringComparison.Ordinal))
        {
            return Reject("Runtime.ProductionRunSafeStopAcknowledgementRejected", "acknowledge safe-stop");
        }

        RequireUtc(acknowledgedAtUtc, nameof(acknowledgedAtUtc));
        if (acknowledgedAtUtc < SafeStopRequestedAtUtc)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.ProductionRunSafeStopAcknowledgementPredatesRequest",
                $"Production Run {Id} cannot acknowledge Safe Stop before it was requested.");
        }

        if (SafeStopAcknowledgedAtUtc is not null)
        {
            return SafeStopAcknowledgedAtUtc == acknowledgedAtUtc
                ? RuntimeOperationResult.Accepted()
                : RuntimeOperationResult.Rejected(
                    "Runtime.ProductionRunSafeStopAcknowledgementMismatch",
                    $"Production Run {Id} already has different Safe Stop acknowledgement evidence.");
        }

        SafeStopAcknowledgedAtUtc = acknowledgedAtUtc;
        LastTransitionAtUtc = acknowledgedAtUtc > LastTransitionAtUtc
            ? acknowledgedAtUtc
            : LastTransitionAtUtc;
        TryFinishRequestedStop(LastTransitionAtUtc);
        return RuntimeOperationResult.Accepted();
    }

    public RuntimeOperationResult Scrap(string reason, DateTimeOffset scrappedAtUtc)
    {
        if (IsTerminal)
        {
            return Reject("Runtime.ProductionRunScrapRejected", "scrap");
        }

        CancelOpenOperations(reason, scrappedAtUtc);
        TransitionToTerminal(
            ExecutionStatus.Completed,
            ResultJudgement.Failed,
            ProductDisposition.Scrapped,
            scrappedAtUtc,
            null,
            null);
        return RuntimeOperationResult.Accepted();
    }

    public ProductionRunSnapshot ToSnapshot() => new(
        Id,
        ProjectId,
        ApplicationId,
        ProjectSnapshotId,
        TopologyId,
        ProductionLineDefinitionId,
        ProductionUnitId,
        ProductionUnitIdentity,
        LotId,
        CarrierId,
        ActorId,
        ExecutionStatus,
        Judgement,
        Disposition,
        ControlState,
        SafeStopRequestedBy,
        SafeStopReason,
        SafeStopRequestedAtUtc,
        SafeStopAcknowledgedAtUtc,
        CreatedAtUtc,
        LastTransitionAtUtc,
        StartedAtUtc,
        CompletedAtUtc,
        FailureCode,
        FailureReason,
        EntryOperationId,
        _operationDefinitions.ToArray(),
        _routeTransitions.ToArray(),
        _operations.Select(static operation => operation.ToSnapshot()).ToArray(),
        _routeDecisions.ToArray(),
        new Dictionary<string, int>(_transitionTraversals, StringComparer.Ordinal),
        _recoveryDecisions.ToArray());

    private void ApplyRoute(OperationRun source, DateTimeOffset decidedAtUtc)
    {
        var outgoing = _routeTransitions
            .Where(transition => string.Equals(
                transition.SourceOperationId,
                source.OperationId,
                StringComparison.Ordinal))
            .ToArray();
        if (outgoing.Length == 0)
        {
            FailClosedRoute(
                source,
                decidedAtUtc,
                $"Operation {source.OperationId} has no explicit outgoing route.");
            return;
        }

        var joins = outgoing
            .Where(transition => transition.Kind == RuntimeRouteTransitionKind.ParallelJoin)
            .GroupBy(transition => transition.ParallelGroupId!, StringComparer.Ordinal)
            .ToArray();
        if (joins.Length > 0)
        {
            foreach (var join in joins)
            {
                ProcessParallelJoin(source, join.Key, decidedAtUtc);
            }

            return;
        }

        var forks = outgoing
            .Where(transition => transition.Kind == RuntimeRouteTransitionKind.ParallelFork)
            .ToArray();
        if (forks.Length > 0)
        {
            foreach (var fork in forks)
            {
                TakeTransition(source, fork, decidedAtUtc, activateTarget: true);
            }

            return;
        }

        var rework = outgoing.SingleOrDefault(transition =>
            transition.Kind == RuntimeRouteTransitionKind.Rework
            && transition.RequiredJudgement == source.Judgement
            && CanTraverse(transition));
        if (rework is not null)
        {
            TakeTransition(source, rework, decidedAtUtc, activateTarget: true);
            return;
        }

        var judgement = outgoing.SingleOrDefault(transition =>
            transition.Kind == RuntimeRouteTransitionKind.Judgement
            && transition.RequiredJudgement == source.Judgement);
        if (judgement is not null)
        {
            TakeTransition(source, judgement, decidedAtUtc, activateTarget: true);
            return;
        }

        var outputCondition = outgoing
            .Where(transition => transition.Kind == RuntimeRouteTransitionKind.Condition
                && transition.OutputCondition!.Matches(source.Outputs))
            .ToArray();
        if (outputCondition.Length > 1)
        {
            throw new InvalidOperationException(
                $"Operation {source.OperationId} matched multiple typed output transitions.");
        }

        if (outputCondition.Length == 1)
        {
            TakeTransition(source, outputCondition[0], decidedAtUtc, activateTarget: true);
            return;
        }

        var sequence = outgoing.SingleOrDefault(transition =>
            transition.Kind == RuntimeRouteTransitionKind.Sequence);
        if (sequence is not null)
        {
            TakeTransition(source, sequence, decidedAtUtc, activateTarget: true);
            return;
        }

        FailClosedRoute(
            source,
            decidedAtUtc,
            $"Operation {source.OperationId} has no route matching judgement {source.Judgement} and its typed outputs.");
    }

    private void ProcessParallelJoin(
        OperationRun source,
        string parallelGroupId,
        DateTimeOffset decidedAtUtc)
    {
        var joins = _routeTransitions.Where(transition =>
            transition.Kind == RuntimeRouteTransitionKind.ParallelJoin
            && string.Equals(transition.ParallelGroupId, parallelGroupId, StringComparison.Ordinal))
            .ToArray();
        var sourceJoin = joins.Single(transition => string.Equals(
            transition.SourceOperationId,
            source.OperationId,
            StringComparison.Ordinal));
        TakeTransition(source, sourceJoin, decidedAtUtc, activateTarget: false);

        var completedWaveCount = joins
            .Select(join => _operations.Count(operation =>
                string.Equals(operation.OperationId, join.SourceOperationId, StringComparison.Ordinal)
                && operation.ExecutionStatus == ExecutionStatus.Completed))
            .Min();
        var targetOperationId = sourceJoin.TargetOperationId!;
        var targetActivationCount = _operations.Count(operation => string.Equals(
            operation.OperationId,
            targetOperationId,
            StringComparison.Ordinal));
        if (completedWaveCount > targetActivationCount)
        {
            ActivateOperation(targetOperationId);
        }
    }

    private void TakeTransition(
        OperationRun source,
        RouteTransitionDefinition transition,
        DateTimeOffset decidedAtUtc,
        bool activateTarget)
    {
        if (_routeDecisions.Any(decision =>
                string.Equals(decision.SourceOperationRunId, source.OperationRunId, StringComparison.Ordinal)
                && string.Equals(decision.TransitionId, transition.TransitionId, StringComparison.Ordinal)))
        {
            return;
        }

        var traversal = _transitionTraversals.GetValueOrDefault(transition.TransitionId) + 1;
        _transitionTraversals[transition.TransitionId] = traversal;
        _routeDecisions.Add(new RouteDecisionSnapshot(
            source.OperationRunId,
            transition.TransitionId,
            transition.TargetOperationId,
            transition.TerminalDisposition,
            source.Judgement,
            traversal,
            decidedAtUtc));
        if (activateTarget && transition.TargetOperationId is not null)
        {
            ActivateOperation(transition.TargetOperationId);
        }
    }

    private bool CanTraverse(RouteTransitionDefinition transition) =>
        transition.MaxTraversals is null
        || _transitionTraversals.GetValueOrDefault(transition.TransitionId)
            < transition.MaxTraversals.Value;

    private OperationRun ActivateOperation(string operationId)
    {
        var definition = _operationDefinitions.Single(candidate => string.Equals(
            candidate.OperationId,
            operationId,
            StringComparison.Ordinal));
        var attempt = _operations.Count(operation => string.Equals(
            operation.OperationId,
            operationId,
            StringComparison.Ordinal)) + 1;
        var operation = OperationRun.Create(definition, attempt);
        _operations.Add(operation);
        return operation;
    }

    private void TryCompleteRun(DateTimeOffset completedAtUtc)
    {
        if (_operations.Any(operation => !operation.IsTerminal))
        {
            return;
        }

        var terminals = _routeDecisions
            .Where(decision => decision.TerminalDisposition is not null)
            .ToArray();
        if (terminals.Length != 1)
        {
            FailClosedRoute(
                null,
                completedAtUtc,
                terminals.Length == 0
                    ? "Production Run completed its Operations without selecting an explicit terminal disposition."
                    : "Production Run selected more than one terminal disposition.");
            return;
        }

        var judgement = AggregateJudgement();
        TransitionToTerminal(
            ExecutionStatus.Completed,
            judgement,
            terminals[0].TerminalDisposition!.Value,
            completedAtUtc,
            null,
            null);
    }

    private void FailClosedRoute(
        OperationRun? source,
        DateTimeOffset failedAtUtc,
        string reason)
    {
        if (IsTerminal)
        {
            return;
        }

        CancelOpenOperations(reason, failedAtUtc);
        TransitionToTerminal(
            ExecutionStatus.Failed,
            ResultJudgement.Unknown,
            ProductDisposition.Held,
            failedAtUtc,
            "Runtime.RouteResolutionFailed",
            source is null
                ? reason
                : $"Route resolution failed after Operation Run {source.OperationRunId}: {reason}");
    }

    private void TryFinishRequestedStop(DateTimeOffset stoppedAtUtc)
    {
        if (ControlState != ProductionRunControlState.StopRequested
            || _operations.Any(operation => !operation.IsTerminal))
        {
            return;
        }

        var isSafeStop = string.Equals(
            FailureCode,
            "Runtime.ProductionRunSafeStopRequested",
            StringComparison.Ordinal);
        if (isSafeStop && SafeStopAcknowledgedAtUtc is null)
        {
            return;
        }

        if (isSafeStop)
        {
            ControlState = ProductionRunControlState.SafeStopped;
        }

        TransitionToTerminal(
            ExecutionStatus.Canceled,
            ResultJudgement.Aborted,
            ProductDisposition.Held,
            stoppedAtUtc,
            isSafeStop
                ? "Runtime.ProductionRunSafeStopped"
                : string.Equals(
                    FailureCode,
                    "Runtime.ProductionRunCancelRequested",
                    StringComparison.Ordinal)
                    ? "Runtime.ProductionRunCanceled"
                    : "Runtime.ProductionRunStopped",
            FailureReason ?? "Production Run stopped at an operation boundary.",
            preserveControlState: isSafeStop);
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

    private void TransitionToTerminal(
        ExecutionStatus status,
        ResultJudgement judgement,
        ProductDisposition disposition,
        DateTimeOffset completedAtUtc,
        string? failureCode,
        string? failureReason,
        bool preserveControlState = false)
    {
        var from = ExecutionStatus;
        ExecutionStatus = status;
        Judgement = judgement;
        Disposition = disposition;
        if (!preserveControlState)
        {
            ControlState = ProductionRunControlState.Active;
        }

        CompletedAtUtc = completedAtUtc;
        LastTransitionAtUtc = completedAtUtc;
        FailureCode = ProductionRunText.Optional(failureCode, nameof(failureCode));
        FailureReason = ProductionRunText.Optional(failureReason, nameof(failureReason));
        RaiseStatusChanged(from, failureReason ?? "Production Run reached a terminal state.");
        RaiseDomainEvent(new ProductionRunTerminalDomainEvent(ToSnapshot()));
    }

    private void CancelOpenOperations(string reason, DateTimeOffset atUtc)
    {
        foreach (var operation in _operations.Where(operation => !operation.IsTerminal))
        {
            var from = operation.ExecutionStatus;
            operation.Cancel(reason, atUtc);
            RaiseOperationStatusChanged(operation, from, reason);
        }
    }

    private void ValidateDefinitionGraph()
    {
        if (_operationDefinitions.Count == 0)
        {
            throw new ArgumentException("A Production Run requires at least one operation.");
        }

        EnsureUnique(_operationDefinitions.Select(static definition => definition.OperationId),
            "operation ids");
        EnsureUnique(_routeTransitions.Select(static transition => transition.TransitionId),
            "route transition ids");
        var operationIds = _operationDefinitions
            .Select(static definition => definition.OperationId)
            .ToHashSet(StringComparer.Ordinal);
        if (!operationIds.Contains(EntryOperationId))
        {
            throw new ArgumentException(
                $"Entry operation {EntryOperationId} is not declared by the Production Run.");
        }

        foreach (var transition in _routeTransitions)
        {
            if (!operationIds.Contains(transition.SourceOperationId)
                || transition.TargetOperationId is not null
                    && !operationIds.Contains(transition.TargetOperationId))
            {
                throw new ArgumentException(
                    $"Route transition {transition.TransitionId} references an unknown operation.");
            }
        }

        foreach (var source in _routeTransitions.GroupBy(
                     static transition => transition.SourceOperationId,
                     StringComparer.Ordinal))
        {
            var conditional = source.Where(transition =>
                transition.Kind is RuntimeRouteTransitionKind.Judgement
                    or RuntimeRouteTransitionKind.Rework).ToArray();
            if (conditional.Where(transition => transition.Kind == RuntimeRouteTransitionKind.Judgement)
                    .GroupBy(static transition => transition.RequiredJudgement)
                    .Any(group => group.Count() > 1)
                || conditional.Where(transition => transition.Kind == RuntimeRouteTransitionKind.Rework)
                    .GroupBy(static transition => transition.RequiredJudgement)
                    .Any(group => group.Count() > 1))
            {
                throw new ArgumentException(
                    $"Operation {source.Key} has ambiguous transitions for one result judgement.");
            }

            var judgementFallbacks = conditional
                .Where(transition => transition.Kind == RuntimeRouteTransitionKind.Judgement)
                .Select(transition => transition.RequiredJudgement!.Value)
                .ToHashSet();
            if (conditional.Any(transition => transition.Kind == RuntimeRouteTransitionKind.Rework
                && !judgementFallbacks.Contains(transition.RequiredJudgement!.Value)))
            {
                throw new ArgumentException(
                    $"Operation {source.Key} bounded rework requires an explicit judgement fallback.");
            }

            var outputConditions = source
                .Where(transition => transition.Kind == RuntimeRouteTransitionKind.Condition)
                .Select(transition => transition.OutputCondition!)
                .ToArray();
            if (outputConditions.Select(condition => condition.OutputKey)
                    .Distinct(StringComparer.Ordinal).Count() > 1
                || outputConditions
                    .GroupBy(condition => (
                        condition.ExpectedValue.Kind,
                        condition.ExpectedValue.CanonicalValue))
                    .Any(group => group.Count() > 1))
            {
                throw new ArgumentException(
                    $"Operation {source.Key} typed output routes must compare one output key against unique exact values.");
            }

            if (outputConditions.Length > 0 && conditional.Length > 0)
            {
                throw new ArgumentException(
                    $"Operation {source.Key} cannot mix judgement routing with typed output routing.");
            }

            var sequences = source.Count(transition =>
                transition.Kind == RuntimeRouteTransitionKind.Sequence);
            if (sequences > 1)
            {
                throw new ArgumentException(
                    $"Operation {source.Key} has multiple sequence transitions; use an explicit parallel fork.");
            }

            if (outputConditions.Length > 0 && sequences != 1)
            {
                throw new ArgumentException(
                    $"Operation {source.Key} typed output routes require exactly one sequence fallback.");
            }

            if (conditional.Length > 0
                && sequences == 0
                && Enum.GetValues<ResultJudgement>().Any(candidate =>
                    !judgementFallbacks.Contains(candidate)))
            {
                throw new ArgumentException(
                    $"Operation {source.Key} judgement routes require a sequence fallback or one branch for every judgement.");
            }

            if (source.Any(transition => transition.Kind == RuntimeRouteTransitionKind.ParallelFork)
                && source.Any(transition => transition.Kind is not RuntimeRouteTransitionKind.ParallelFork))
            {
                throw new ArgumentException(
                    $"Operation {source.Key} cannot mix a parallel fork with other transition kinds.");
            }
        }

        var reachable = new HashSet<string>(StringComparer.Ordinal) { EntryOperationId };
        var queue = new Queue<string>();
        queue.Enqueue(EntryOperationId);
        while (queue.TryDequeue(out var operationId))
        {
            foreach (var target in _routeTransitions
                         .Where(transition => string.Equals(
                             transition.SourceOperationId,
                             operationId,
                             StringComparison.Ordinal))
                         .Where(transition => transition.TargetOperationId is not null)
                         .Select(static transition => transition.TargetOperationId!))
            {
                if (reachable.Add(target))
                {
                    queue.Enqueue(target);
                }
            }
        }

        var unreachable = operationIds.FirstOrDefault(operationId => !reachable.Contains(operationId));
        if (unreachable is not null)
        {
            throw new ArgumentException($"Operation {unreachable} is unreachable from {EntryOperationId}.");
        }
    }

    private void ValidateState()
    {
        RequireUtc(CreatedAtUtc, nameof(CreatedAtUtc));
        RequireUtc(LastTransitionAtUtc, nameof(LastTransitionAtUtc));
        if (StartedAtUtc is { } started)
        {
            RequireUtc(started, nameof(StartedAtUtc));
        }

        if (CompletedAtUtc is { } completed)
        {
            RequireUtc(completed, nameof(CompletedAtUtc));
        }

        if (ExecutionStatus == ExecutionStatus.Pending)
        {
            Require(StartedAtUtc is null && CompletedAtUtc is null && _operations.Count == 0,
                "Pending Production Run contains execution state.");
        }
        else if (ExecutionStatus == ExecutionStatus.Running)
        {
            Require(StartedAtUtc is not null && CompletedAtUtc is null,
                "Running Production Run has invalid timestamps.");
            Require(_operations.Count > 0, "Running Production Run has no activated operation.");
        }
        else
        {
            Require(CompletedAtUtc is not null, "Terminal Production Run has no completion timestamp.");
            Require(_operations.All(operation => operation.IsTerminal),
                "Terminal Production Run contains an open operation.");
        }

        if (ControlState == ProductionRunControlState.RecoveryRequired)
        {
            Require(ExecutionStatus == ExecutionStatus.Running
                && Disposition == ProductDisposition.Held
                && FailureCode is not null
                && FailureReason is not null,
                "Recovery-required Production Run must be running, held, and explain the interruption.");
        }

        if (ControlState == ProductionRunControlState.StopRequested)
        {
            Require(ExecutionStatus == ExecutionStatus.Running
                && Disposition == ProductDisposition.Held
                && FailureCode is "Runtime.ProductionRunStopRequested"
                    or "Runtime.ProductionRunSafeStopRequested"
                && FailureReason is not null,
                "Stop-requested Production Run must be running, held, and explain the request.");
        }


        if (SafeStopRequestedAtUtc is not null)
        {
            Require(SafeStopRequestedAtUtc >= CreatedAtUtc
                && SafeStopRequestedAtUtc <= LastTransitionAtUtc
                && SafeStopRequestedBy is not null
                && SafeStopReason is not null,
                "Safe Stop evidence must be within the Production Run timeline.");
        }
        else
        {
            Require(SafeStopRequestedBy is null && SafeStopReason is null,
                "Safe Stop actor and reason cannot exist without a request timestamp.");
        }


        if (SafeStopAcknowledgedAtUtc is not null)
        {
            Require(SafeStopRequestedAtUtc is not null
                && SafeStopAcknowledgedAtUtc >= SafeStopRequestedAtUtc
                && SafeStopAcknowledgedAtUtc <= LastTransitionAtUtc,
                "Safe Stop acknowledgement must follow its request within the Production Run timeline.");
        }

        foreach (var traversal in _transitionTraversals)
        {
            Require(traversal.Value > 0
                && _routeTransitions.Any(transition => string.Equals(
                    transition.TransitionId,
                    traversal.Key,
                    StringComparison.Ordinal)),
                "Production Run contains an invalid transition traversal counter.");
        }

        EnsureUnique(
            _recoveryDecisions.Select(decision => decision.DecisionId.ToString("D")),
            "recovery decision ids");
        Require(
            _recoveryDecisions.All(decision => decision.DecidedAtUtc >= CreatedAtUtc
                && decision.DecidedAtUtc <= LastTransitionAtUtc),
            "Production Run contains a Recovery Decision outside its execution timeline.");
        ValidateRecoveryDecisionState();
    }

    private void ValidateRecoveryDecisionState()
    {
        foreach (var decision in _recoveryDecisions)
        {
            switch (decision.Kind)
            {
                case ProductionRecoveryDecisionKind.Reconcile:
                    {
                        var operation = _operations.SingleOrDefault(candidate => string.Equals(
                            candidate.OperationRunId,
                            decision.OperationRunId,
                            StringComparison.Ordinal));
                        Require(
                            operation is not null
                            && operation.ExecutionStatus == ExecutionStatus.Completed
                            && operation.CompletedAtUtc == decision.DecidedAtUtc
                            && operation.Judgement == decision.ObservedJudgement
                            && operation.Outputs.Count == decision.ObservedOutputs.Count
                            && operation.Outputs.All(output =>
                                decision.ObservedOutputs.TryGetValue(output.Key, out var value)
                                && value == output.Value),
                            "Reconcile Recovery Decision differs from its completed Operation evidence.");
                        break;
                    }
                case ProductionRecoveryDecisionKind.Retry:
                    Require(
                        _operations.Count(operation => string.Equals(
                            operation.OperationId,
                            decision.OperationId,
                            StringComparison.Ordinal)) >= 2,
                        "Retry Recovery Decision does not have an interrupted and replacement Operation attempt.");
                    break;
                case ProductionRecoveryDecisionKind.Abort:
                    Require(
                        ExecutionStatus == ExecutionStatus.Canceled
                        && Judgement == ResultJudgement.Aborted,
                        "Abort Recovery Decision requires a canceled, aborted Production Run.");
                    break;
                case ProductionRecoveryDecisionKind.Scrap:
                    Require(
                        ExecutionStatus == ExecutionStatus.Completed
                        && Judgement == ResultJudgement.Failed
                        && Disposition == ProductDisposition.Scrapped,
                        "Scrap Recovery Decision requires a completed, failed, scrapped Production Run.");
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported Recovery Decision kind {decision.Kind}.");
            }
        }
    }

    private RuntimeOperationResult? FindRecoveryDecision(ProductionRecoveryDecision decision)
    {
        var existing = _recoveryDecisions.SingleOrDefault(candidate =>
            candidate.DecisionId == decision.DecisionId);
        if (existing is null)
        {
            return null;
        }

        return RecoveryDecisionEquals(existing, decision)
            ? RuntimeOperationResult.Accepted()
            : RuntimeOperationResult.Rejected(
                "Runtime.RecoveryDecisionIdentityMismatch",
                $"Recovery Decision {decision.DecisionId:D} already contains different immutable evidence.");
    }

    private RuntimeOperationResult? ValidateRecoveryDecisionTimestamp(
        ProductionRecoveryDecision decision) =>
        decision.DecidedAtUtc < LastTransitionAtUtc
            ? RuntimeOperationResult.Rejected(
                "Runtime.RecoveryDecisionPrecedesRecovery",
                $"Recovery Decision {decision.DecisionId:D} predates the current Recovery Required state.")
            : null;

    private static bool RecoveryDecisionEquals(
        ProductionRecoveryDecision left,
        ProductionRecoveryDecision right) =>
        left.DecisionId == right.DecisionId
        && left.Kind == right.Kind
        && string.Equals(left.ActorId, right.ActorId, StringComparison.Ordinal)
        && string.Equals(left.Reason, right.Reason, StringComparison.Ordinal)
        && string.Equals(left.EvidenceReference, right.EvidenceReference, StringComparison.Ordinal)
        && left.DecidedAtUtc == right.DecidedAtUtc
        && string.Equals(left.OperationRunId, right.OperationRunId, StringComparison.Ordinal)
        && string.Equals(left.OperationId, right.OperationId, StringComparison.Ordinal)
        && left.ObservedJudgement == right.ObservedJudgement
        && left.ObservedOutputs.Count == right.ObservedOutputs.Count
        && left.ObservedOutputs.All(output =>
            right.ObservedOutputs.TryGetValue(output.Key, out var value) && value == output.Value);

    private OperationRun? FindOperationRun(string operationRunId) =>
        _operations.SingleOrDefault(operation => string.Equals(
            operation.OperationRunId,
            operationRunId,
            StringComparison.Ordinal));

    private RuntimeOperationResult OperationNotFound(string operationRunId) =>
        RuntimeOperationResult.Rejected(
            "Runtime.OperationRunNotFound",
            $"Operation Run {operationRunId} does not exist in Production Run {Id}.");

    private RuntimeOperationResult Reject(string code, string action) =>
        RuntimeOperationResult.Rejected(
            code,
            $"Production Run {Id} cannot {action} from {ExecutionStatus}/{ControlState}.");

    private void RaiseStatusChanged(ExecutionStatus from, string reason) =>
        RaiseDomainEvent(new ProductionRunStatusChangedDomainEvent(Id, from, ExecutionStatus, reason));

    private void RaiseOperationStatusChanged(
        OperationRun operation,
        ExecutionStatus from,
        string reason) =>
        RaiseDomainEvent(new OperationRunStatusChangedDomainEvent(
            Id,
            operation.OperationRunId,
            operation.OperationId,
            from,
            operation.ExecutionStatus,
            operation.RuntimeSessionId,
            reason));

    private static void EnsureUnique(IEnumerable<string> values, string description)
    {
        var materialized = values.ToArray();
        if (materialized.Distinct(StringComparer.Ordinal).Count() != materialized.Length
            || materialized.Distinct(StringComparer.OrdinalIgnoreCase).Count() != materialized.Length)
        {
            throw new ArgumentException($"Production Run {description} must be unique, including case.");
        }
    }

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException($"{parameterName} must use UTC offset zero.", parameterName);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
