namespace OpenLineOps.Runtime.Application.Recovery;

public interface IRuntimeSessionRecoveryService
{
    ValueTask<RuntimeSessionRecoveryPlan> CreateRecoveryPlanAsync(
        CancellationToken cancellationToken = default);
}
