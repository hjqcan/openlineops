namespace OpenLineOps.Engineering.Application.Configuration;

public sealed record CreateEngineeringProjectRequest(
    string ProjectId,
    string WorkspaceId,
    string DisplayName);
