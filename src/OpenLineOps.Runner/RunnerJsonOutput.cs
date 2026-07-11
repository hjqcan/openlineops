using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Projects.Application.Projects;
using OpenLineOps.Projects.Application.ProjectWorkspaces;
using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runner;

public sealed record RunnerErrorOutput(string Code, string Message);

public sealed record RunnerProjectOutput(
    string ManifestPath,
    string ProjectId,
    string ApplicationId,
    string SnapshotId,
    string ReleaseContentSha256);

public sealed record RunnerResourceOutput(
    string Kind,
    string ResourceId,
    long? FencingToken);

public sealed record RunnerProductionContextOutput(
    string Kind,
    string CanonicalValue);

public sealed record RunnerOperationOutput(
    string OperationId,
    string OperationRunId,
    int Attempt,
    string StationSystemId,
    string StationId,
    string ProcessDefinitionId,
    string ProcessVersionId,
    string ConfigurationSnapshotId,
    string RecipeSnapshotId,
    string ExecutionStatus,
    string ResultJudgement,
    Guid? RuntimeSessionId,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? FailureCode,
    string? FailureReason,
    int CompletedStepCount,
    int CommandCount,
    int IncidentCount,
    IReadOnlyList<RunnerResourceOutput> Resources,
    IReadOnlyDictionary<string, RunnerProductionContextOutput> Outputs);

public sealed record RunnerRouteDecisionOutput(
    string SourceOperationRunId,
    string TransitionId,
    string TargetOperationId,
    string SourceJudgement,
    int Traversal,
    DateTimeOffset DecidedAtUtc);

public sealed record RunnerProductionRunOutput(
    Guid ProductionRunId,
    string ProductionLineDefinitionId,
    string ProductModelId,
    string ProductionUnitIdentityInputKey,
    string ProductionUnitIdentityValue,
    string? LotId,
    string? CarrierId,
    string ActorId,
    string ExecutionStatus,
    string ResultJudgement,
    string ProductDisposition,
    string ControlState,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? FailureCode,
    string? FailureReason,
    int CompletedOperationCount,
    int OperationCount,
    int CompletedStepCount,
    int CommandCount,
    int IncidentCount,
    IReadOnlyList<RunnerOperationOutput> Operations,
    IReadOnlyList<RunnerRouteDecisionOutput> RouteDecisions);

public sealed record RunnerJsonOutput(
    bool Success,
    string Command,
    int ExitCode,
    string Target,
    RunnerProjectOutput? Project,
    RunnerProductionRunOutput? ProductionRun,
    RunnerErrorOutput? Error)
{
    public static RunnerJsonOutput Succeeded(
        string target,
        AutomationProjectWorkspaceDetails workspace,
        PublishedProjectSnapshotDetails snapshot,
        ProductionRunSnapshot productionRun)
    {
        return new RunnerJsonOutput(
            Success: true,
            Command: "run",
            RunnerExitCodes.Success,
            target,
            ToProject(workspace, snapshot),
            ToProductionRun(productionRun),
            Error: null);
    }

    public static RunnerJsonOutput Failed(
        int exitCode,
        string target,
        string errorCode,
        string errorMessage,
        AutomationProjectWorkspaceDetails? workspace = null,
        PublishedProjectSnapshotDetails? snapshot = null,
        ProductionRunSnapshot? productionRun = null)
    {
        return new RunnerJsonOutput(
            Success: false,
            Command: "run",
            exitCode,
            target,
            workspace is not null && snapshot is not null
                ? ToProject(workspace, snapshot)
                : null,
            productionRun is null ? null : ToProductionRun(productionRun),
            new RunnerErrorOutput(errorCode, errorMessage));
    }

    private static RunnerProjectOutput ToProject(
        AutomationProjectWorkspaceDetails workspace,
        PublishedProjectSnapshotDetails snapshot)
    {
        return new RunnerProjectOutput(
            workspace.ManifestPath,
            snapshot.ProjectId,
            snapshot.ApplicationId,
            snapshot.SnapshotId,
            snapshot.ReleaseContentSha256);
    }

    private static RunnerProductionRunOutput ToProductionRun(ProductionRunSnapshot productionRun)
    {
        var operations = productionRun.Operations
            .OrderBy(operation => operation.OperationRunId, StringComparer.Ordinal)
            .Select(operation => new RunnerOperationOutput(
                operation.Definition.OperationId,
                operation.OperationRunId,
                operation.Attempt,
                operation.Definition.StationSystemId,
                operation.Definition.StationId.Value,
                operation.Definition.ProcessDefinitionId.Value,
                operation.Definition.ProcessVersionId.Value,
                operation.Definition.ConfigurationSnapshotId.Value,
                operation.Definition.RecipeSnapshotId.Value,
                operation.ExecutionStatus.ToString(),
                operation.Judgement.ToString(),
                operation.RuntimeSessionId?.Value,
                operation.StartedAtUtc,
                operation.CompletedAtUtc,
                operation.FailureCode,
                operation.FailureReason,
                operation.CompletedStepCount,
                operation.CommandCount,
                operation.IncidentCount,
                operation.Definition.ResourceRequirements
                    .Select(requirement => new RunnerResourceOutput(
                        requirement.Kind.ToString(),
                        requirement.ResourceId,
                        operation.FencingTokens.TryGetValue(requirement, out var token)
                            ? token
                            : null))
                    .ToArray(),
                operation.Outputs.ToDictionary(
                    static pair => pair.Key,
                    static pair => new RunnerProductionContextOutput(
                        pair.Value.Kind.ToString(),
                        pair.Value.CanonicalValue),
                    StringComparer.Ordinal)))
            .ToArray();
        var routeDecisions = productionRun.RouteDecisions
            .Select(decision => new RunnerRouteDecisionOutput(
                decision.SourceOperationRunId,
                decision.TransitionId,
                decision.TargetOperationId,
                decision.SourceJudgement.ToString(),
                decision.Traversal,
                decision.DecidedAtUtc))
            .ToArray();

        return new RunnerProductionRunOutput(
            productionRun.RunId.Value,
            productionRun.ProductionLineDefinitionId,
            productionRun.ProductionUnitIdentity.ModelId,
            productionRun.ProductionUnitIdentity.InputKey,
            productionRun.ProductionUnitIdentity.Value,
            productionRun.LotId,
            productionRun.CarrierId,
            productionRun.ActorId,
            productionRun.ExecutionStatus.ToString(),
            productionRun.Judgement.ToString(),
            productionRun.Disposition.ToString(),
            productionRun.ControlState.ToString(),
            productionRun.CreatedAtUtc,
            productionRun.StartedAtUtc,
            productionRun.CompletedAtUtc,
            productionRun.FailureCode,
            productionRun.FailureReason,
            operations.Count(operation => string.Equals(
                operation.ExecutionStatus,
                ExecutionStatus.Completed.ToString(),
                StringComparison.Ordinal)),
            operations.Length,
            operations.Sum(operation => operation.CompletedStepCount),
            operations.Sum(operation => operation.CommandCount),
            operations.Sum(operation => operation.IncidentCount),
            operations,
            routeDecisions);
    }
}

public static class RunnerJsonOutputWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static Task WriteAsync(TextWriter writer, RunnerJsonOutput output)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(output);

        return writer.WriteLineAsync(JsonSerializer.Serialize(output, JsonOptions));
    }
}
