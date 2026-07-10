using OpenLineOps.Application.Abstractions.Results;

namespace OpenLineOps.Production.Application.LineDefinitions;

public interface IProjectProductionLineDefinitionService
{
    Task<Result<ProductionLineDefinitionDetails>> CreateAsync(
        string projectId,
        string applicationId,
        SaveProductionLineDefinitionRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<ProductionLineDefinitionDetails>> ReplaceAsync(
        string projectId,
        string applicationId,
        string lineDefinitionId,
        SaveProductionLineDefinitionRequest request,
        CancellationToken cancellationToken = default);

    Task<Result<ProductionLineDefinitionDetails>> GetByIdAsync(
        string projectId,
        string applicationId,
        string lineDefinitionId,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyCollection<ProductionLineDefinitionSummary>>> ListAsync(
        string projectId,
        string applicationId,
        CancellationToken cancellationToken = default);
}
