using System.Security.Cryptography;
using System.Text;
using OpenLineOps.Application.Abstractions.Results;
using OpenLineOps.Runtime.Application.Persistence;
using OpenLineOps.Runtime.Domain.Identifiers;
using OpenLineOps.Runtime.Domain.Stations;

namespace OpenLineOps.Runtime.Application.Stations;

public sealed class StationAgentControlLeaseOptions
{
    public const string SectionName =
        "OpenLineOps:Runtime:StationAgentControlLease";

    public TimeSpan TimeToLive { get; init; } = TimeSpan.FromSeconds(15);

    public void Validate()
    {
        if (TimeToLive < TimeSpan.FromSeconds(1)
            || TimeToLive > TimeSpan.FromMinutes(5))
        {
            throw new InvalidOperationException(
                $"{SectionName}:TimeToLive must be between 00:00:01 and 00:05:00.");
        }
    }
}

public interface IStationAgentControlLeaseValidator
{
    ValueTask<StationAgentControlLeaseValidationResult> ValidateAsync(
        StationId stationId,
        string ownerAgentId,
        string ownerInstanceId,
        long fencingToken,
        string leaseHandle,
        CancellationToken cancellationToken = default);
}

public sealed record StationAgentControlLeaseAcquisition(
    StationAgentControlLeaseObservation Observation);

