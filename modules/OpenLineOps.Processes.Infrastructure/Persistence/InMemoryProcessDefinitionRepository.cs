using System.Collections.Concurrent;
using OpenLineOps.Processes.Application.Persistence;
using OpenLineOps.Processes.Domain.Definitions;
using OpenLineOps.Processes.Domain.Identifiers;

namespace OpenLineOps.Processes.Infrastructure.Persistence;

public sealed class InMemoryProcessDefinitionRepository : IProcessDefinitionRepository
{
    private readonly ConcurrentDictionary<ProcessDefinitionId, ProcessDefinition> _definitions = [];
    private int _saveCount;

    public int SaveCount => Volatile.Read(ref _saveCount);

    public ValueTask SaveAsync(ProcessDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();

        _definitions[definition.Id] = definition;
        Interlocked.Increment(ref _saveCount);

        return ValueTask.CompletedTask;
    }

    public ValueTask<ProcessDefinition?> GetByIdAsync(
        ProcessDefinitionId definitionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _definitions.TryGetValue(definitionId, out var definition);

        return ValueTask.FromResult(definition);
    }

    public ValueTask<IReadOnlyCollection<ProcessDefinition>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var definitions = _definitions.Values
            .OrderBy(definition => definition.Id.Value, StringComparer.Ordinal)
            .ToArray();

        return ValueTask.FromResult<IReadOnlyCollection<ProcessDefinition>>(definitions);
    }
}
