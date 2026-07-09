using System.Collections.Concurrent;
using OpenLineOps.Topology.Application.Persistence;
using OpenLineOps.Topology.Domain.Identifiers;
using OpenLineOps.Topology.Domain.Topology;

namespace OpenLineOps.Topology.Infrastructure.Persistence;

public sealed class InMemoryAutomationTopologyRepository : IAutomationTopologyRepository
{
    private readonly ConcurrentDictionary<AutomationTopologyId, AutomationTopology> _topologies = [];
    private int _saveCount;

    public int SaveCount => Volatile.Read(ref _saveCount);

    public ValueTask SaveAsync(AutomationTopology topology, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topology);
        cancellationToken.ThrowIfCancellationRequested();

        _topologies[topology.Id] = topology;
        Interlocked.Increment(ref _saveCount);

        return ValueTask.CompletedTask;
    }

    public ValueTask<AutomationTopology?> GetByIdAsync(
        AutomationTopologyId topologyId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _topologies.TryGetValue(topologyId, out var topology);

        return ValueTask.FromResult(topology);
    }

    public ValueTask<IReadOnlyCollection<AutomationTopology>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var topologies = _topologies.Values
            .OrderBy(topology => topology.Id.Value, StringComparer.Ordinal)
            .ToArray();

        return ValueTask.FromResult<IReadOnlyCollection<AutomationTopology>>(topologies);
    }
}
