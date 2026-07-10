using OpenLineOps.Topology.Application.Persistence;
using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Topology.Domain.Identifiers;
using OpenLineOps.Topology.Domain.Layouts;

namespace OpenLineOps.Topology.Infrastructure.Persistence;

public sealed class FileSystemProjectSiteLayoutRepository : IProjectSiteLayoutRepository
{
    public ValueTask SaveAsync(
        ProjectApplicationWorkspaceScope scope,
        SiteLayout layout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(layout);
        cancellationToken.ThrowIfCancellationRequested();

        var path = ProjectTopologyResourcePath.GetLayoutPath(scope, layout.Id.Value);
        var document = ProjectTopologyResourceSnapshotMapper.FromLayout(scope, layout);

        return ProjectTopologyResourceJsonStore.SaveAsync(path, document, cancellationToken);
    }

    public async ValueTask<SiteLayout?> GetByIdAsync(
        ProjectApplicationWorkspaceScope scope,
        SiteLayoutId layoutId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();

        var path = ProjectTopologyResourcePath.GetLayoutPath(scope, layoutId.Value);
        var document = await ProjectTopologyResourceJsonStore
            .LoadAsync<ProjectSiteLayoutDocument>(path, cancellationToken)
            .ConfigureAwait(false);

        if (document is null)
        {
            return null;
        }

        if (!string.Equals(document.LayoutId, layoutId.Value, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Layout resource '{path}' contains id {document.LayoutId}, not {layoutId.Value}.");
        }

        return ProjectTopologyResourceSnapshotMapper.ToLayout(scope, document);
    }

    public async ValueTask<IReadOnlyCollection<SiteLayout>> ListByTopologyAsync(
        ProjectApplicationWorkspaceScope scope,
        AutomationTopologyId topologyId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();

        var directory = ProjectTopologyResourcePath.GetLayoutDirectory(scope);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var layouts = new List<SiteLayout>();
        foreach (var path in Directory.EnumerateFiles(directory, "layout-*.json", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = await ProjectTopologyResourceJsonStore
                .LoadAsync<ProjectSiteLayoutDocument>(path, cancellationToken)
                .ConfigureAwait(false);
            if (document is null)
            {
                throw new InvalidDataException($"Layout resource '{path}' is empty.");
            }

            var layout = ProjectTopologyResourceSnapshotMapper.ToLayout(scope, document);
            if (layout.TopologyId == topologyId)
            {
                layouts.Add(layout);
            }
        }

        return layouts;
    }
}
