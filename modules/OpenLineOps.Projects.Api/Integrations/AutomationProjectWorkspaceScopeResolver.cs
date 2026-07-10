using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Projects.Application.Persistence;
using OpenLineOps.Projects.Domain.Identifiers;

namespace OpenLineOps.Projects.Api.Integrations;

public sealed class AutomationProjectWorkspaceScopeResolver : IProjectApplicationWorkspaceScopeResolver
{
    private readonly IAutomationProjectRepository _projectRepository;

    public AutomationProjectWorkspaceScopeResolver(IAutomationProjectRepository projectRepository)
    {
        _projectRepository = projectRepository;
    }

    public async ValueTask<ProjectApplicationWorkspaceScope?> ResolveAsync(
        string projectId,
        string applicationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(applicationId))
        {
            return null;
        }

        var project = await _projectRepository
            .GetByIdAsync(new AutomationProjectId(projectId), cancellationToken)
            .ConfigureAwait(false);
        var application = project?.Applications.SingleOrDefault(candidate =>
            candidate.Id.Value == applicationId);
        if (project is null || application is null)
        {
            return null;
        }

        return new ProjectApplicationWorkspaceScope(
            project.Id.Value,
            applicationId,
            project.ProjectPath,
            application.ProjectFilePath);
    }
}
