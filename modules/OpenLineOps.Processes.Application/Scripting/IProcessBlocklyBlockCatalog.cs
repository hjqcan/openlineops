using OpenLineOps.Application.Abstractions.Results;

namespace OpenLineOps.Processes.Application.Scripting;

public interface IProcessBlocklyBlockCatalog
{
    Task<Result<IReadOnlyCollection<ProcessBlocklyBlockDefinitionDetails>>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyCollection<ProcessBlocklyBlockDefinitionDetails>>> ListVersionsAsync(
        string blockType,
        CancellationToken cancellationToken = default);

    Task<Result<ProcessBlocklyBlockDefinitionDetails>> RegisterAsync(
        RegisterProcessBlocklyBlockDefinitionRequest request,
        CancellationToken cancellationToken = default);
}
