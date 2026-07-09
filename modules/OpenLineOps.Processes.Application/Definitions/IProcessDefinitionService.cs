using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Processes.Application.Validation;

namespace OpenLineOps.Processes.Application.Definitions;

public interface IProcessDefinitionService
{
    Task<Result<ProcessDefinitionDetails>> CreateAsync(
        CreateProcessDefinitionRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<ProcessDefinitionDetails>> GetByIdAsync(
        string processDefinitionId,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyCollection<ProcessDefinitionSummary>>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<Result<ProcessGraphValidationReportDetails>> ValidateAsync(
        string processDefinitionId,
        CancellationToken cancellationToken = default);

    Task<Result<ProcessDefinitionDetails>> PublishAsync(
        string processDefinitionId,
        CancellationToken cancellationToken = default);
}
