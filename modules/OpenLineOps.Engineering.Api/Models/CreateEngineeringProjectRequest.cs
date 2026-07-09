namespace OpenLineOps.Engineering.Api.Models;

public sealed record CreateEngineeringProjectRequest(
    string? ProjectId,
    string? WorkspaceId,
    string? DisplayName);
