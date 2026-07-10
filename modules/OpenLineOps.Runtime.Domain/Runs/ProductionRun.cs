using OpenLineOps.Domain.Abstractions.Entities;
using OpenLineOps.Runtime.Domain.Events;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Operations;

namespace OpenLineOps.Runtime.Domain.Runs;

public sealed class ProductionRun : AggregateRoot<ProductionRunId>
{
    private readonly List<ProductionStageRun> _stages = [];

    private ProductionRun(
        ProductionRunId id,
        string projectId,
        string applicationId,
        string projectSnapshotId,
        string topologyId,
        string productionLineDefinitionId,
        DutIdentity dutIdentity,
        string? batchId,
        string? fixtureId,
        string? deviceId,
        string actorId,
        DateTimeOffset createdAtUtc)
        : base(id)
    {
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException("Production run id cannot be empty.", nameof(id));
        }

        ProjectId = ProductionRunText.Required(projectId, nameof(projectId));
        ApplicationId = ProductionRunText.Required(applicationId, nameof(applicationId));
        ProjectSnapshotId = ProductionRunText.Required(projectSnapshotId, nameof(projectSnapshotId));
        TopologyId = ProductionRunText.Required(topologyId, nameof(topologyId));
        ProductionLineDefinitionId = ProductionRunText.Required(
            productionLineDefinitionId,
            nameof(productionLineDefinitionId));
        DutIdentity = dutIdentity ?? throw new ArgumentNullException(nameof(dutIdentity));
        BatchId = ProductionRunText.Optional(batchId, nameof(batchId));
        FixtureId = ProductionRunText.Optional(fixtureId, nameof(fixtureId));
        DeviceId = ProductionRunText.Optional(deviceId, nameof(deviceId));
        ActorId = ProductionRunText.Required(actorId, nameof(actorId));
        CreatedAtUtc = createdAtUtc;
        LastTransitionAtUtc = createdAtUtc;
        Status = ProductionRunStatus.Created;
    }

    public string ProjectId { get; }

    public string ApplicationId { get; }

    public string ProjectSnapshotId { get; }

    public string TopologyId { get; }

    public string ProductionLineDefinitionId { get; }

    public DutIdentity DutIdentity { get; }

    public string? BatchId { get; }

    public string? FixtureId { get; }

    public string? DeviceId { get; }

    public string ActorId { get; }

    public ProductionRunStatus Status { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset LastTransitionAtUtc { get; private set; }

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public string? FailureCode { get; private set; }

    public string? FailureReason { get; private set; }

    public IReadOnlyList<ProductionStageRun> Stages => _stages.AsReadOnly();

    public bool IsTerminal => Status is ProductionRunStatus.Completed
        or ProductionRunStatus.Failed
        or ProductionRunStatus.Canceled;

    public static ProductionRun Create(
        ProductionRunId id,
        string projectId,
        string applicationId,
        string projectSnapshotId,
        string topologyId,
        string productionLineDefinitionId,
        DutIdentity dutIdentity,
        string? batchId,
        string? fixtureId,
        string? deviceId,
        string actorId,
        DateTimeOffset createdAtUtc,
        IEnumerable<ProductionStageRunDefinition> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);
        var definitions = stages.OrderBy(stage => stage.Sequence).ToArray();
        ValidateDefinitions(definitions);

        var run = new ProductionRun(
            id,
            projectId,
            applicationId,
            projectSnapshotId,
            topologyId,
            productionLineDefinitionId,
            dutIdentity,
            batchId,
            fixtureId,
            deviceId,
            actorId,
            createdAtUtc);
        run._stages.AddRange(definitions.Select(ProductionStageRun.Create));
        run.RaiseDomainEvent(new ProductionRunCreatedDomainEvent(id));
        return run;
    }

    public static ProductionRun Restore(ProductionRunSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Stages);

        var run = new ProductionRun(
            snapshot.RunId,
            snapshot.ProjectId,
            snapshot.ApplicationId,
            snapshot.ProjectSnapshotId,
            snapshot.TopologyId,
            snapshot.ProductionLineDefinitionId,
            snapshot.DutIdentity,
            snapshot.BatchId,
            snapshot.FixtureId,
            snapshot.DeviceId,
            snapshot.ActorId,
            snapshot.CreatedAtUtc)
        {
            Status = snapshot.Status,
            LastTransitionAtUtc = snapshot.LastTransitionAtUtc,
            StartedAtUtc = snapshot.StartedAtUtc,
            CompletedAtUtc = snapshot.CompletedAtUtc,
            FailureCode = ProductionRunText.Optional(snapshot.FailureCode, nameof(snapshot.FailureCode)),
            FailureReason = ProductionRunText.Optional(snapshot.FailureReason, nameof(snapshot.FailureReason))
        };
        run._stages.AddRange(snapshot.Stages.Select(ProductionStageRun.Restore));
        ValidateDefinitions(run._stages.Select(stage => new ProductionStageRunDefinition(
            stage.StageId,
            stage.Sequence,
            stage.WorkstationId,
            stage.StationId,
            stage.ProcessDefinitionId,
            stage.ProcessVersionId,
            stage.ConfigurationSnapshotId,
            stage.RecipeSnapshotId)).ToArray());
        run.ValidateState();
        run.ClearDomainEvents();
        return run;
    }

    public RuntimeOperationResult Start(DateTimeOffset startedAtUtc)
    {
        if (Status != ProductionRunStatus.Created)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.ProductionRunStartRejected",
                $"Production run {Id} cannot start from {Status}.");
        }

        var fromStatus = Status;
        Status = ProductionRunStatus.Running;
        StartedAtUtc = startedAtUtc;
        LastTransitionAtUtc = startedAtUtc;
        RaiseDomainEvent(new ProductionRunStatusChangedDomainEvent(
            Id,
            fromStatus,
            Status,
            "Production run started."));
        return RuntimeOperationResult.Accepted();
    }

    public RuntimeOperationResult StartStage(
        string stageId,
        RuntimeSessionId runtimeSessionId,
        DateTimeOffset startedAtUtc)
    {
        if (Status != ProductionRunStatus.Running)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.ProductionRunNotRunning",
                $"Production run {Id} must be running before a stage can start.");
        }

        if (_stages.Any(stage => stage.Status == ProductionStageRunStatus.Running))
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.ProductionStageAlreadyRunning",
                $"Production run {Id} already has a running stage.");
        }

        var nextStage = _stages.FirstOrDefault(stage => stage.Status == ProductionStageRunStatus.Pending);
        if (nextStage is null || !string.Equals(nextStage.StageId, stageId, StringComparison.Ordinal))
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.ProductionStageOutOfOrder",
                $"Production stage {stageId} is not the next pending stage in run {Id}.");
        }

        if (_stages.TakeWhile(stage => stage != nextStage)
            .Any(stage => stage.Status != ProductionStageRunStatus.Completed))
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.ProductionStagePredecessorIncomplete",
                $"Production stage {stageId} has an incomplete predecessor.");
        }

        var result = nextStage.Start(runtimeSessionId, startedAtUtc);
        if (!result.Succeeded)
        {
            return result;
        }

        LastTransitionAtUtc = startedAtUtc;
        RaiseStageStatusChanged(
            nextStage,
            ProductionStageRunStatus.Pending,
            "Production stage started.");
        return result;
    }

    public RuntimeOperationResult CompleteStage(
        string stageId,
        int completedStepCount,
        int commandCount,
        int incidentCount,
        DateTimeOffset completedAtUtc)
    {
        var stage = FindStage(stageId);
        if (stage is null)
        {
            return StageNotFound(stageId);
        }

        var result = stage.Complete(
            completedStepCount,
            commandCount,
            incidentCount,
            completedAtUtc);
        if (!result.Succeeded)
        {
            return result;
        }

        LastTransitionAtUtc = completedAtUtc;
        RaiseStageStatusChanged(stage, ProductionStageRunStatus.Running, "Production stage completed.");

        if (_stages.All(candidate => candidate.Status == ProductionStageRunStatus.Completed))
        {
            TransitionToTerminal(
                ProductionRunStatus.Completed,
                completedAtUtc,
                "Production run completed.",
                null,
                null);
        }

        return result;
    }

    public RuntimeOperationResult FailStage(
        string stageId,
        string code,
        string reason,
        int completedStepCount,
        int commandCount,
        int incidentCount,
        DateTimeOffset failedAtUtc)
    {
        var stage = FindStage(stageId);
        if (stage is null)
        {
            return StageNotFound(stageId);
        }

        var normalizedCode = ProductionRunText.Required(code, nameof(code));
        var normalizedReason = ProductionRunText.Required(reason, nameof(reason));
        var result = stage.Fail(
            normalizedCode,
            normalizedReason,
            completedStepCount,
            commandCount,
            incidentCount,
            failedAtUtc);
        if (!result.Succeeded)
        {
            return result;
        }

        LastTransitionAtUtc = failedAtUtc;
        RaiseStageStatusChanged(stage, ProductionStageRunStatus.Running, normalizedReason);
        SkipPendingStages("Skipped because an earlier production stage failed.", failedAtUtc);
        TransitionToTerminal(
            ProductionRunStatus.Failed,
            failedAtUtc,
            normalizedReason,
            normalizedCode,
            normalizedReason);
        return result;
    }

    public RuntimeOperationResult MarkInterrupted(
        string reason,
        int completedStepCount,
        int commandCount,
        int incidentCount,
        DateTimeOffset interruptedAtUtc)
    {
        if (Status != ProductionRunStatus.Running)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.ProductionRunInterruptRejected",
                $"Production run {Id} cannot be interrupted from {Status}.");
        }

        var normalizedReason = ProductionRunText.Required(reason, nameof(reason));
        var runningStage = _stages.SingleOrDefault(
            stage => stage.Status == ProductionStageRunStatus.Running);
        if (runningStage is not null)
        {
            var failResult = runningStage.Fail(
                "Runtime.ProductionRunInterrupted",
                normalizedReason,
                completedStepCount,
                commandCount,
                incidentCount,
                interruptedAtUtc);
            if (!failResult.Succeeded)
            {
                return failResult;
            }

            RaiseStageStatusChanged(
                runningStage,
                ProductionStageRunStatus.Running,
                normalizedReason);
        }

        SkipPendingStages(
            "Skipped because the production run was interrupted.",
            interruptedAtUtc);
        TransitionToTerminal(
            ProductionRunStatus.Failed,
            interruptedAtUtc,
            normalizedReason,
            "Runtime.ProductionRunInterrupted",
            normalizedReason);
        return RuntimeOperationResult.Accepted();
    }

    public RuntimeOperationResult Cancel(
        string reason,
        int completedStepCount,
        int commandCount,
        int incidentCount,
        DateTimeOffset canceledAtUtc)
    {
        if (IsTerminal)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.ProductionRunCancelRejected",
                $"Production run {Id} cannot be canceled from {Status}.");
        }

        var normalizedReason = ProductionRunText.Required(reason, nameof(reason));
        var runningStage = _stages.SingleOrDefault(
            stage => stage.Status == ProductionStageRunStatus.Running);
        if (runningStage is not null)
        {
            runningStage.Cancel(
                normalizedReason,
                completedStepCount,
                commandCount,
                incidentCount,
                canceledAtUtc);
            RaiseStageStatusChanged(
                runningStage,
                ProductionStageRunStatus.Running,
                normalizedReason);
        }

        SkipPendingStages("Skipped because the production run was canceled.", canceledAtUtc);
        TransitionToTerminal(
            ProductionRunStatus.Canceled,
            canceledAtUtc,
            normalizedReason,
            "Runtime.ProductionRunCanceled",
            normalizedReason);
        return RuntimeOperationResult.Accepted();
    }

    public ProductionRunSnapshot ToSnapshot()
    {
        return new ProductionRunSnapshot(
            Id,
            ProjectId,
            ApplicationId,
            ProjectSnapshotId,
            TopologyId,
            ProductionLineDefinitionId,
            DutIdentity,
            BatchId,
            FixtureId,
            DeviceId,
            ActorId,
            Status,
            CreatedAtUtc,
            LastTransitionAtUtc,
            StartedAtUtc,
            CompletedAtUtc,
            FailureCode,
            FailureReason,
            _stages.Select(stage => stage.ToSnapshot()).ToArray());
    }

    private static void ValidateDefinitions(ProductionStageRunDefinition[] definitions)
    {
        if (definitions.Length == 0)
        {
            throw new ArgumentException("A production run must contain at least one stage.", nameof(definitions));
        }

        if (definitions.Select(stage => stage.StageId).Distinct(StringComparer.Ordinal).Count()
            != definitions.Length)
        {
            throw new ArgumentException("Production stage ids must be unique.", nameof(definitions));
        }

        for (var index = 0; index < definitions.Length; index++)
        {
            var expectedSequence = index + 1;
            if (definitions[index].Sequence != expectedSequence)
            {
                throw new ArgumentException(
                    $"Production stage sequence must be contiguous from 1; expected {expectedSequence}.",
                    nameof(definitions));
            }
        }
    }

    private void ValidateState()
    {
        if (LastTransitionAtUtc < CreatedAtUtc)
        {
            throw new InvalidOperationException("Production run transition time precedes creation time.");
        }

        var runningCount = _stages.Count(stage => stage.Status == ProductionStageRunStatus.Running);
        if (runningCount > 1)
        {
            throw new InvalidOperationException("A production run cannot contain more than one running stage.");
        }

        switch (Status)
        {
            case ProductionRunStatus.Created:
                Require(StartedAtUtc is null && CompletedAtUtc is null && FailureCode is null && FailureReason is null,
                    "Created production run contains lifecycle state.");
                Require(_stages.All(stage => stage.Status == ProductionStageRunStatus.Pending),
                    "Created production run contains a non-pending stage.");
                break;
            case ProductionRunStatus.Running:
                Require(StartedAtUtc is not null && CompletedAtUtc is null && FailureCode is null && FailureReason is null,
                    "Running production run has invalid lifecycle state.");
                Require(_stages.All(stage => stage.Status is ProductionStageRunStatus.Completed
                    or ProductionStageRunStatus.Running
                    or ProductionStageRunStatus.Pending),
                    "Running production run contains a terminal failure stage.");
                ValidateStageOrder();
                break;
            case ProductionRunStatus.Completed:
                Require(StartedAtUtc is not null && CompletedAtUtc is not null
                    && FailureCode is null && FailureReason is null,
                    "Completed production run has invalid lifecycle state.");
                Require(_stages.All(stage => stage.Status == ProductionStageRunStatus.Completed),
                    "Completed production run contains an incomplete stage.");
                break;
            case ProductionRunStatus.Failed:
                Require(StartedAtUtc is not null && CompletedAtUtc is not null
                    && FailureCode is not null && FailureReason is not null,
                    "Failed production run has invalid lifecycle state.");
                Require(_stages.Count(stage => stage.Status == ProductionStageRunStatus.Failed) <= 1,
                    "Failed production run contains more than one failed stage.");
                Require(_stages.All(stage => stage.Status is ProductionStageRunStatus.Completed
                    or ProductionStageRunStatus.Failed
                    or ProductionStageRunStatus.Skipped),
                    "Failed production run contains invalid stage state.");
                ValidateStageOrder();
                break;
            case ProductionRunStatus.Canceled:
                Require(CompletedAtUtc is not null && FailureCode is not null && FailureReason is not null,
                    "Canceled production run has invalid lifecycle state.");
                Require(_stages.Count(stage => stage.Status == ProductionStageRunStatus.Canceled) <= 1,
                    "Canceled production run contains more than one canceled stage.");
                Require(_stages.All(stage => stage.Status is ProductionStageRunStatus.Completed
                    or ProductionStageRunStatus.Canceled
                    or ProductionStageRunStatus.Skipped),
                    "Canceled production run contains invalid stage state.");
                ValidateStageOrder();
                break;
            default:
                throw new InvalidOperationException($"Unsupported production run status {Status}.");
        }
    }

    private void ValidateStageOrder()
    {
        var encounteredOpenOrTerminal = false;
        foreach (var stage in _stages)
        {
            if (stage.Status == ProductionStageRunStatus.Completed)
            {
                Require(!encounteredOpenOrTerminal,
                    "A completed production stage appears after a non-completed stage.");
                continue;
            }

            encounteredOpenOrTerminal = true;
        }
    }

    private void SkipPendingStages(string reason, DateTimeOffset skippedAtUtc)
    {
        foreach (var pendingStage in _stages.Where(
            stage => stage.Status == ProductionStageRunStatus.Pending))
        {
            pendingStage.Skip(reason, skippedAtUtc);
            RaiseStageStatusChanged(
                pendingStage,
                ProductionStageRunStatus.Pending,
                reason);
        }
    }

    private void TransitionToTerminal(
        ProductionRunStatus targetStatus,
        DateTimeOffset completedAtUtc,
        string reason,
        string? failureCode,
        string? failureReason)
    {
        var fromStatus = Status;
        Status = targetStatus;
        LastTransitionAtUtc = completedAtUtc;
        CompletedAtUtc = completedAtUtc;
        FailureCode = failureCode;
        FailureReason = failureReason;
        RaiseDomainEvent(new ProductionRunStatusChangedDomainEvent(
            Id,
            fromStatus,
            targetStatus,
            reason));
        RaiseDomainEvent(new ProductionRunTerminalDomainEvent(ToSnapshot()));
    }

    private void RaiseStageStatusChanged(
        ProductionStageRun stage,
        ProductionStageRunStatus fromStatus,
        string reason)
    {
        RaiseDomainEvent(new ProductionStageRunStatusChangedDomainEvent(
            Id,
            stage.StageId,
            stage.Sequence,
            fromStatus,
            stage.Status,
            stage.RuntimeSessionId,
            reason));
    }

    private ProductionStageRun? FindStage(string stageId)
    {
        if (string.IsNullOrWhiteSpace(stageId))
        {
            return null;
        }

        return _stages.SingleOrDefault(
            stage => string.Equals(stage.StageId, stageId, StringComparison.Ordinal));
    }

    private static RuntimeOperationResult StageNotFound(string stageId)
    {
        return RuntimeOperationResult.Rejected(
            "Runtime.ProductionStageNotFound",
            $"Production stage {stageId} was not found.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
