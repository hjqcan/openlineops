using OpenLineOps.Application.Abstractions.ProjectWorkspaces;
using OpenLineOps.Processes.Domain.Definitions;
using OpenLineOps.Processes.Domain.Identifiers;

namespace OpenLineOps.Processes.Application.Persistence;

public interface IProjectProcessDefinitionRepository
{
    ValueTask SaveAsync(
        ProjectApplicationWorkspaceScope scope,
        ProcessDefinition definition,
        CancellationToken cancellationToken = default);

    ValueTask<ProcessDefinition?> GetByIdAsync(
        ProjectApplicationWorkspaceScope scope,
        ProcessDefinitionId definitionId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<ProcessDefinition>> ListAsync(
        ProjectApplicationWorkspaceScope scope,
        CancellationToken cancellationToken = default);
}
