using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Application.Runs;

public interface IStationOperationDispatcher
{
    ValueTask<StationOperationDispatchResult> DispatchAsync(
        StationOperationDispatchRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record StationOperationDispatchRequest
{
    public StationOperationDispatchRequest(
        ProductionRunSnapshot run,
        OperationRunSnapshot operation,
        OperationExecutionPlan executionPlan,
        RuntimeSessionId runtimeSessionId,
        IReadOnlyCollection<ResourceLease> resourceLeases)
    {
        Run = run ?? throw new ArgumentNullException(nameof(run));
        Operation = operation ?? throw new ArgumentNullException(nameof(operation));
        ExecutionPlan = executionPlan ?? throw new ArgumentNullException(nameof(executionPlan));
        if (runtimeSessionId.Value == Guid.Empty)
        {
            throw new ArgumentException("Runtime Session id cannot be empty.", nameof(runtimeSessionId));
        }

        RuntimeSessionId = runtimeSessionId;
        ArgumentNullException.ThrowIfNull(resourceLeases);
        ResourceLeases = resourceLeases.ToArray();
        IdempotencyKey = $"{run.RunId.Value:D}/{operation.OperationRunId}";
        if (ResourceLeases.Count != operation.Definition.ResourceRequirements.Count
            || ResourceLeases.Any(lease => lease.ProductionRunId != run.RunId
                || !string.Equals(
                    lease.OperationRunId,
                    operation.OperationRunId,
                    StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Station operation dispatch requires every resource lease owned by the Operation Run.",
                nameof(resourceLeases));
        }
    }

    public ProductionRunSnapshot Run { get; }

    public OperationRunSnapshot Operation { get; }

    public OperationExecutionPlan ExecutionPlan { get; }

    public RuntimeSessionId RuntimeSessionId { get; }

    public IReadOnlyCollection<ResourceLease> ResourceLeases { get; }

    public string IdempotencyKey { get; }
}

public sealed record StationOperationDispatchResult
{
    public StationOperationDispatchResult(
        ExecutionStatus executionStatus,
        ResultJudgement judgement,
        IReadOnlyDictionary<string, ProductionContextValue>? outputs,
        int completedStepCount,
        int commandCount,
        int incidentCount,
        DateTimeOffset completedAtUtc,
        string? failureCode = null,
        string? failureReason = null)
    {
        if (executionStatus is ExecutionStatus.Pending or ExecutionStatus.Running)
        {
            throw new ArgumentException("Station operation result must be terminal.", nameof(executionStatus));
        }

        if (completedStepCount < 0 || commandCount < 0 || incidentCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedStepCount),
                "Station operation metrics cannot be negative.");
        }

        var failedExecution = executionStatus != ExecutionStatus.Completed;
        if (failedExecution != (failureCode is not null && failureReason is not null))
        {
            throw new ArgumentException(
                "Unsuccessful station execution requires failure details; completed execution cannot include them.");
        }

        ExecutionStatus = executionStatus;
        Judgement = judgement;
        Outputs = outputs?.ToDictionary() ?? [];
        CompletedStepCount = completedStepCount;
        CommandCount = commandCount;
        IncidentCount = incidentCount;
        CompletedAtUtc = completedAtUtc;
        FailureCode = failureCode;
        FailureReason = failureReason;
    }

    public ExecutionStatus ExecutionStatus { get; }

    public ResultJudgement Judgement { get; }

    public IReadOnlyDictionary<string, ProductionContextValue> Outputs { get; }

    public int CompletedStepCount { get; }

    public int CommandCount { get; }

    public int IncidentCount { get; }

    public DateTimeOffset CompletedAtUtc { get; }

    public string? FailureCode { get; }

    public string? FailureReason { get; }
}
