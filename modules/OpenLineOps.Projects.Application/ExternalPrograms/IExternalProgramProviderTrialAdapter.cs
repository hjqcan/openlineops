using OpenLineOps.Application.Abstractions.ProjectWorkspaces;

namespace OpenLineOps.Projects.Application.ExternalPrograms;

public interface IExternalProgramProviderTrialAdapter
{
    string ProviderKind { get; }

    string ProviderKey { get; }

    ValueTask<ExternalProgramProviderTrialExecutionResult> ExecuteAsync(
        ExternalProgramProviderTrialExecutionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ExternalProgramProviderTrialExecutionRequest(
    ProjectApplicationWorkspaceScope Scope,
    ExternalProgramResource Resource,
    string InvocationPayload,
    IReadOnlyCollection<string> Arguments,
    TimeSpan Timeout);

public enum ExternalProgramProviderTrialExecutionStatus
{
    Completed = 1,
    Failed = 2,
    TimedOut = 3,
    Canceled = 4,
    Rejected = 5
}

public sealed record ExternalProgramProviderTrialExecutionResult(
    ExternalProgramProviderTrialExecutionStatus ExecutionStatus,
    string? ResultPayload,
    string? FailureReason,
    IReadOnlyCollection<ExternalProgramTrialArtifact> Artifacts);
