namespace OpenLineOps.Projects.Application.ProjectWorkspaces;

public interface IAutomationProjectManifestStore
{
    string GetManifestPath(string projectPath);

    ValueTask SaveAsync(
        AutomationProjectManifest manifest,
        CancellationToken cancellationToken = default);

    ValueTask<AutomationProjectManifest?> LoadAsync(
        string projectPath,
        CancellationToken cancellationToken = default);
}
