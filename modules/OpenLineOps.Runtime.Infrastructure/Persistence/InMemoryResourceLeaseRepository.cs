using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Resources;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

public sealed class InMemoryResourceLeaseRepository : IResourceLeaseRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<ResourceRequirement, ResourceLease> _leases = [];
    private readonly Dictionary<ResourceRequirement, long> _fencingTokens = [];

    public ValueTask<IReadOnlyCollection<ResourceLease>?> TryAcquireAsync(
        ProductionRunId runId,
        string operationRunId,
        IReadOnlyCollection<ResourceRequirement> resources,
        DateTimeOffset acquiredAtUtc,
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
            var requested = resources.Distinct().ToArray();
            if (requested.Length != resources.Count)
            {
                throw new ArgumentException("Resource lease requests must be unique.", nameof(resources));
            }

            foreach (var resource in requested)
            {
                if (_leases.TryGetValue(resource, out var lease)
                    && lease.ExpiresAtUtc > acquiredAtUtc
                    && (lease.ProductionRunId != runId
                        || !string.Equals(
                            lease.OperationRunId,
                            operationRunId,
                            StringComparison.Ordinal)))
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

    public ValueTask ReleaseAsync(
        ProductionRunId runId,
        string operationRunId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            foreach (var resource in _leases
                         .Where(pair => pair.Value.ProductionRunId == runId
                             && string.Equals(
                                 pair.Value.OperationRunId,
                                 operationRunId,
                                 StringComparison.Ordinal))
                         .Select(static pair => pair.Key)
                         .ToArray())
            {
                _leases.Remove(resource);
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask HoldForRecoveryAsync(
        ProductionRunId runId,
        string operationRunId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            foreach (var pair in _leases.Where(pair =>
                         pair.Value.ProductionRunId == runId
                         && string.Equals(
                             pair.Value.OperationRunId,
                             operationRunId,
                             StringComparison.Ordinal)).ToArray())
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
}
