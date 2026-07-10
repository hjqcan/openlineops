namespace OpenLineOps.Application.Abstractions.ProjectWorkspaces;

public sealed record ProjectApplicationWorkspaceScope
{
    public ProjectApplicationWorkspaceScope(
        string projectId,
        string applicationId,
        string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new ArgumentException("Project id cannot be empty.", nameof(projectId));
        }

        if (string.IsNullOrWhiteSpace(applicationId))
        {
            throw new ArgumentException("Application id cannot be empty.", nameof(applicationId));
        }

        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new ArgumentException("Project path cannot be empty.", nameof(projectPath));
        }

        ProjectId = projectId.Trim();
        ApplicationId = applicationId.Trim();
        ProjectPath = Path.GetFullPath(projectPath.Trim());
    }

    public string ProjectId { get; }

    public string ApplicationId { get; }

    public string ProjectPath { get; }
}
