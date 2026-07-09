namespace OpenLineOps.Processes.Application.Scripting;

public interface IProcessBlocklyBlockCatalogSource
{
    ValueTask<IReadOnlyCollection<ProcessBlocklyBlockDefinitionDetails>> ListAsync(
        CancellationToken cancellationToken = default);
}
