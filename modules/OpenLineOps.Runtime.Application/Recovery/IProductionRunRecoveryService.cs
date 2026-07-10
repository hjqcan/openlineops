namespace OpenLineOps.Runtime.Application.Recovery;

public interface IProductionRunRecoveryService
{
    ValueTask<ProductionRunRecoveryResult> RecoverAsync(
        CancellationToken cancellationToken = default);
}

public sealed record ProductionRunRecoveryResult(
    int CanceledRunCount,
    int FailedRunCount,
    int CompletedRunCount)
{
    public int TotalRecoveredRuns => CanceledRunCount + FailedRunCount + CompletedRunCount;
}
