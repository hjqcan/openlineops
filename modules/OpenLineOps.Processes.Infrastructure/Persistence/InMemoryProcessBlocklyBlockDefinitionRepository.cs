using OpenLineOps.Processes.Application.Persistence;

namespace OpenLineOps.Processes.Infrastructure.Persistence;

public sealed class InMemoryProcessBlocklyBlockDefinitionRepository : IProcessBlocklyBlockDefinitionRepository
{
    private readonly Dictionary<string, List<ProcessBlocklyBlockDefinitionRecord>> _blocks =
        new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public ValueTask<IReadOnlyCollection<ProcessBlocklyBlockDefinitionRecord>> ListLatestAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var records = _blocks.Values
                .Select(versions => versions.OrderByDescending(block => block.Version).First())
                .OrderBy(block => block.BlockType, StringComparer.Ordinal)
                .ToArray();

            return ValueTask.FromResult<IReadOnlyCollection<ProcessBlocklyBlockDefinitionRecord>>(records);
        }
    }

    public ValueTask<ProcessBlocklyBlockDefinitionRecord?> GetLatestAsync(
        string blockType,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return ValueTask.FromResult(_blocks.TryGetValue(blockType, out var versions)
                ? versions.OrderByDescending(block => block.Version).First()
                : null);
        }
    }

    public ValueTask<IReadOnlyCollection<ProcessBlocklyBlockDefinitionRecord>> ListVersionsAsync(
        string blockType,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            var records = _blocks.TryGetValue(blockType, out var versions)
                ? versions.OrderByDescending(block => block.Version).ToArray()
                : [];

            return ValueTask.FromResult<IReadOnlyCollection<ProcessBlocklyBlockDefinitionRecord>>(records);
        }
    }

    public ValueTask<ProcessBlocklyBlockDefinitionRecord> SaveNewVersionAsync(
        string blockType,
        string category,
        string displayName,
        string blocklyJson,
        string pythonCodeTemplate,
        DateTimeOffset recordedAtUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_blocks.TryGetValue(blockType, out var versions))
            {
                versions = [];
                _blocks[blockType] = versions;
            }

            var latest = versions.OrderByDescending(block => block.Version).FirstOrDefault();
            var record = new ProcessBlocklyBlockDefinitionRecord(
                blockType,
                category,
                displayName,
                blocklyJson,
                pythonCodeTemplate,
                latest?.Version + 1 ?? 1,
                latest?.CreatedAtUtc ?? recordedAtUtc,
                recordedAtUtc);

            versions.Add(record);

            return ValueTask.FromResult(record);
        }
    }
}
