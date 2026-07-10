using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Processes.Application.Definitions;
using OpenLineOps.Processes.Application.Validation;

namespace OpenLineOps.Processes.Application.ProjectWorkspaces;

public interface IProjectProcessDefinitionService
{
    Task<Result<ProcessDefinitionDetails>> CreateAsync(
        string projectId,
        string applicationId,
        CreateProcessDefinitionRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<ProcessDefinitionDetails>> ReplaceDraftAsync(
        string projectId,
        string applicationId,
        string processDefinitionId,
        CreateProcessDefinitionRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<ProcessDefinitionDetails>> GetByIdAsync(
        string projectId,
        string applicationId,
        string processDefinitionId,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyCollection<ProcessDefinitionSummary>>> ListAsync(
        string projectId,
        string applicationId,
        CancellationToken cancellationToken = default);

    Task<Result<ProcessGraphValidationReportDetails>> ValidateAsync(
        string projectId,
        string applicationId,
        string processDefinitionId,
        CancellationToken cancellationToken = default);

    Task<Result<ProcessDefinitionDetails>> PublishAsync(
        string projectId,
        string applicationId,
        string processDefinitionId,
        CancellationToken cancellationToken = default);
}
