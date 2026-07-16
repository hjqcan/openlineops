using OpenLineOps.Runtime.Contracts;
using OpenLineOps.Runtime.Domain.Resources;
using OpenLineOps.Runtime.Domain.Runs;

namespace OpenLineOps.Runtime.Application.Persistence;

public interface IProductionRunSafetyTransitionStore
{
    ValueTask<long> SaveWithLeaseHoldsAsync(
        ProductionRun run,
        long expectedRevision,
        IReadOnlyCollection<ProductionRunLeaseHold> leaseHolds,
        CancellationToken cancellationToken = default);
}

public sealed record ProductionRunLeaseHold
{
    public ProductionRunLeaseHold(
        string operationRunId,
        IReadOnlyCollection<ResourceLeaseHoldClaim> claims)
    {
        OperationRunId = string.IsNullOrWhiteSpace(operationRunId)
            || char.IsWhiteSpace(operationRunId[0])
            || char.IsWhiteSpace(operationRunId[^1])
                ? throw new ArgumentException(
                    "Operation Run id must be a non-empty canonical string.",
                    nameof(operationRunId))
                : operationRunId;
        ArgumentNullException.ThrowIfNull(claims);
        var suppliedClaims = claims.ToArray();
        if (suppliedClaims.Length == 0
            || suppliedClaims.Any(static claim => claim is null)
            || suppliedClaims.Select(static claim => claim.Resource).Distinct().Count()
                != suppliedClaims.Length)
        {
            throw new ArgumentException(
                "A Production Run lease hold requires non-empty unique exact fencing claims.",
                nameof(claims));
        }

        var canonicalClaims = suppliedClaims
            .OrderBy(static claim => claim.Resource.CanonicalKey, StringComparer.Ordinal)
            .ToArray();
        Claims = canonicalClaims;
    }

    public string OperationRunId { get; }

    public IReadOnlyList<ResourceLeaseHoldClaim> Claims { get; }

    public static ProductionRunLeaseHold[] RequireExactFor(
        ProductionRun run,
        IReadOnlyCollection<ProductionRunLeaseHold> leaseHolds)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(leaseHolds);
        var suppliedHolds = leaseHolds.ToArray();
        if (suppliedHolds.Length == 0
            || suppliedHolds.Any(static hold => hold is null))
        {
            throw new ArgumentException(
                "A protected Production Run transition requires unique Operation lease holds.",
                nameof(leaseHolds));
        }

        var canonical = suppliedHolds
            .OrderBy(static hold => hold.OperationRunId, StringComparer.Ordinal)
            .ToArray();
        if (canonical.Select(static hold => hold.OperationRunId).Distinct(StringComparer.Ordinal).Count()
            != canonical.Length)
        {
            throw new ArgumentException(
                "A protected Production Run transition requires unique Operation lease holds.",
                nameof(leaseHolds));
        }

        var operations = run.Operations.ToDictionary(
            static operation => operation.OperationRunId,
            StringComparer.Ordinal);
        foreach (var hold in canonical)
        {
            if (!operations.TryGetValue(hold.OperationRunId, out var operation))
            {
                throw new ArgumentException(
                    $"Lease hold references unknown Operation Run '{hold.OperationRunId}'.",
                    nameof(leaseHolds));
            }

            if (operation.ExecutionStatus != ExecutionStatus.Running)
            {
                throw new ArgumentException(
                    $"Lease hold references non-running Operation Run '{hold.OperationRunId}'.",
                    nameof(leaseHolds));
            }

            var exact = operation.FencingTokens.Count == hold.Claims.Count
                && hold.Claims.All(claim => operation.FencingTokens.TryGetValue(
                        claim.Resource,
                        out var token)
                    && token == claim.FencingToken);
            if (!exact)
            {
                throw new ArgumentException(
                    $"Lease hold for Operation Run '{hold.OperationRunId}' is not its full exact fencing set.",
                    nameof(leaseHolds));
            }
        }

        var suppliedOperationIds = canonical
            .Select(static hold => hold.OperationRunId)
            .ToHashSet(StringComparer.Ordinal);
        var missingActive = run.Operations.FirstOrDefault(operation =>
            operation.ExecutionStatus == ExecutionStatus.Running
            && !suppliedOperationIds.Contains(operation.OperationRunId));
        if (missingActive is not null)
        {
            throw new ArgumentException(
                $"Protected transition omitted active Operation Run '{missingActive.OperationRunId}'.",
                nameof(leaseHolds));
        }

        return canonical;
    }
}
