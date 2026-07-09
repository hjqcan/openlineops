using OpenLineOps.Application.Abstractions.Results;

namespace OpenLineOps.Projects.Application.Projects;

public interface IAutomationProjectService
{
    Task<Result<AutomationProjectDetails>> CreateAsync(
        CreateAutomationProjectRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<AutomationProjectDetails>> GetByIdAsync(
        string projectId,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyCollection<AutomationProjectSummary>>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<Result<AutomationProjectDetails>> AddApplicationAsync(
        string projectId,
        AddProjectApplicationRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<AutomationProjectDetails>> LinkTopologyAsync(
        string projectId,
        LinkProjectTopologyRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<AutomationProjectDetails>> LinkProcessDefinitionAsync(
        string projectId,
        LinkProjectProcessDefinitionRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<AutomationProjectDetails>> PublishSnapshotAsync(
        string projectId,
        PublishProjectSnapshotRequest request,
        CancellationToken cancellationToken = default);
}
