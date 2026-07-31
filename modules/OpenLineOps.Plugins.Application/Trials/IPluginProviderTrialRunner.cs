using OpenLineOps.Application.Abstractions.ProjectWorkspaces;

namespace OpenLineOps.Plugins.Application.Trials;

public interface IPluginProviderTrialRunner
{
    ValueTask<PluginProviderTrialResult> ExecuteAsync(
        ProjectApplicationWorkspaceScope scope,
        PluginProviderTrialRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record PluginProviderTrialRequest(
    string ProviderKind,
    string ProviderKey,
    string Capability,
    string CommandName,
    string? InputPayload,
    int TimeoutMilliseconds);

public enum PluginProviderTrialOutcome
{
    Completed = 1,
    Failed = 2,
    TimedOut = 3,
    Canceled = 4,
    Rejected = 5
}

public sealed record PluginProviderTrialResult(
    PluginProviderTrialOutcome Outcome,
    string? ResultPayload,
    string? FailureReason);
