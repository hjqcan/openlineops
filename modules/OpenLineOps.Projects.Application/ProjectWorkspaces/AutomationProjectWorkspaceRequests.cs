namespace OpenLineOps.Projects.Application.ProjectWorkspaces;

public sealed record CreateAutomationProjectWorkspaceRequest(
    string ProjectId,
    string DisplayName,
    string ProjectPath,
    string? DefaultApplicationId,
    string? DefaultApplicationName);

public sealed record OpenAutomationProjectWorkspaceRequest(string ProjectPath);

public sealed record ImportAutomationProjectApplicationRequest(string ProjectFilePath);
