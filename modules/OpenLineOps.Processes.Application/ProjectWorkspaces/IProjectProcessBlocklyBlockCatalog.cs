using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Processes.Application.Scripting;

namespace OpenLineOps.Processes.Application.ProjectWorkspaces;

public interface IProjectProcessBlocklyBlockCatalog
{
    Task<Result<IReadOnlyCollection<ProcessBlocklyBlockDefinitionDetails>>> ListAsync(
        string projectId,
        string applicationId,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyCollection<ProcessBlocklyBlockDefinitionDetails>>> ListVersionsAsync(
        string projectId,
        string applicationId,
        string blockType,
        CancellationToken cancellationToken = default);

    Task<Result<ProcessBlocklyBlockDefinitionDetails>> RegisterAsync(
        string projectId,
        string applicationId,
        RegisterProcessBlocklyBlockDefinitionRequest request,
        CancellationToken cancellationToken = default);
}
