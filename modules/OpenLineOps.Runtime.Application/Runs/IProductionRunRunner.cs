using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Runtime.Domain.Identifiers;

namespace OpenLineOps.Runtime.Application.Runs;

public interface IProductionRunRunner
{
    ValueTask<Result<ProductionRunRunResult>> ExecuteAsync(
        ProductionRunId runId,
        CancellationToken cancellationToken = default);
}
