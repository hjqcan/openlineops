namespace OpenLineOps.Processes.Application.Persistence;

public interface IProcessBlocklyBlockDefinitionRepository
{
    ValueTask<IReadOnlyCollection<ProcessBlocklyBlockDefinitionRecord>> ListLatestAsync(
        CancellationToken cancellationToken = default);

    ValueTask<ProcessBlocklyBlockDefinitionRecord?> GetLatestAsync(
        string blockType,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<ProcessBlocklyBlockDefinitionRecord>> ListVersionsAsync(
        string blockType,
        CancellationToken cancellationToken = default);

    ValueTask<ProcessBlocklyBlockDefinitionRecord> SaveNewVersionAsync(
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
