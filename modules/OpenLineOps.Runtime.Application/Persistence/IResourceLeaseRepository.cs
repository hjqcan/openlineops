using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Resources;

namespace OpenLineOps.Runtime.Application.Persistence;

public interface IResourceLeaseRepository
{
    ValueTask<IReadOnlyCollection<ResourceLease>> ListAsync(
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyCollection<ResourceLease>?> TryAcquireAsync(
        ProductionRunId runId,
        string operationRunId,
        IReadOnlyCollection<ResourceRequirement> resources,
        DateTimeOffset acquiredAtUtc,
        TimeSpan duration,
        CancellationToken cancellationToken = default);

    ValueTask<ResourceLeaseFenceValidationResult> ValidateCurrentAsync(
        ProductionRunId runId,
        string operationRunId,
        IReadOnlyCollection<ResourceLeaseFenceEvidence> evidence,
        DateTimeOffset validatedAtUtc,
        CancellationToken cancellationToken = default);

    ValueTask ReleaseAsync(
        ProductionRunId runId,
        string operationRunId,
        CancellationToken cancellationToken = default);

    ValueTask HoldForRecoveryAsync(
        ProductionRunId runId,
        string operationRunId,
        CancellationToken cancellationToken = default);
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
