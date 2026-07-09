using OpenLineOps.Processes.Domain.Definitions;
using OpenLineOps.Processes.Domain.Identifiers;

namespace OpenLineOps.Processes.Application.Persistence;

public interface IProcessDefinitionRepository
{
    ValueTask SaveAsync(ProcessDefinition definition, CancellationToken cancellationToken = default);

    ValueTask<ProcessDefinition?> GetByIdAsync(
        ProcessDefinitionId definitionId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<ProcessDefinition>> ListAsync(CancellationToken cancellationToken = default);
}
