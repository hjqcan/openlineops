using OpenLineOps.Application.Abstractions.Time;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Stations;

namespace OpenLineOps.Runtime.Infrastructure.Persistence;

public sealed class InMemoryStationAgentControlLeaseRepository(IClock clock) :
    IStationAgentControlLeaseRepository
{
    private readonly IClock _clock =
        clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly object _gate = new();
    private readonly Dictionary<StationId, StationAgentControlLease> _leases = [];

    public ValueTask<StationAgentControlLeaseObservation> GetAsync(
        StationId stationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stationId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var observedAtUtc = ReadStoreUtcNow();
            _leases.TryGetValue(stationId, out var lease);
            EnsureClockDidNotMoveBackward(lease, observedAtUtc);
            return ValueTask.FromResult(
                new StationAgentControlLeaseObservation(lease, observedAtUtc));
        }
    }

    public ValueTask<StationAgentControlLeaseMutationResult> TryAcquireAsync(
        StationId stationId,
        string ownerAgentId,
        string ownerInstanceId,
        string leaseProofSha256,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(stationId, ownerAgentId, ownerInstanceId);
        _ = StationAgentControlLease.RequireProofSha256(
            leaseProofSha256,
            nameof(leaseProofSha256));
        ValidateDuration(duration);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var acquiredAtUtc = ReadStoreUtcNow();
            _leases.TryGetValue(stationId, out var current);
            EnsureClockDidNotMoveBackward(current, acquiredAtUtc);
            if (current?.IsActiveAt(acquiredAtUtc) == true)
            {
                return ValueTask.FromResult(new StationAgentControlLeaseMutationResult(
                    current.IsOwnedBy(ownerAgentId, ownerInstanceId)
                        ? current.MatchesProofSha256(leaseProofSha256)
                            ? StationAgentControlLeaseMutationStatus.AlreadyOwned
                            : StationAgentControlLeaseMutationStatus.StaleProof
                        : StationAgentControlLeaseMutationStatus.HeldByAnotherOwner,
                    current,
                    acquiredAtUtc));
            }

            var token = checked((current?.FencingToken ?? 0) + 1);
            var acquired = new StationAgentControlLease(
                stationId,
                ownerAgentId,
                ownerInstanceId,
                token,
                leaseProofSha256,
                acquiredAtUtc,
                acquiredAtUtc,
                acquiredAtUtc.Add(duration));
            _leases[stationId] = acquired;
            return ValueTask.FromResult(new StationAgentControlLeaseMutationResult(
                StationAgentControlLeaseMutationStatus.Acquired,
                acquired,
                acquiredAtUtc));
        }
    }

    public ValueTask<StationAgentControlLeaseMutationResult> TryRenewAsync(
        StationId stationId,
        string ownerAgentId,
        string ownerInstanceId,
        long fencingToken,
        string leaseProofSha256,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(stationId, ownerAgentId, ownerInstanceId);
        ArgumentOutOfRangeException.ThrowIfLessThan(fencingToken, 1);
        _ = StationAgentControlLease.RequireProofSha256(
            leaseProofSha256,
            nameof(leaseProofSha256));
        ValidateDuration(duration);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var renewedAtUtc = ReadStoreUtcNow();
            if (!_leases.TryGetValue(stationId, out var current))
            {
                return ValueTask.FromResult(Missing(renewedAtUtc));
            }

            EnsureClockDidNotMoveBackward(current, renewedAtUtc);
            var rejected = RejectStaleClaim(
                current,
                ownerAgentId,
                ownerInstanceId,
                fencingToken,
                leaseProofSha256,
                renewedAtUtc);
            if (rejected is not null)
            {
                return ValueTask.FromResult(rejected);
            }

            var renewed = new StationAgentControlLease(
                stationId,
                ownerAgentId,
                ownerInstanceId,
                fencingToken,
                current.LeaseProofSha256,
                current.AcquiredAtUtc,
                renewedAtUtc,
                renewedAtUtc.Add(duration));
            _leases[stationId] = renewed;
            return ValueTask.FromResult(new StationAgentControlLeaseMutationResult(
                StationAgentControlLeaseMutationStatus.Renewed,
                renewed,
                renewedAtUtc));
        }
    }

    public ValueTask<StationAgentControlLeaseValidationResult> ValidateGenerationAsync(
        StationId stationId,
        string ownerAgentId,
        string ownerInstanceId,
        long fencingToken,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(stationId, ownerAgentId, ownerInstanceId);
        ArgumentOutOfRangeException.ThrowIfLessThan(fencingToken, 1);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var observedAtUtc = ReadStoreUtcNow();
            _leases.TryGetValue(stationId, out var current);
            EnsureClockDidNotMoveBackward(current, observedAtUtc);
            var observation = new StationAgentControlLeaseObservation(
                current,
                observedAtUtc);
            return ValueTask.FromResult(new StationAgentControlLeaseValidationResult(
                observation.IsActive
                && current!.IsOwnedBy(ownerAgentId, ownerInstanceId)
                && current.FencingToken == fencingToken,
                observation));
        }
    }

    public ValueTask<StationAgentControlLeaseValidationResult> ValidateProofAsync(
        StationId stationId,
        string ownerAgentId,
        string ownerInstanceId,
        long fencingToken,
        string leaseProofSha256,
        CancellationToken cancellationToken = default)
    {
        _ = StationAgentControlLease.RequireProofSha256(
            leaseProofSha256,
            nameof(leaseProofSha256));
        var generation = ValidateGenerationAsync(
            stationId,
            ownerAgentId,
            ownerInstanceId,
            fencingToken,
            cancellationToken);
        if (!generation.IsCompletedSuccessfully)
        {
            return AwaitProofValidationAsync(generation, leaseProofSha256);
        }

        var result = generation.Result;
        return ValueTask.FromResult(new StationAgentControlLeaseValidationResult(
            result.IsValid
                && result.Observation.Lease!.MatchesProofSha256(
                    leaseProofSha256),
            result.Observation));
    }

    public ValueTask<StationAgentControlLeaseMutationResult> TryReleaseAsync(
        StationId stationId,
        string ownerAgentId,
        string ownerInstanceId,
        long fencingToken,
        string leaseProofSha256,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(stationId, ownerAgentId, ownerInstanceId);
        ArgumentOutOfRangeException.ThrowIfLessThan(fencingToken, 1);
        _ = StationAgentControlLease.RequireProofSha256(
            leaseProofSha256,
            nameof(leaseProofSha256));
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var releasedAtUtc = ReadStoreUtcNow();
            if (!_leases.TryGetValue(stationId, out var current))
            {
                return ValueTask.FromResult(Missing(releasedAtUtc));
            }

            EnsureClockDidNotMoveBackward(current, releasedAtUtc);
            var rejected = RejectStaleClaim(
                current,
                ownerAgentId,
                ownerInstanceId,
                fencingToken,
                leaseProofSha256,
                releasedAtUtc);
            if (rejected is not null)
            {
                return ValueTask.FromResult(rejected);
            }

            var released = new StationAgentControlLease(
                stationId,
                current.OwnerAgentId,
                current.OwnerInstanceId,
                current.FencingToken,
                current.LeaseProofSha256,
                current.AcquiredAtUtc,
                releasedAtUtc,
                releasedAtUtc);
            _leases[stationId] = released;
            return ValueTask.FromResult(new StationAgentControlLeaseMutationResult(
                StationAgentControlLeaseMutationStatus.Released,
                released,
                releasedAtUtc));
        }
    }

    private static StationAgentControlLeaseMutationResult? RejectStaleClaim(
        StationAgentControlLease current,
        string ownerAgentId,
        string ownerInstanceId,
        long fencingToken,
        string leaseProofSha256,
        DateTimeOffset observedAtUtc)
    {
        if (!current.IsActiveAt(observedAtUtc))
        {
            return new StationAgentControlLeaseMutationResult(
                StationAgentControlLeaseMutationStatus.Expired,
                current,
                observedAtUtc);
        }

        if (!current.IsOwnedBy(ownerAgentId, ownerInstanceId))
        {
            return new StationAgentControlLeaseMutationResult(
                StationAgentControlLeaseMutationStatus.StaleOwner,
                current,
                observedAtUtc);
        }

        if (!current.MatchesProofSha256(leaseProofSha256))
        {
            return new StationAgentControlLeaseMutationResult(
                StationAgentControlLeaseMutationStatus.StaleProof,
                current,
                observedAtUtc);
        }

        return current.FencingToken == fencingToken
            ? null
            : new StationAgentControlLeaseMutationResult(
                StationAgentControlLeaseMutationStatus.StaleFencingToken,
                current,
                observedAtUtc);
    }

    private static StationAgentControlLeaseMutationResult Missing(
        DateTimeOffset observedAtUtc) => new(
            StationAgentControlLeaseMutationStatus.NotFound,
            null,
            observedAtUtc);

    private static void ValidateRequest(
        StationId stationId,
        string ownerAgentId,
        string ownerInstanceId)
    {
        ArgumentNullException.ThrowIfNull(stationId);
        _ = StationAgentControlLease.RequireOwnerIdentity(
            ownerAgentId,
            nameof(ownerAgentId));
        _ = StationAgentControlLease.RequireOwnerInstanceId(
            ownerInstanceId,
            nameof(ownerInstanceId));
    }

    private static void ValidateDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                "Station Agent control lease duration must be positive and no "
                + "longer than five minutes.");
        }
    }

    private DateTimeOffset ReadStoreUtcNow()
    {
        var utcNow = _clock.UtcNow;
        return utcNow == default || utcNow.Offset != TimeSpan.Zero
            ? throw new InvalidOperationException(
                "Station Agent control lease store clock must return non-default UTC.")
            : utcNow;
    }

    private static void EnsureClockDidNotMoveBackward(
        StationAgentControlLease? current,
        DateTimeOffset observedAtUtc)
    {
        if (current is not null && observedAtUtc < current.RenewedAtUtc)
        {
            throw new InvalidOperationException(
                "Station Agent control lease store clock moved backward.");
        }
    }

    private static async ValueTask<StationAgentControlLeaseValidationResult>
        AwaitProofValidationAsync(
            ValueTask<StationAgentControlLeaseValidationResult> generation,
            string leaseProofSha256)
    {
        var result = await generation.ConfigureAwait(false);
        return new StationAgentControlLeaseValidationResult(
            result.IsValid
                && result.Observation.Lease!.MatchesProofSha256(
                    leaseProofSha256),
            result.Observation);
    }
}
