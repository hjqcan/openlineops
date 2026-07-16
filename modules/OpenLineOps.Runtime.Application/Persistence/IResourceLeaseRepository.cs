using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Resources;

namespace OpenLineOps.Runtime.Application.Persistence;

public interface IResourceLeaseRepository
{
    ValueTask<IReadOnlyCollection<ResourceLease>> ListAsync(
        CancellationToken cancellationToken = default);

    // Lease time is repository-authoritative. Callers declare only the duration and must
    // never supply a wall-clock value used for expiry or fencing decisions.
    ValueTask<IReadOnlyCollection<ResourceLease>?> TryAcquireAsync(
        ProductionRunId runId,
        string operationRunId,
        IReadOnlyCollection<ResourceRequirement> resources,
        TimeSpan duration,
        CancellationToken cancellationToken = default);

    ValueTask<ResourceLeaseFenceValidationResult> ValidateCurrentAsync(
        ProductionRunId runId,
        string operationRunId,
        IReadOnlyCollection<ResourceLeaseFenceEvidence> evidence,
        CancellationToken cancellationToken = default);

    ValueTask ReleaseAsync(
        ProductionRunId runId,
        string operationRunId,
        IReadOnlyCollection<ResourceLeaseReleaseClaim> claims,
        CancellationToken cancellationToken = default);

    ValueTask HoldForRecoveryAsync(
        ProductionRunId runId,
        IReadOnlyCollection<ProductionRunLeaseHold> leaseHolds,
        CancellationToken cancellationToken = default);

    ValueTask ReleaseRecoveryHoldAsync(
        ProductionRunId runId,
        IReadOnlyCollection<ProductionRunLeaseHold> leaseHolds,
        CancellationToken cancellationToken = default);
}

public sealed class ResourceLeaseOwnershipException : InvalidOperationException
{
    public ResourceLeaseOwnershipException(
        ProductionRunId runId,
        string operationRunId,
        string reason)
        : base(
            $"Resource leases for Production Run {runId}, Operation Run "
            + $"'{operationRunId}' could not be held exactly: {reason}")
    {
        RunId = runId;
        OperationRunId = operationRunId;
    }

    public ProductionRunId RunId { get; }

    public string OperationRunId { get; }
}

public sealed record ResourceLeaseFenceValidationResult(
    bool Accepted,
    string? RejectionReason)
{
    public static ResourceLeaseFenceValidationResult Accept() => new(true, null);

    public static ResourceLeaseFenceValidationResult Reject(string reason) =>
        new(false, string.IsNullOrWhiteSpace(reason)
            ? throw new ArgumentException("Resource lease rejection reason is required.", nameof(reason))
            : reason);
}
