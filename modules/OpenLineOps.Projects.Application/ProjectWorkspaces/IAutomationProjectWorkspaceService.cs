using OpenLineOps.Application.Abstractions.Results;

namespace OpenLineOps.Projects.Application.ProjectWorkspaces;

public interface IAutomationProjectWorkspaceService
{
    Task<Result<AutomationProjectWorkspaceDetails>> CreateAsync(
        CreateAutomationProjectWorkspaceRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<AutomationProjectWorkspaceDetails>> OpenAsync(
        OpenAutomationProjectWorkspaceRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<AutomationProjectWorkspaceDetails>> SaveManifestAsync(
        string projectId,
        CancellationToken cancellationToken = default);

    Task<Result<AutomationProjectWorkspaceDetails>> ImportApplicationAsync(
        string projectId,
        ImportAutomationProjectApplicationRequest request,
        CancellationToken cancellationToken = default);
}
