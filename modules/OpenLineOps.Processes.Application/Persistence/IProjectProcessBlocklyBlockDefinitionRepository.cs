using OpenLineOps.Application.Abstractions.ProjectWorkspaces;

namespace OpenLineOps.Processes.Application.Persistence;

public interface IProjectProcessBlocklyBlockDefinitionRepository
{
    ValueTask<IReadOnlyCollection<ProcessBlocklyBlockDefinitionRecord>> ListLatestAsync(
        ProjectApplicationWorkspaceScope scope,
        CancellationToken cancellationToken = default);

    ValueTask<ProcessBlocklyBlockDefinitionRecord?> GetLatestAsync(
        ProjectApplicationWorkspaceScope scope,
        string blockType,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<ProcessBlocklyBlockDefinitionRecord>> ListVersionsAsync(
        ProjectApplicationWorkspaceScope scope,
        string blockType,
        CancellationToken cancellationToken = default);

    ValueTask<ProcessBlocklyBlockDefinitionRecord> SaveNewVersionAsync(
        ProjectApplicationWorkspaceScope scope,
        string blockType,
        string category,
        string displayName,
        string blocklyJson,
        string executionMode,
        string runtimeActionContractSchemaVersion,
        string runtimeActionContractJson,
        string runtimeActionContractSha256,
        DateTimeOffset recordedAtUtc,
        CancellationToken cancellationToken = default);
}
