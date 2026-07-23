using OpenLineOps.Application.Abstractions.ProjectWorkspaces;

namespace OpenLineOps.Processes.Application.Scripting;

public interface IProcessBlocklyBlockCatalogSource
{
    ValueTask<IReadOnlyCollection<ProcessBlocklyBlockDefinitionDetails>> ListAsync(
        ProjectApplicationWorkspaceScope scope,
        CancellationToken cancellationToken = default);
}
