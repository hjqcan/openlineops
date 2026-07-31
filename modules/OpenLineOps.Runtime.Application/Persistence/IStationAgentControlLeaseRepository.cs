using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Stations;

namespace OpenLineOps.Runtime.Application.Persistence;

public interface IStationAgentControlLeaseRepository
{
    ValueTask<StationAgentControlLeaseObservation> GetAsync(
        StationId stationId,
        CancellationToken cancellationToken = default);

    ValueTask<StationAgentControlLeaseMutationResult> TryAcquireAsync(
        StationId stationId,
        string ownerAgentId,
        string ownerInstanceId,
        string leaseProofSha256,
        TimeSpan duration,
        CancellationToken cancellationToken = default);

    ValueTask<StationAgentControlLeaseMutationResult> TryRenewAsync(
        StationId stationId,
        string ownerAgentId,
        string ownerInstanceId,
        long fencingToken,
        string leaseProofSha256,
        TimeSpan duration,
        CancellationToken cancellationToken = default);

    ValueTask<StationAgentControlLeaseValidationResult> ValidateGenerationAsync(
        StationId stationId,
        string ownerAgentId,
        string ownerInstanceId,
        long fencingToken,
        CancellationToken cancellationToken = default);

    ValueTask<StationAgentControlLeaseValidationResult> ValidateProofAsync(
        StationId stationId,
        string ownerAgentId,
        string ownerInstanceId,
        long fencingToken,
        string leaseProofSha256,
        CancellationToken cancellationToken = default);

    ValueTask<StationAgentControlLeaseMutationResult> TryReleaseAsync(
        StationId stationId,
        string ownerAgentId,
        string ownerInstanceId,
        long fencingToken,
        string leaseProofSha256,
        CancellationToken cancellationToken = default);
}

public sealed record StationAgentControlLeaseObservation
{
    public StationAgentControlLeaseObservation(
        StationAgentControlLease? lease,
        DateTimeOffset observedAtUtc)
    {
        if (observedAtUtc == default || observedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Lease observation time must be a non-default UTC timestamp.",
                nameof(observedAtUtc));
        }

        Lease = lease;
        ObservedAtUtc = observedAtUtc;
    }

    public StationAgentControlLease? Lease { get; }

    public DateTimeOffset ObservedAtUtc { get; }

    public bool IsActive => Lease?.IsActiveAt(ObservedAtUtc) == true;
}

public enum StationAgentControlLeaseMutationStatus
{
    Acquired = 1,
    AlreadyOwned = 2,
    Renewed = 3,
    Released = 4,
    NotFound = 5,
    HeldByAnotherOwner = 6,
    Expired = 7,
    StaleOwner = 8,
    StaleFencingToken = 9,
    StaleProof = 10
}

public sealed record StationAgentControlLeaseMutationResult
{
    public StationAgentControlLeaseMutationResult(
        StationAgentControlLeaseMutationStatus status,
        StationAgentControlLease? lease,
        DateTimeOffset observedAtUtc)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (observedAtUtc == default || observedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Lease mutation time must be a non-default UTC timestamp.",
                nameof(observedAtUtc));
        }

        if (status is not StationAgentControlLeaseMutationStatus.NotFound
            && lease is null)
        {
            throw new ArgumentNullException(nameof(lease));
        }

        Status = status;
        Lease = lease;
        ObservedAtUtc = observedAtUtc;
    }

    public StationAgentControlLeaseMutationStatus Status { get; }

    public StationAgentControlLease? Lease { get; }

    public DateTimeOffset ObservedAtUtc { get; }

    public bool Succeeded => Status is
        StationAgentControlLeaseMutationStatus.Acquired
        or StationAgentControlLeaseMutationStatus.AlreadyOwned
        or StationAgentControlLeaseMutationStatus.Renewed
        or StationAgentControlLeaseMutationStatus.Released;
}

public sealed record StationAgentControlLeaseValidationResult(
    bool IsValid,
    StationAgentControlLeaseObservation Observation);
