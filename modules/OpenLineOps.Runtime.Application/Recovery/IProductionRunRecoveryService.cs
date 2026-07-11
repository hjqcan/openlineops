namespace OpenLineOps.Runtime.Application.Recovery;

public interface IProductionRunRecoveryService
{
    ValueTask<ProductionRunRecoveryResult> RecoverAsync(
        CancellationToken cancellationToken = default);
}

public sealed record ProductionRunRecoveryResult(
    int PendingRunCount,
    int ResumableRunCount,
    int RecoveryRequiredRunCount)
{
    public int TotalInspectedRuns => PendingRunCount + ResumableRunCount + RecoveryRequiredRunCount;
}
