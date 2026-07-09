namespace OpenLineOps.Engineering.Application.Configuration;

public sealed record CreateWorkspaceRequest(
    string WorkspaceId,
    string DisplayName);
