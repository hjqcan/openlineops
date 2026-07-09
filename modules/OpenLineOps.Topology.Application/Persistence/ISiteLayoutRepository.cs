using OpenLineOps.Topology.Domain.Identifiers;
using OpenLineOps.Topology.Domain.Layouts;

namespace OpenLineOps.Topology.Application.Persistence;

public interface ISiteLayoutRepository
{
    ValueTask SaveAsync(SiteLayout layout, CancellationToken cancellationToken = default);

    ValueTask<SiteLayout?> GetByIdAsync(
        SiteLayoutId layoutId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<SiteLayout>> ListByTopologyAsync(
        AutomationTopologyId topologyId,
        CancellationToken cancellationToken = default);
}
