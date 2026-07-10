namespace OpenLineOps.Application.Abstractions.ProjectWorkspaces;

public interface IProjectApplicationWorkspaceScopeResolver
{
    ValueTask<ProjectApplicationWorkspaceScope?> ResolveAsync(
        string projectId,
        string applicationId,
        CancellationToken cancellationToken = default);
}
