using System.Text.Json;
using System.Text.Json.Serialization;
using OpenLineOps.Processes.Application.Runtime;
using OpenLineOps.Projects.Application.ProjectWorkspaces;
using OpenLineOps.Projects.Application.Projects;

namespace OpenLineOps.Runner;

public sealed record RunnerErrorOutput(string Code, string Message);

public sealed record RunnerProjectOutput(
    string ManifestPath,
    string ProjectId,
    string ApplicationId,
    string SnapshotId,
    string ReleaseContentSha256);

public sealed record RunnerSessionOutput(
    Guid SessionId,
    string ConfigurationSnapshotId,
    string Status,
    int CompletedSteps,
    int CommandCount,
    int IncidentCount);

public sealed record RunnerJsonOutput(
    int SchemaVersion,
    bool Success,
    string Command,
    int ExitCode,
    string Target,
    RunnerProjectOutput? Project,
    RunnerSessionOutput? Session,
    RunnerErrorOutput? Error)
{
    public const int CurrentSchemaVersion = 1;

    public static RunnerJsonOutput Succeeded(
        string target,
        AutomationProjectWorkspaceDetails workspace,
        PublishedProjectSnapshotDetails snapshot,
        StartedProcessRuntimeSessionDetails session)
    {
        return new RunnerJsonOutput(
            CurrentSchemaVersion,
            Success: true,
            Command: "run",
            RunnerExitCodes.Success,
            target,
            ToProject(workspace, snapshot),
            ToSession(session),
            Error: null);
    }

    public static RunnerJsonOutput Failed(
        int exitCode,
        string target,
        string errorCode,
        string errorMessage,
        AutomationProjectWorkspaceDetails? workspace = null,
        PublishedProjectSnapshotDetails? snapshot = null,
        StartedProcessRuntimeSessionDetails? session = null)
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
            session is null ? null : ToSession(session),
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

    private static RunnerSessionOutput ToSession(StartedProcessRuntimeSessionDetails session)
    {
        return new RunnerSessionOutput(
            session.SessionId,
            session.ConfigurationSnapshotId,
            session.Status,
            session.CompletedSteps,
            session.CommandCount,
            session.IncidentCount);
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
