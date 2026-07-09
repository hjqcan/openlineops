namespace OpenLineOps.Engineering.Api.Models;

public sealed record CreateWorkspaceRequest(
    string? WorkspaceId,
    string? DisplayName);
