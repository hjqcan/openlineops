using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Topology.Domain.Identifiers;
using OpenLineOps.Topology.Domain.Topology;

namespace OpenLineOps.Topology.Application.Persistence;

public interface IProjectAutomationTopologyRepository
{
    ValueTask SaveAsync(
        ProjectApplicationWorkspaceScope scope,
        AutomationTopology topology,
        CancellationToken cancellationToken = default);

    ValueTask<AutomationTopology?> GetByIdAsync(
        ProjectApplicationWorkspaceScope scope,
        AutomationTopologyId topologyId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<AutomationTopology>> ListAsync(
        ProjectApplicationWorkspaceScope scope,
        CancellationToken cancellationToken = default);
}
