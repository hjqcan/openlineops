using OpenLineOps.Topology.Domain.Identifiers;
using OpenLineOps.Topology.Domain.Topology;

namespace OpenLineOps.Topology.Application.Persistence;

public interface IAutomationTopologyRepository
{
    ValueTask SaveAsync(AutomationTopology topology, CancellationToken cancellationToken = default);

    ValueTask<AutomationTopology?> GetByIdAsync(
        AutomationTopologyId topologyId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<AutomationTopology>> ListAsync(
        CancellationToken cancellationToken = default);
}
