using System.Collections.Concurrent;
using OpenLineOps.Engineering.Application.Persistence;
using OpenLineOps.Engineering.Domain.Identifiers;
using OpenLineOps.Engineering.Domain.Workspaces;

namespace OpenLineOps.Engineering.Infrastructure.Persistence;

public sealed class InMemoryWorkspaceRepository : IWorkspaceRepository
{
    private readonly ConcurrentDictionary<string, Workspace> _workspaces = new(StringComparer.Ordinal);

    public Task SaveAsync(Workspace workspace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        cancellationToken.ThrowIfCancellationRequested();

        _workspaces[workspace.Id.Value] = workspace;

        return Task.CompletedTask;
    }

    public Task<Workspace?> GetByIdAsync(
        WorkspaceId workspaceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspaceId);
        cancellationToken.ThrowIfCancellationRequested();

        _workspaces.TryGetValue(workspaceId.Value, out var workspace);

        return Task.FromResult(workspace);
    }

    public Task<IReadOnlyCollection<Workspace>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<IReadOnlyCollection<Workspace>>(_workspaces.Values.ToArray());
    }
}
