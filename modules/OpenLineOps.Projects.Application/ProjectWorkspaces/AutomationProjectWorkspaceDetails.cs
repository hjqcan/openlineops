using OpenLineOps.Projects.Application.Projects;

namespace OpenLineOps.Projects.Application.ProjectWorkspaces;

public sealed record AutomationProjectWorkspaceDetails(
    AutomationProjectDetails Project,
    string ManifestPath,
    AutomationProjectManifest Manifest);
