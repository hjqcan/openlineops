using System.Security.Cryptography;
using System.Text;
using OpenLineOps.Runtime.Application.Runs;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Tests;

internal static class ProductionRunExecutionEvidenceTestFactory
{
    public static OperationExecutionEvidence Create(
        StationOperationDispatchRequest request,
        ExecutionStatus executionStatus,
        ResultJudgement judgement,
        DateTimeOffset completedAtUtc,
        int completedStepCount = 0,
        int commandCount = 0,
        int incidentCount = 0,
        string? failureCode = null,
        string? failureReason = null) => Create(
            ProductionRun.Restore(request.Run),
            request.Operation.OperationRunId,
            executionStatus,
            judgement,
            completedAtUtc,
            completedStepCount,
            commandCount,
            incidentCount,
            failureCode,
            failureReason);

    public static OperationExecutionEvidence Create(
        ProductionRun run,
        string operationRunId,
        ExecutionStatus executionStatus,
        ResultJudgement judgement,
        DateTimeOffset completedAtUtc,
        int completedStepCount = 0,
        int commandCount = 0,
        int incidentCount = 0,
        string? failureCode = null,
        string? failureReason = null)
    {
        var snapshot = run.ToSnapshot();
        var operation = snapshot.Operations.Single(candidate => string.Equals(
            candidate.OperationRunId,
            operationRunId,
            StringComparison.Ordinal));
        var stepCount = Math.Max(completedStepCount, commandCount == 0 ? 0 : 1);
        var steps = Enumerable.Range(0, stepCount)
            .Select(index =>
            {
                var completed = index < completedStepCount;
                var status = completed
                    ? "Completed"
                    : executionStatus == ExecutionStatus.Canceled ? "Canceled" : "Failed";
                return new OperationStepExecutionEvidence(
                    Id(operationRunId, "step", index),
                    $"node.{index:D4}",
                    $"action.{index:D4}",
                    "Station",
                    operation.Definition.StationSystemId,
                    $"Test step {index:D4}",
                    status,
                    completedAtUtc,
                    completedAtUtc,
                    status == "Failed" ? failureReason ?? "Test step failed." : null);
            })
            .ToArray();
        var commands = Enumerable.Range(0, commandCount)
            .Select(index =>
            {
                var step = steps[Math.Min(index, steps.Length - 1)];
                var commandStatus = executionStatus;
                var accepted = commandStatus is ExecutionStatus.Completed
                    or ExecutionStatus.Failed
                    or ExecutionStatus.TimedOut
                    ? completedAtUtc
                    : (DateTimeOffset?)null;
                return new OperationCommandExecutionEvidence(
                    Id(operationRunId, "command", index),
                    step.StepId,
                    step.NodeId,
                    step.ActionId,
                    step.TargetKind,
                    step.TargetId,
                    "capability.test",
                    "Execute",
                    commandStatus,
                    completedAtUtc,
                    completedAtUtc,
                    accepted,
                    accepted,
                    completedAtUtc,
                    commandStatus == ExecutionStatus.Completed ? "{}" : null,
                    commandStatus == ExecutionStatus.Completed
                        ? null
                        : failureReason ?? "Test command failed.",
                    commandStatus switch
                    {
                        ExecutionStatus.Completed => judgement,
                        ExecutionStatus.Canceled => ResultJudgement.Aborted,
                        _ => ResultJudgement.Unknown
                    });
            })
            .ToArray();
        var incidents = Enumerable.Range(0, incidentCount)
            .Select(index => new OperationIncidentExecutionEvidence(
                Id(operationRunId, "incident", index),
                "Error",
                failureCode ?? "Runtime.TestIncident",
                failureReason ?? "Test incident.",
                completedAtUtc))
            .ToArray();
        return new OperationExecutionEvidence(
            OperationExecutionEvidenceOrigin.RuntimeSession,
            operation.RuntimeSessionId?.Value
                ?? throw new InvalidOperationException("Test operation must be started before evidence is created."),
            snapshot.RunId.Value,
            snapshot.ProductionUnitId.Value,
            snapshot.ProductionLineDefinitionId,
            operation.Definition.OperationId,
            operation.OperationRunId,
            operation.Attempt,
            operation.Definition.StationSystemId,
            operation.Definition.StationId.Value,
            operation.Definition.ProcessDefinitionId.Value,
            operation.Definition.ProcessVersionId.Value,
            operation.Definition.ConfigurationSnapshotId.Value,
            operation.Definition.RecipeSnapshotId.Value,
            snapshot.ProductionUnitIdentity.ModelId,
            snapshot.ProductionUnitIdentity.InputKey,
            snapshot.ProductionUnitIdentity.Value,
            snapshot.LotId,
            snapshot.CarrierId,
            FindResource(operation, ResourceKind.Fixture),
            FindResource(operation, ResourceKind.Device),
            snapshot.ActorId,
            snapshot.ProjectId,
            snapshot.ApplicationId,
            snapshot.ProjectSnapshotId,
            snapshot.TopologyId,
            executionStatus switch
            {
                ExecutionStatus.Completed => "Completed",
                ExecutionStatus.Canceled => "Canceled",
                _ => "Failed"
            },
            completedAtUtc,
            operation.FencingTokens.Select(pair => new OperationResourceFenceEvidence(
                pair.Key.Kind.ToString(),
                pair.Key.ResourceId,
                pair.Value,
                completedAtUtc.AddMinutes(5))).ToArray(),
            steps,
            commands,
            incidents,
            []);
    }

    private static string? FindResource(OperationRunSnapshot operation, ResourceKind kind) =>
        operation.Definition.ResourceRequirements
            .FirstOrDefault(requirement => requirement.Kind == kind)?.ResourceId;

    private static Guid Id(string operationRunId, string kind, int index)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{operationRunId}/{kind}/{index}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
