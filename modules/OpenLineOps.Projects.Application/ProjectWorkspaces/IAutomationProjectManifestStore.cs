namespace OpenLineOps.Projects.Application.ProjectWorkspaces;

public interface IAutomationProjectManifestStore
{
    string GetProjectRootPath(string projectTarget);

    string GetManifestPath(string projectTarget, string? projectId = null);

    ValueTask SaveAsync(
        AutomationProjectManifest manifest,
        CancellationToken cancellationToken = default);

    ValueTask<AutomationProjectManifest?> LoadAsync(
        string projectTarget,
        CancellationToken cancellationToken = default);

    ValueTask<ProjectApplicationManifest?> LoadApplicationProjectAsync(
        string projectRootPath,
        string applicationProjectTarget,
        CancellationToken cancellationToken = default);
}
