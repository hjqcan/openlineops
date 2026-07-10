using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Topology.Domain.Identifiers;
using OpenLineOps.Topology.Domain.Layouts;

namespace OpenLineOps.Topology.Application.Persistence;

public interface IProjectSiteLayoutRepository
{
    ValueTask SaveAsync(
        ProjectApplicationWorkspaceScope scope,
        SiteLayout layout,
        CancellationToken cancellationToken = default);

    ValueTask<SiteLayout?> GetByIdAsync(
        ProjectApplicationWorkspaceScope scope,
        SiteLayoutId layoutId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<SiteLayout>> ListByTopologyAsync(
        ProjectApplicationWorkspaceScope scope,
        AutomationTopologyId topologyId,
        CancellationToken cancellationToken = default);
}
