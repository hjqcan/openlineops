using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Production.Domain.Aggregates;
using OpenLineOps.Production.Domain.Identifiers;

namespace OpenLineOps.Production.Application.Persistence;

public interface IProjectProductionLineDefinitionRepository
{
    ValueTask SaveAsync(
        ProjectApplicationWorkspaceScope scope,
        ProductionLineDefinition definition,
        CancellationToken cancellationToken = default);

    ValueTask<ProductionLineDefinition?> GetByIdAsync(
        ProjectApplicationWorkspaceScope scope,
        ProductionLineDefinitionId definitionId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<ProductionLineDefinition>> ListAsync(
        ProjectApplicationWorkspaceScope scope,
        CancellationToken cancellationToken = default);
}
