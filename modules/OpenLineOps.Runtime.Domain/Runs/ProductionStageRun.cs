using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Operations;

namespace OpenLineOps.Runtime.Domain.Runs;

public sealed class ProductionStageRun
{
    private ProductionStageRun(
        ProductionStageRunDefinition definition,
        ProductionStageRunStatus status,
        RuntimeSessionId? runtimeSessionId,
        DateTimeOffset? startedAtUtc,
        DateTimeOffset? completedAtUtc,
        string? failureCode,
        string? failureReason,
        int completedStepCount,
        int commandCount,
        int incidentCount)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        Status = status;
        RuntimeSessionId = runtimeSessionId;
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
        FailureCode = ProductionRunText.Optional(failureCode, nameof(failureCode));
        FailureReason = ProductionRunText.Optional(failureReason, nameof(failureReason));
        CompletedStepCount = completedStepCount;
        CommandCount = commandCount;
        IncidentCount = incidentCount;
        ValidateState();
    }

    public string StageId => Definition.StageId;

    public int Sequence => Definition.Sequence;

    public string WorkstationId => Definition.WorkstationId;

    public StationId StationId => Definition.StationId;

    public ProcessDefinitionId ProcessDefinitionId => Definition.ProcessDefinitionId;

    public ProcessVersionId ProcessVersionId => Definition.ProcessVersionId;

    public ConfigurationSnapshotId ConfigurationSnapshotId => Definition.ConfigurationSnapshotId;

    public RecipeSnapshotId RecipeSnapshotId => Definition.RecipeSnapshotId;

    public ProductionStageRunStatus Status { get; private set; }

    public RuntimeSessionId? RuntimeSessionId { get; private set; }

    public DateTimeOffset? StartedAtUtc { get; private set; }

    public DateTimeOffset? CompletedAtUtc { get; private set; }

    public string? FailureCode { get; private set; }

    public string? FailureReason { get; private set; }

    public int CompletedStepCount { get; private set; }

    public int CommandCount { get; private set; }

    public int IncidentCount { get; private set; }

    private ProductionStageRunDefinition Definition { get; }

    internal static ProductionStageRun Create(ProductionStageRunDefinition definition)
    {
        return new ProductionStageRun(
            definition,
            ProductionStageRunStatus.Pending,
            null,
            null,
            null,
            null,
            null,
            0,
            0,
            0);
    }

    internal static ProductionStageRun Restore(ProductionStageRunSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new ProductionStageRun(
            new ProductionStageRunDefinition(
                snapshot.StageId,
                snapshot.Sequence,
                snapshot.WorkstationId,
                snapshot.StationId,
                snapshot.ProcessDefinitionId,
                snapshot.ProcessVersionId,
                snapshot.ConfigurationSnapshotId,
                snapshot.RecipeSnapshotId),
            snapshot.Status,
            snapshot.RuntimeSessionId,
            snapshot.StartedAtUtc,
            snapshot.CompletedAtUtc,
            snapshot.FailureCode,
            snapshot.FailureReason,
            snapshot.CompletedStepCount,
            snapshot.CommandCount,
            snapshot.IncidentCount);
    }

    internal RuntimeOperationResult Start(RuntimeSessionId runtimeSessionId, DateTimeOffset startedAtUtc)
    {
        if (runtimeSessionId.Value == Guid.Empty)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.ProductionStageSessionIdRequired",
                $"Production stage {StageId} requires a non-empty runtime session id.");
        }

        if (Status != ProductionStageRunStatus.Pending)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.ProductionStageStartRejected",
                $"Production stage {StageId} cannot start from {Status}.");
        }

        Status = ProductionStageRunStatus.Running;
        RuntimeSessionId = runtimeSessionId;
        StartedAtUtc = startedAtUtc;
        return RuntimeOperationResult.Accepted();
    }

    internal RuntimeOperationResult Complete(
        int completedStepCount,
        int commandCount,
        int incidentCount,
        DateTimeOffset completedAtUtc)
    {
        if (Status != ProductionStageRunStatus.Running)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.ProductionStageCompleteRejected",
                $"Production stage {StageId} cannot complete from {Status}.");
        }

        Status = ProductionStageRunStatus.Completed;
        CompletedAtUtc = completedAtUtc;
        SetMetrics(completedStepCount, commandCount, incidentCount);
        return RuntimeOperationResult.Accepted();
    }

    internal RuntimeOperationResult Fail(
        string code,
        string reason,
        int completedStepCount,
        int commandCount,
        int incidentCount,
        DateTimeOffset failedAtUtc)
    {
        if (Status != ProductionStageRunStatus.Running)
        {
            return RuntimeOperationResult.Rejected(
                "Runtime.ProductionStageFailRejected",
                $"Production stage {StageId} cannot fail from {Status}.");
        }

        Status = ProductionStageRunStatus.Failed;
        CompletedAtUtc = failedAtUtc;
        FailureCode = ProductionRunText.Required(code, nameof(code));
        FailureReason = ProductionRunText.Required(reason, nameof(reason));
        SetMetrics(completedStepCount, commandCount, incidentCount);
        return RuntimeOperationResult.Accepted();
    }

    internal void Cancel(
        string reason,
        int completedStepCount,
        int commandCount,
        int incidentCount,
        DateTimeOffset canceledAtUtc)
    {
        if (Status != ProductionStageRunStatus.Running)
        {
            throw new InvalidOperationException(
                $"Production stage {StageId} cannot be canceled from {Status}.");
        }

        Status = ProductionStageRunStatus.Canceled;
        CompletedAtUtc = canceledAtUtc;
        FailureCode = "Runtime.ProductionRunCanceled";
        FailureReason = ProductionRunText.Required(reason, nameof(reason));
        SetMetrics(completedStepCount, commandCount, incidentCount);
    }

    internal void Skip(string reason, DateTimeOffset skippedAtUtc)
    {
        if (Status != ProductionStageRunStatus.Pending)
        {
            throw new InvalidOperationException(
                $"Production stage {StageId} cannot be skipped from {Status}.");
        }

        Status = ProductionStageRunStatus.Skipped;
        CompletedAtUtc = skippedAtUtc;
        FailureReason = ProductionRunText.Required(reason, nameof(reason));
    }

    internal ProductionStageRunSnapshot ToSnapshot()
    {
        return new ProductionStageRunSnapshot(
            StageId,
            Sequence,
            WorkstationId,
            StationId,
            ProcessDefinitionId,
            ProcessVersionId,
            ConfigurationSnapshotId,
            RecipeSnapshotId,
            Status,
            RuntimeSessionId,
            StartedAtUtc,
            CompletedAtUtc,
            FailureCode,
            FailureReason,
            CompletedStepCount,
            CommandCount,
            IncidentCount);
    }

    private void ValidateState()
    {
        ValidateMetrics(CompletedStepCount, CommandCount, IncidentCount);

        if (Status == ProductionStageRunStatus.Pending)
        {
            if (RuntimeSessionId is not null || StartedAtUtc is not null || CompletedAtUtc is not null
                || FailureCode is not null || FailureReason is not null
                || HasMetrics())
            {
                throw new InvalidOperationException(
                    $"Pending production stage {StageId} contains execution state.");
            }

            return;
        }

        if (Status == ProductionStageRunStatus.Skipped)
        {
            if (RuntimeSessionId is not null || StartedAtUtc is not null || CompletedAtUtc is null
                || FailureCode is not null || FailureReason is null
                || HasMetrics())
            {
                throw new InvalidOperationException(
                    $"Skipped production stage {StageId} has invalid state.");
            }

            return;
        }

        if (RuntimeSessionId is null || StartedAtUtc is null)
        {
            throw new InvalidOperationException(
                $"Production stage {StageId} in {Status} does not declare its runtime session.");
        }

        if (Status == ProductionStageRunStatus.Running)
        {
            if (CompletedAtUtc is not null || FailureCode is not null || FailureReason is not null
                || HasMetrics())
            {
                throw new InvalidOperationException(
                    $"Running production stage {StageId} contains terminal state.");
            }

            return;
        }

        if (CompletedAtUtc is null)
        {
            throw new InvalidOperationException(
                $"Terminal production stage {StageId} does not declare completion time.");
        }

        if (Status == ProductionStageRunStatus.Completed
            && (FailureCode is not null || FailureReason is not null))
        {
            throw new InvalidOperationException(
                $"Completed production stage {StageId} contains failure state.");
        }

        if (Status is ProductionStageRunStatus.Failed or ProductionStageRunStatus.Canceled
            && (FailureCode is null || FailureReason is null))
        {
            throw new InvalidOperationException(
                $"Failed or canceled production stage {StageId} does not declare failure state.");
        }
    }

    private void SetMetrics(int completedStepCount, int commandCount, int incidentCount)
    {
        ValidateMetrics(completedStepCount, commandCount, incidentCount);
        CompletedStepCount = completedStepCount;
        CommandCount = commandCount;
        IncidentCount = incidentCount;
    }

    private bool HasMetrics()
    {
        return CompletedStepCount != 0 || CommandCount != 0 || IncidentCount != 0;
    }

    private static void ValidateMetrics(int completedStepCount, int commandCount, int incidentCount)
    {
        if (completedStepCount < 0 || commandCount < 0 || incidentCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedStepCount),
                "Production stage session metrics cannot be negative.");
        }
    }
}
