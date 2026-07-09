using System.Collections.Concurrent;
using OpenLineOps.Topology.Application.Persistence;
using OpenLineOps.Topology.Domain.Identifiers;
using OpenLineOps.Topology.Domain.Layouts;

namespace OpenLineOps.Topology.Infrastructure.Persistence;

public sealed class InMemorySiteLayoutRepository : ISiteLayoutRepository
{
    private readonly ConcurrentDictionary<SiteLayoutId, SiteLayout> _layouts = [];
    private int _saveCount;

    public int SaveCount => Volatile.Read(ref _saveCount);

    public ValueTask SaveAsync(SiteLayout layout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        cancellationToken.ThrowIfCancellationRequested();

        _layouts[layout.Id] = layout;
        Interlocked.Increment(ref _saveCount);

        return ValueTask.CompletedTask;
    }

    public ValueTask<SiteLayout?> GetByIdAsync(
        SiteLayoutId layoutId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _layouts.TryGetValue(layoutId, out var layout);

        return ValueTask.FromResult(layout);
    }

    public ValueTask<IReadOnlyCollection<SiteLayout>> ListByTopologyAsync(
        AutomationTopologyId topologyId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var layouts = _layouts.Values
            .Where(layout => layout.TopologyId == topologyId)
            .OrderBy(layout => layout.Id.Value, StringComparer.Ordinal)
            .ToArray();

        return ValueTask.FromResult<IReadOnlyCollection<SiteLayout>>(layouts);
    }
}
