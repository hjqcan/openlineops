using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Resources;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

internal sealed class ProductionRunLeaseHoldSet
{
    private readonly HashSet<string> _operationRunIds;

    private ProductionRunLeaseHoldSet(
        ProductionRunLeaseHoldClaim[] claims,
        string operationIdentity,
        IEnumerable<string> operationRunIds)
    {
        Claims = claims;
        OperationIdentity = operationIdentity;
        _operationRunIds = operationRunIds.ToHashSet(StringComparer.Ordinal);
    }

    public IReadOnlyList<ProductionRunLeaseHoldClaim> Claims { get; }

    public string OperationIdentity { get; }

    public static ProductionRunLeaseHoldSet Create(
        IReadOnlyCollection<ProductionRunLeaseHold> leaseHolds)
    {
        ArgumentNullException.ThrowIfNull(leaseHolds);
        var supplied = leaseHolds.ToArray();
        if (supplied.Length == 0
            || supplied.Any(static hold => hold is null)
            || supplied.Select(static hold => hold.OperationRunId)
                .Distinct(StringComparer.Ordinal)
                .Count() != supplied.Length)
        {
            throw new ArgumentException(
                "Recovery lease holds must be a non-empty set with unique Operation Run ids.",
                nameof(leaseHolds));
        }

        var orderedHolds = supplied
            .OrderBy(static hold => hold.OperationRunId, StringComparer.Ordinal)
            .ToArray();
        var claims = orderedHolds
            .SelectMany(static hold => hold.Claims.Select(claim =>
                new ProductionRunLeaseHoldClaim(
                    hold.OperationRunId,
                    claim.Resource,
                    claim.FencingToken)))
            .OrderBy(static claim => claim.OperationRunId, StringComparer.Ordinal)
            .ThenBy(static claim => claim.Resource.CanonicalKey, StringComparer.Ordinal)
            .ToArray();
        if (claims.Length == 0
            || claims.Select(static claim => claim.Resource).Distinct().Count() != claims.Length)
        {
            throw new ArgumentException(
                "Recovery lease hold claims must be non-empty and unique across the complete batch.",
                nameof(leaseHolds));
        }

        return new ProductionRunLeaseHoldSet(
            claims,
            string.Join(",", orderedHolds.Select(static hold => hold.OperationRunId)),
            orderedHolds.Select(static hold => hold.OperationRunId));
    }

    public bool MatchesExactly(IEnumerable<ProductionRunLeaseHoldClaim> actualClaims)
    {
        ArgumentNullException.ThrowIfNull(actualClaims);
        var canonicalActual = actualClaims
            .OrderBy(static claim => claim.OperationRunId, StringComparer.Ordinal)
            .ThenBy(static claim => claim.Resource.CanonicalKey, StringComparer.Ordinal)
            .ToArray();
        return canonicalActual.Length == Claims.Count
               && canonicalActual.SequenceEqual(Claims);
    }

    public bool ContainsOperation(string operationRunId) =>
        _operationRunIds.Contains(operationRunId);

    public ResourceLeaseOwnershipException OwnershipException(
        ProductionRunId runId,
        string reason) =>
        new(runId, OperationIdentity, reason);
}

internal readonly record struct ProductionRunLeaseHoldClaim(
    string OperationRunId,
    ResourceRequirement Resource,
    long FencingToken);
