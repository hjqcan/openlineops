using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Resources;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

public sealed class InMemoryResourceLeaseRepository(IClock clock) : IResourceLeaseRepository
{
    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly object _gate = new();
    private readonly Dictionary<ResourceRequirement, ResourceLease> _leases = [];
    private readonly Dictionary<ResourceRequirement, long> _fencingTokens = [];

    internal object CoordinationGate => _gate;

    internal Dictionary<ResourceRequirement, ResourceLease> CaptureCoordinationSnapshot()
    {
        lock (_gate)
        {
            return new Dictionary<ResourceRequirement, ResourceLease>(_leases);
        }
    }

    internal void RestoreCoordinationSnapshot(
        IReadOnlyDictionary<ResourceRequirement, ResourceLease> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            _leases.Clear();
            foreach (var pair in snapshot)
            {
                _leases.Add(pair.Key, pair.Value);
            }
        }
    }

    public ValueTask<IReadOnlyCollection<ResourceLease>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult<IReadOnlyCollection<ResourceLease>>(
                _leases.Values
                    .OrderBy(static lease => lease.Resource.CanonicalKey, StringComparer.Ordinal)
                    .ToArray());
        }
    }

    public ValueTask<IReadOnlyCollection<ResourceLease>?> TryAcquireAsync(
        ProductionRunId runId,
        string operationRunId,
        IReadOnlyCollection<ResourceRequirement> resources,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationRunId);
        ArgumentNullException.ThrowIfNull(resources);
        cancellationToken.ThrowIfCancellationRequested();
        if (resources.Count == 0 || duration <= TimeSpan.Zero)
        {
            throw new ArgumentException("Resource acquisition requires resources and a positive duration.");
        }

        lock (_gate)
        {
            var acquiredAtUtc = ReadStoreUtcNow();
            var requested = resources.Distinct().ToArray();
            if (requested.Length != resources.Count)
            {
                throw new ArgumentException("Resource lease requests must be unique.", nameof(resources));
            }

            foreach (var resource in requested)
            {
                if (_leases.TryGetValue(resource, out var lease)
                    && lease.ExpiresAtUtc > acquiredAtUtc)
                {
                    return ValueTask.FromResult<IReadOnlyCollection<ResourceLease>?>(null);
                }
            }

            var acquired = new List<ResourceLease>(requested.Length);
            foreach (var resource in requested)
            {
                var token = checked(_fencingTokens.GetValueOrDefault(resource) + 1);
                _fencingTokens[resource] = token;
                var lease = new ResourceLease(
                    resource,
                    runId,
                    operationRunId,
                    token,
                    acquiredAtUtc,
                    acquiredAtUtc.Add(duration));
                _leases[resource] = lease;
                acquired.Add(lease);
            }

            return ValueTask.FromResult<IReadOnlyCollection<ResourceLease>?>(acquired);
        }
    }

    public ValueTask<ResourceLeaseFenceValidationResult> ValidateCurrentAsync(
        ProductionRunId runId,
        string operationRunId,
        IReadOnlyCollection<ResourceLeaseFenceEvidence> evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationRunId);
        ArgumentNullException.ThrowIfNull(evidence);
        cancellationToken.ThrowIfCancellationRequested();
        var supplied = evidence.ToArray();
        if (supplied.Length == 0
            || supplied.Any(static item => item is null)
            || supplied.Select(static item => item.Resource).Distinct().Count() != supplied.Length)
        {
            throw new ArgumentException(
                "Resource lease validation requires non-empty unique evidence.",
                nameof(evidence));
        }

        lock (_gate)
        {
            var validatedAtUtc = ReadStoreUtcNow();
            foreach (var item in supplied)
            {
                if (!_fencingTokens.TryGetValue(item.Resource, out var currentToken)
                    || currentToken != item.FencingToken
                    || !_leases.TryGetValue(item.Resource, out var lease)
                    || lease.ProductionRunId != runId
                    || !string.Equals(lease.OperationRunId, operationRunId, StringComparison.Ordinal)
                    || lease.FencingToken != item.FencingToken
                    || lease.ExpiresAtUtc != item.ExpiresAtUtc
                    || lease.ExpiresAtUtc <= validatedAtUtc)
                {
                    return ValueTask.FromResult(ResourceLeaseFenceValidationResult.Reject(
                        $"Resource lease fence {item.Resource.CanonicalKey}/{item.FencingToken} is missing, stale, expired, or owned by another Operation Run."));
                }
            }

            return ValueTask.FromResult(ResourceLeaseFenceValidationResult.Accept());
        }
    }

    public ValueTask ReleaseAsync(
        ProductionRunId runId,
        string operationRunId,
        IReadOnlyCollection<ResourceLeaseReleaseClaim> claims,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationRunId);
        ArgumentNullException.ThrowIfNull(claims);
        cancellationToken.ThrowIfCancellationRequested();
        var supplied = claims.ToArray();
        if (supplied.Any(static claim => claim is null)
            || supplied.Select(static claim => claim.Resource).Distinct().Count() != supplied.Length)
        {
            throw new ArgumentException(
                "Resource lease release claims must be unique.",
                nameof(claims));
        }

        lock (_gate)
        {
            foreach (var claim in supplied)
            {
                if (_leases.TryGetValue(claim.Resource, out var lease)
                    && lease.ProductionRunId == runId
                    && string.Equals(
                        lease.OperationRunId,
                        operationRunId,
                        StringComparison.Ordinal)
                    && lease.FencingToken == claim.FencingToken
                    && lease.ExpiresAtUtc != DateTimeOffset.MaxValue)
                {
                    _leases.Remove(claim.Resource);
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ReleaseRecoveryHoldAsync(
        ProductionRunId runId,
        IReadOnlyCollection<ProductionRunLeaseHold> leaseHolds,
        CancellationToken cancellationToken = default)
    {
        var expected = ProductionRunLeaseHoldSet.Create(leaseHolds);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var owned = _leases
                .Where(pair => pair.Value.ProductionRunId == runId
                               && expected.ContainsOperation(pair.Value.OperationRunId))
                .ToArray();
            if (!expected.MatchesExactly(owned.Select(static pair =>
                    new ProductionRunLeaseHoldClaim(
                        pair.Value.OperationRunId,
                        pair.Key,
                        pair.Value.FencingToken))))
            {
                throw expected.OwnershipException(
                    runId,
                    owned.Length == 0
                        ? "no current recovery-held lease rows are owned by the Production Run"
                        : "the complete current recovery-held owner set differs from the supplied fencing claims");
            }

            var invalid = owned.FirstOrDefault(pair =>
                pair.Value.ExpiresAtUtc != DateTimeOffset.MaxValue
                || !_fencingTokens.TryGetValue(pair.Key, out var currentToken)
                || currentToken != pair.Value.FencingToken);
            if (invalid.Value is not null)
            {
                throw expected.OwnershipException(
                    runId,
                    $"{invalid.Key.CanonicalKey} is not an exact current recovery hold");
            }

            foreach (var pair in owned)
            {
                _leases.Remove(pair.Key);
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask HoldForRecoveryAsync(
        ProductionRunId runId,
        IReadOnlyCollection<ProductionRunLeaseHold> leaseHolds,
        CancellationToken cancellationToken = default)
    {
        var expected = ProductionRunLeaseHoldSet.Create(leaseHolds);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var owned = _leases
                .Where(pair => pair.Value.ProductionRunId == runId
                    && expected.ContainsOperation(pair.Value.OperationRunId))
                .ToArray();
            if (!expected.MatchesExactly(owned.Select(static pair =>
                    new ProductionRunLeaseHoldClaim(
                        pair.Value.OperationRunId,
                        pair.Key,
                        pair.Value.FencingToken))))
            {
                throw expected.OwnershipException(
                    runId,
                    owned.Length == 0
                        ? "no current lease rows are owned by the Production Run"
                        : "a supplied durable Operation Run lease set differs from its fencing claims");
            }

            var stale = owned.FirstOrDefault(pair =>
                !_fencingTokens.TryGetValue(pair.Key, out var currentToken)
                || currentToken != pair.Value.FencingToken);
            if (stale.Value is not null)
            {
                throw expected.OwnershipException(
                    runId,
                    $"the permanent fence for {stale.Key.CanonicalKey} is stale");
            }

            foreach (var pair in owned)
            {
                var lease = pair.Value;
                _leases[pair.Key] = new ResourceLease(
                    lease.Resource,
                    lease.ProductionRunId,
                    lease.OperationRunId,
                    lease.FencingToken,
                    lease.AcquiredAtUtc,
                    DateTimeOffset.MaxValue);
            }
        }

        return ValueTask.CompletedTask;
    }

    private DateTimeOffset ReadStoreUtcNow()
    {
        var utcNow = _clock.UtcNow;
        return utcNow == default || utcNow.Offset != TimeSpan.Zero
            ? throw new InvalidOperationException(
                "Resource lease store clock must return non-default UTC.")
            : utcNow;
    }
}
