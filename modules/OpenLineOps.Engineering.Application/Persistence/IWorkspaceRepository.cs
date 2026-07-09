using OpenLineOps.Engineering.Domain.Identifiers;
using OpenLineOps.Engineering.Domain.Workspaces;

namespace OpenLineOps.Engineering.Application.Persistence;

public interface IWorkspaceRepository
{
    Task SaveAsync(Workspace workspace, CancellationToken cancellationToken = default);

    Task<Workspace?> GetByIdAsync(
        WorkspaceId workspaceId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<Workspace>> ListAsync(
        CancellationToken cancellationToken = default);
}
