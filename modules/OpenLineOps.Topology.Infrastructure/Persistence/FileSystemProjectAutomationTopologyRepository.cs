using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Topology.Application.Persistence;
using OpenLineOps.Topology.Domain.Identifiers;
using OpenLineOps.Topology.Domain.Topology;

namespace OpenLineOps.Topology.Infrastructure.Persistence;

public sealed class FileSystemProjectAutomationTopologyRepository : IProjectAutomationTopologyRepository
{
    public ValueTask SaveAsync(
        ProjectApplicationWorkspaceScope scope,
        AutomationTopology topology,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(topology);
        cancellationToken.ThrowIfCancellationRequested();

        var path = ProjectTopologyResourcePath.GetTopologyPath(scope, topology.Id.Value);
        var document = ProjectTopologyResourceSnapshotMapper.FromTopology(scope, topology);

        return ProjectTopologyResourceJsonStore.SaveAsync(path, document, cancellationToken);
    }

    public async ValueTask<AutomationTopology?> GetByIdAsync(
        ProjectApplicationWorkspaceScope scope,
        AutomationTopologyId topologyId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();

        var path = ProjectTopologyResourcePath.GetTopologyPath(scope, topologyId.Value);
        var document = await ProjectTopologyResourceJsonStore
            .LoadAsync<ProjectAutomationTopologyDocument>(path, cancellationToken)
            .ConfigureAwait(false);

        if (document is null)
        {
            return null;
        }

        if (!string.Equals(document.TopologyId, topologyId.Value, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Topology resource '{path}' contains id {document.TopologyId}, not {topologyId.Value}.");
        }

        return ProjectTopologyResourceSnapshotMapper.ToTopology(scope, document);
    }

    public async ValueTask<IReadOnlyCollection<AutomationTopology>> ListAsync(
        ProjectApplicationWorkspaceScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();

        var directory = ProjectTopologyResourcePath.GetTopologyDirectory(scope);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var topologies = new List<AutomationTopology>();
        foreach (var path in Directory.EnumerateFiles(directory, "topology-*.json", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = await ProjectTopologyResourceJsonStore
                .LoadAsync<ProjectAutomationTopologyDocument>(path, cancellationToken)
                .ConfigureAwait(false);
            if (document is null)
            {
                throw new InvalidDataException($"Topology resource '{path}' is empty.");
            }

            topologies.Add(ProjectTopologyResourceSnapshotMapper.ToTopology(scope, document));
        }

        return topologies;
    }
}
