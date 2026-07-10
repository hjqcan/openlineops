using OpenLineOps.Application.Abstractions.Results;

namespace OpenLineOps.Runtime.Application.Runs;

public interface IProductionRunRunner
{
    ValueTask<Result<ProductionRunRunResult>> RunAsync(
        StartProductionRunRequest request,
        CancellationToken cancellationToken = default);
}
