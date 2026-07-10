using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Projects.Application.Projects;
using OpenLineOps.Projects.Application.ProjectWorkspaces;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runner;

public sealed record RunnerErrorOutput(string Code, string Message);

public sealed record RunnerProjectOutput(
    string ManifestPath,
    string ProjectId,
    string ApplicationId,
    string SnapshotId,
    string ReleaseContentSha256);

public sealed record RunnerProductionStageOutput(
    string StageId,
    int Sequence,
    string WorkstationId,
    string StationId,
    string ProcessDefinitionId,
    string ProcessVersionId,
    string ConfigurationSnapshotId,
    string Status,
    Guid? RuntimeSessionId,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? FailureCode,
    string? FailureReason,
    int CompletedStepCount,
    int CommandCount,
    int IncidentCount);

public sealed record RunnerProductionRunOutput(
    Guid ProductionRunId,
    string ProductionLineDefinitionId,
    string DutModelId,
    string DutIdentityInputKey,
    string DutIdentityValue,
    string ActorId,
    string? BatchId,
    string? FixtureId,
    string? DeviceId,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? FailureCode,
    string? FailureReason,
    int CompletedStageCount,
    int StageCount,
    int CompletedStepCount,
    int CommandCount,
    int IncidentCount,
    IReadOnlyList<RunnerProductionStageOutput> Stages);

public sealed record RunnerJsonOutput(
    int SchemaVersion,
    bool Success,
    string Command,
    int ExitCode,
    string Target,
    RunnerProjectOutput? Project,
    RunnerProductionRunOutput? ProductionRun,
    RunnerErrorOutput? Error)
{
    public const int CurrentSchemaVersion = 1;

    public static RunnerJsonOutput Succeeded(
        string target,
        AutomationProjectWorkspaceDetails workspace,
        PublishedProjectSnapshotDetails snapshot,
        ProductionRunSnapshot productionRun)
    {
        return new RunnerJsonOutput(
            CurrentSchemaVersion,
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
            CurrentSchemaVersion,
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
        var stages = productionRun.Stages
            .OrderBy(stage => stage.Sequence)
            .Select(stage => new RunnerProductionStageOutput(
                stage.StageId,
                stage.Sequence,
                stage.WorkstationId,
                stage.StationId.Value,
                stage.ProcessDefinitionId.Value,
                stage.ProcessVersionId.Value,
                stage.ConfigurationSnapshotId.Value,
                stage.Status.ToString(),
                stage.RuntimeSessionId?.Value,
                stage.StartedAtUtc,
                stage.CompletedAtUtc,
                stage.FailureCode,
                stage.FailureReason,
                stage.CompletedStepCount,
                stage.CommandCount,
                stage.IncidentCount))
            .ToArray();

        return new RunnerProductionRunOutput(
            productionRun.RunId.Value,
            productionRun.ProductionLineDefinitionId,
            productionRun.DutIdentity.ModelId,
            productionRun.DutIdentity.InputKey,
            productionRun.DutIdentity.Value,
            productionRun.ActorId,
            productionRun.BatchId,
            productionRun.FixtureId,
            productionRun.DeviceId,
            productionRun.Status.ToString(),
            productionRun.CreatedAtUtc,
            productionRun.StartedAtUtc,
            productionRun.CompletedAtUtc,
            productionRun.FailureCode,
            productionRun.FailureReason,
            stages.Count(stage => string.Equals(
                stage.Status,
                ProductionStageRunStatus.Completed.ToString(),
                StringComparison.Ordinal)),
            stages.Length,
            stages.Sum(stage => stage.CompletedStepCount),
            stages.Sum(stage => stage.CommandCount),
            stages.Sum(stage => stage.IncidentCount),
            stages);
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