public sealed class StationAgentControlLeaseService(
    IStationAgentControlLeaseRepository repository,
    StationAgentControlLeaseOptions options) :
    IStationAgentControlLeaseValidator
{
    public async ValueTask<Result<StationAgentControlLeaseObservation>> GetAsync(
        StationId stationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stationId);
        var observation = await repository.GetAsync(stationId, cancellationToken)
            .ConfigureAwait(false);
        return observation.Lease is null
            ? Result.Failure<StationAgentControlLeaseObservation>(
                ApplicationError.NotFound(
                    "Runtime.StationAgentControlLeaseNotFound",
                    $"Station Agent control lease {stationId} has not been acquired."))
            : Result.Success(observation);
    }

    public async ValueTask<Result<StationAgentControlLeaseAcquisition>> AcquireAsync(
        StationId stationId,
        string ownerAgentId,
        string ownerInstanceId,
        string leaseHandle,
        CancellationToken cancellationToken = default)
    {
        ValidateInput(stationId, ownerAgentId, ownerInstanceId);
        options.Validate();
        leaseHandle = RequireLeaseHandle(leaseHandle);
        var result = await repository.TryAcquireAsync(
                stationId,
                ownerAgentId,
                ownerInstanceId,
                HashLeaseHandle(leaseHandle),
                options.TimeToLive,
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Status is StationAgentControlLeaseMutationStatus.Acquired)
        {
            return Result.Success(new StationAgentControlLeaseAcquisition(
                new StationAgentControlLeaseObservation(
                    result.Lease,
                    result.ObservedAtUtc)));
        }

        if (result.Status is StationAgentControlLeaseMutationStatus.AlreadyOwned)
        {
            return Result.Success(new StationAgentControlLeaseAcquisition(
                new StationAgentControlLeaseObservation(
                    result.Lease,
                    result.ObservedAtUtc)));
        }

        var failure = ToResult(result, "acquire");
        return Result.Failure<StationAgentControlLeaseAcquisition>(failure.Error);
    }

    public async ValueTask<Result<StationAgentControlLeaseObservation>> RenewAsync(
        StationId stationId,
        string ownerAgentId,
        string ownerInstanceId,
        long fencingToken,
        string leaseHandle,
        CancellationToken cancellationToken = default)
    {
        ValidateInput(stationId, ownerAgentId, ownerInstanceId);
        ArgumentOutOfRangeException.ThrowIfLessThan(fencingToken, 1);
        options.Validate();
        var result = await repository.TryRenewAsync(
                stationId,
                ownerAgentId,
                ownerInstanceId,
                fencingToken,
                HashLeaseHandle(RequireLeaseHandle(leaseHandle)),
                options.TimeToLive,
                cancellationToken)
            .ConfigureAwait(false);
        return ToResult(result, "renew");
    }

    public async ValueTask<Result<StationAgentControlLeaseObservation>> ReleaseAsync(
        StationId stationId,
        string ownerAgentId,
        string ownerInstanceId,
        long fencingToken,
        string leaseHandle,
        CancellationToken cancellationToken = default)
    {
        ValidateInput(stationId, ownerAgentId, ownerInstanceId);
        ArgumentOutOfRangeException.ThrowIfLessThan(fencingToken, 1);
        var result = await repository.TryReleaseAsync(
                stationId,
                ownerAgentId,
                ownerInstanceId,
                fencingToken,
                HashLeaseHandle(RequireLeaseHandle(leaseHandle)),
                cancellationToken)
            .ConfigureAwait(false);
        return ToResult(result, "release");
    }

    public ValueTask<StationAgentControlLeaseValidationResult> ValidateAsync(
        StationId stationId,
        string ownerAgentId,
        string ownerInstanceId,
        long fencingToken,
        string leaseHandle,
        CancellationToken cancellationToken = default)
    {
        ValidateInput(stationId, ownerAgentId, ownerInstanceId);
        ArgumentOutOfRangeException.ThrowIfLessThan(fencingToken, 1);
        return repository.ValidateProofAsync(
            stationId,
            ownerAgentId,
            ownerInstanceId,
            fencingToken,
            HashLeaseHandle(RequireLeaseHandle(leaseHandle)),
            cancellationToken);
    }

    private static Result<StationAgentControlLeaseObservation> ToResult(
        StationAgentControlLeaseMutationResult mutation,
        string operation)
    {
        if (mutation.Succeeded)
        {
            return Result.Success(new StationAgentControlLeaseObservation(
                mutation.Lease,
                mutation.ObservedAtUtc));
        }

        var error = mutation.Status switch
        {
            StationAgentControlLeaseMutationStatus.NotFound =>
                ApplicationError.NotFound(
                    "Runtime.StationAgentControlLeaseNotFound",
                    "The station Agent control lease has not been acquired."),
            StationAgentControlLeaseMutationStatus.HeldByAnotherOwner =>
                ApplicationError.Conflict(
                    "Runtime.StationAgentControlLeaseHeld",
                    "Another Agent instance owns the active station control lease."),
            StationAgentControlLeaseMutationStatus.Expired =>
                ApplicationError.Conflict(
                    "Runtime.StationAgentControlLeaseExpired",
                    "The station Agent control lease expired; acquire a new lease "
                    + "and fencing token."),
            StationAgentControlLeaseMutationStatus.StaleOwner =>
                ApplicationError.Conflict(
                    "Runtime.StationAgentControlLeaseStaleOwner",
                    "The station Agent control lease is owned by another Agent instance."),
            StationAgentControlLeaseMutationStatus.StaleFencingToken =>
                ApplicationError.Conflict(
                    "Runtime.StationAgentControlLeaseStaleFencingToken",
                    "The supplied station Agent control fencing token is stale."),
            StationAgentControlLeaseMutationStatus.StaleProof =>
                ApplicationError.Conflict(
                    "Runtime.StationAgentControlLeaseStaleProof",
                    "The supplied station Agent control lease proof is stale."),
            _ => throw new InvalidOperationException(
                $"Lease {operation} returned unexpected status {mutation.Status}.")
        };
        return Result.Failure<StationAgentControlLeaseObservation>(error);
    }

    private static void ValidateInput(
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

    private static string RequireLeaseHandle(string value)
    {
        if (value is null
            || value.Length != 43
            || value.Any(static character =>
                !(char.IsAsciiLetterOrDigit(character)
                    || character is '-' or '_')))
        {
            throw new ArgumentException(
                "Station Agent control lease handle must be a 256-bit "
                + "base64url capability.",
                nameof(value));
        }

        return value;
    }

    private static string HashLeaseHandle(string leaseHandle) =>
        Convert.ToHexString(
                SHA256.HashData(Encoding.ASCII.GetBytes(leaseHandle)))
            .ToLowerInvariant();
}
